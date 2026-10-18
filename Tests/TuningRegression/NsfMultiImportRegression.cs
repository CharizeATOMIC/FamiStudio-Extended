using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using FamiStudio;

static class NsfMultiImportRegression
{
    private sealed class CancelCapture : ILogOutput
    {
        public bool AbortOperation { get; private set; }
        public void LogMessage(string message) { }
        public void ReportProgress(float fraction) { if (fraction >= 0.1f) AbortOperation = true; }
    }

    public static string FixturePath(string output) => Path.Combine(output, "multi-import.nsfe");

    private static void MakeFixture(string path, Action<bool, string> check)
    {
        var p = new Project { AutoSortSongs = false, Name = "Multi import regression", Author = "Test" };
        p.SetExpansionAudioMask(ExpansionType.FdsMask);
        var first = p.CreateInstrument(ExpansionType.Fds, "Wave one");
        var second = p.CreateInstrument(ExpansionType.Fds, "Wave two");
        foreach (var instrument in new[] { first, second }) instrument.FdsWavePreset = WavePresetType.Custom;
        for (int i = 0; i < 64; i++)
        {
            first.FdsWaveformEnvelope.Values[i] = (sbyte)i;
            second.FdsWaveformEnvelope.Values[i] = (sbyte)(i < 32 ? 0 : 63);
        }
        var sampleA = p.CreateDPCMSampleFromDmcData("DMC A", Enumerable.Repeat((byte)0x33, 257).ToArray());
        var sampleB = p.CreateDPCMSampleFromDmcData("DMC B", Enumerable.Repeat((byte)0xcc, 257).ToArray());
        var dpcmA = p.CreateInstrument(ExpansionType.None, "DPCM A");
        var dpcmB = p.CreateInstrument(ExpansionType.None, "DPCM B");
        dpcmA.MapDPCMSample(49, sampleA, 15, true);
        dpcmB.MapDPCMSample(49, sampleB, 15, true);
        for (int index = 0; index < 3; index++)
        {
            var song = p.CreateSong("Track " + (index + 1));
            song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
            song.SetDefaultPatternLength(128); song.SetLength(1); song.SetLoopPoint(0);
            song.GetChannelByType(ChannelType.FdsWave).GetOrCreatePattern(0).Notes[0] = new Note((byte)(46 + index)) { Instrument = index == 1 ? second : first, Duration = 128, Volume = 12 };
            song.GetChannelByType(ChannelType.Dpcm).GetOrCreatePattern(0).Notes[0] = new Note(49) { Instrument = index == 1 ? dpcmB : dpcmA, Duration = 128 };
        }
        check(new NsfFile().Save(p, FamiToneKernel.FamiStudio, path, p.Songs.Select(s => s.Id).ToArray(), p.Name, p.Author, "", MachineType.NTSC, true), "multi-import fixture exports NSFE with distinct waves and samples");

        // NSFE permits duplicate display names. They must not become Project.MergeProject collisions.
        var bytes = File.ReadAllBytes(path);
        using var output = new MemoryStream();
        output.Write(bytes, 0, 4);
        for (int offset = 4; offset < bytes.Length;)
        {
            int length = BitConverter.ToInt32(bytes, offset);
            string type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
            byte[] data = type == "tlbl" ? Encoding.UTF8.GetBytes("Repeated\0Repeated\0Last\0") : bytes.AsSpan(offset + 8, length).ToArray();
            output.Write(BitConverter.GetBytes(data.Length)); output.Write(bytes, offset + 4, 4); output.Write(data);
            offset += length + 8;
        }
        File.WriteAllBytes(path, output.ToArray());
    }

    private static Note FirstNote(Song song, int type) => song.GetChannelByType(type).Patterns
        .SelectMany(p => p.Notes.Values).First(n => n.IsMusical && n.Instrument != null);

    private static void CheckReferences(Project project, Action<bool, string> check)
    {
        check(project.Songs.All(s => ReferenceEquals(s.Project, project)), "all imported songs belong to the merged project");
        check(project.Songs.SelectMany(s => s.Channels).SelectMany(c => c.Patterns).SelectMany(p => p.Notes.Values)
            .Where(n => n.Instrument != null).All(n => project.Instruments.Contains(n.Instrument)), "all note instruments remain owned by the merged project");
        check(project.Instruments.Where(i => i.SamplesMapping != null).SelectMany(i => i.SamplesMapping.Values)
            .All(m => m.Sample == null || project.Samples.Contains(m.Sample)), "all imported DPCM mappings point to retained samples");
    }

    public static void Run(string output, Action<bool, string> check)
    {
        Directory.CreateDirectory(output);
        var path = FixturePath(output); MakeFixture(path, check);
        var names = NsfFile.GetSongNamesAndDurations(path, out var durations);
        check(names.SequenceEqual(new[] { "Repeated", "Repeated", "Last" }), "NSFE duplicate display titles are read independently");
        var reader = new NsfFile();
        var result = reader.LoadSongs(path, new[] { 0, 1, 2 }, new[] { 1, 2, 1 }, 64, 0, true, false, false, true);
        check(result != null && result.Songs.Count == 3, "three selected NSF tracks import into one project");
        check(result.Songs.Select(s => s.Name).SequenceEqual(new[] { "Repeated", "Repeated (2)", "Last" }), "track order and duplicate titles survive batch import");
        check(result.Songs[1].Length > result.Songs[0].Length, "each selected track has an independent capture duration");
        CheckReferences(result, check);
        var fds = result.Songs.Select(s => FirstNote(s, ChannelType.FdsWave).Instrument).ToArray();
        check(!ReferenceEquals(fds[0], fds[1]) && !fds[0].FdsWaveformEnvelope.Values.SequenceEqual(fds[1].FdsWaveformEnvelope.Values), "colliding generated FDS instrument names preserve different waveforms");
        check(ReferenceEquals(fds[0], fds[2]), "identical imported wave instruments are shared across songs");
        var dpcm = result.Songs.Select(s => FirstNote(s, ChannelType.Dpcm)).ToArray();
        var samples = dpcm.Select(n => n.Instrument.GetDPCMMapping(n.Value).Sample).ToArray();
        check(!ReferenceEquals(samples[0], samples[1]) && !samples[0].ProcessedData.SequenceEqual(samples[1].ProcessedData), "colliding sample names preserve different DPCM bytes");
        check(ReferenceEquals(samples[0], samples[2]) && ReferenceEquals(dpcm[0].Instrument, dpcm[2].Instrument), "identical samples and their mappings are shared safely");
        check(!ReferenceEquals(dpcm[0].Instrument, dpcm[1].Instrument), "DPCM instruments with different sample references are not merged by CRC");
        for (int track = 0; track < 3; track++)
        {
            var independent = new NsfFile().Load(path, track, track == 1 ? 2 : 1, 64, 0, true, false, false, true);
            var a = new CapturePlayer(false, false); a.Render(independent.Songs[0]);
            var b = new CapturePlayer(false, false); b.Render(result.Songs[track]);
            // Filter history in the emulator is not reset between captures. Compare the source
            // register state instead; a sample address may move when the shared DPCM bank grows.
            check(a.Frames.Count == b.Frames.Count && a.Frames.Zip(b.Frames).All(pair =>
                pair.First.Where(kv => kv.Key != 0x4012).OrderBy(kv => kv.Key)
                .SequenceEqual(pair.Second.Where(kv => kv.Key != 0x4012).OrderBy(kv => kv.Key))),
                "merged track " + track + " register state matches independent import at every frame");
        }
        string saved = Path.Combine(output, "multi-import.fms");
        check(new ProjectFile().Save(result, saved), "multi-track imported project saves to FMS");
        var restored = new ProjectFile().Load(saved);
        check(restored.Songs.Count == 3 && restored.Samples.Count == result.Samples.Count, "multi-track imported FMS reloads songs and resources");
        CheckReferences(restored, check);
        var subset = reader.LoadSongs(path, new[] { 2, 0, 2 }, new[] { 1, 1, 1 }, 64, 0, true, false, false, true);
        check(subset.Songs.Count == 2 && subset.Songs[0].Name == "Last" && subset.Songs[1].Name == "Repeated", "noncontiguous selection preserves requested order and ignores repeated indices");
        check(reader.LoadSongs(path, Array.Empty<int>(), Array.Empty<int>(), 64, 0, true, false, false, true) == null, "empty selection returns no project");
        check(reader.LoadSongs(path, new[] { 0, 3 }, new[] { 1, 1 }, 64, 0, true, false, false, true) == null, "invalid selection rejects the entire import before capture");
        check(reader.Load(path, names.Length, 1, 64, 0, true, false, false, true) == null, "one-past-end track index is rejected");
        // Four-frame patterns reach the project's timeline limit quickly, while still exercising
        // the same unbounded-duration single-song entry point used by the existing CLI.
        check(reader.Load(path, 0, 601, 4, 0, false, false, false, true) != null, "single-song CLI import retains support for durations above 600 seconds");
        check(reader.LoadSongs(path, new[] { 0 }, new[] { 601 }, 64, 0, true, false, false, true) == null, "multi-song UI duration remains limited to 600 seconds per song");
        for (int length = 1; length <= 3; length++)
        {
            var tiny = reader.Load(path, 0, 1, length, 0, false, false, false, true);
            check(tiny != null && tiny.Songs[0].PatternLength == length && FirstNote(tiny.Songs[0], ChannelType.FdsWave).Instrument != null,
                "single-song CLI captures musical notes with legacy pattern length " + length);
        }
        const int shiftedStart = 2048;
        var shifted = reader.Load(path, 0, 1, 64, shiftedStart, false, false, false, true);
        var shiftedChannel = shifted?.Songs[0].GetChannelByType(ChannelType.FdsWave);
        check(shiftedChannel != null && shiftedChannel.PatternInstances.Take(shiftedStart / 64).All(p => p == null) &&
            shiftedChannel.PatternInstances[shiftedStart / 64]?.Notes.Values.Any(n => n.IsMusical) == true,
            "single-song CLI retains capture placement beyond the GUI start-frame limit");
        int timelineLimit = (Song.MaxLength - 1) * 64;
        var lastFrame = reader.Load(path, 0, 1, 64, timelineLimit - 1, false, false, false, true);
        check(lastFrame != null && lastFrame.Songs[0].Length <= Song.MaxLength &&
            lastFrame.Songs[0].GetChannelByType(ChannelType.FdsWave).PatternInstances[Song.MaxLength - 2]?.Notes.Values.Any(n => n.IsMusical) == true,
            "single-song CLI captures the last representable starting frame safely");
        check(reader.Load(path, 0, 1, 64, timelineLimit, false, false, false, true) == null &&
            reader.Load(path, 0, 1, 64, int.MaxValue, false, false, false, true) == null &&
            reader.Load(path, 0, 1, 0, 0, false, false, false, true) == null &&
            reader.Load(path, 0, 1, Pattern.MaxLength + 1, 0, false, false, false, true) == null,
            "unrepresentable start positions and pattern sizes reject before capture");
        check(reader.Load(path, 0, int.MaxValue, 64, 0, false, false, false, true) == null &&
            (IntPtr)typeof(NsfFile).GetField("nsf", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(reader) == IntPtr.Zero,
            "duration-to-frame overflow rejects safely and releases the native NSF handle");
        check(reader.LoadSongs(path, new[] { 0 }, new[] { 1 }, 3, 0, false, false, false, true) == null &&
            reader.LoadSongs(path, new[] { 0 }, new[] { 1 }, 64, shiftedStart, false, false, false, true) == null,
            "multi-song UI retains its pattern and starting-frame bounds separately from CLI");
        var oldLog = Log.LogOutput;
        try
        {
            Log.LogOutput = new CancelCapture();
            check(reader.LoadSongs(path, new[] { 0, 1 }, new[] { 2, 2 }, 64, 0, true, false, false, true) == null, "cancel during capture discards the whole batch");
            Log.LogOutput = new CancelCapture();
            check(reader.Load(path, 0, 2, 64, 0, true, false, false, true) == null &&
                (IntPtr)typeof(NsfFile).GetField("nsf", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(reader) == IntPtr.Zero,
                "cancelling single-track capture releases its native NSF handle");
        }
        finally { Log.LogOutput = oldLog; }
        check(reader.Load(path, 0, 1, 64, 0, true, false, false, true) != null, "importer can load again after invalid selection or cancellation");
        CheckN163(output, check);
    }

    private static void CheckN163(string output, Action<bool, string> check)
    {
        // A tiny independent NSF switches the hardware channel count in INIT, one versus four.
        var bytes = new byte[128 + 32];
        Encoding.ASCII.GetBytes("NESM\x1a").CopyTo(bytes, 0);
        bytes[5] = 1; bytes[6] = 2; bytes[7] = 1;
        bytes[9] = bytes[11] = bytes[13] = 0x80; bytes[12] = 20;
        bytes[110] = 0xff; bytes[111] = 0x40; bytes[123] = 0x10;
        new byte[] { 0xc9, 0x00, 0xf0, 0x02, 0xa9, 0x30, 0xaa, 0xa9, 0x7f, 0x8d, 0x00, 0xf8, 0x8a, 0x8d, 0x00, 0x48, 0x60 }.CopyTo(bytes, 128);
        bytes[128 + 20] = 0x60;
        var path = Path.Combine(output, "channel-count.nsf"); File.WriteAllBytes(path, bytes);
        var result = new NsfFile().LoadSongs(path, new[] { 0, 1 }, new[] { 1, 1 }, 64, 0, false, false, false, true);
        check(result != null && result.ExpansionNumN163Channels == 4 && result.Songs.All(s => s.GetChannelByType(ChannelType.N163Wave4) != null), "N163 batch import retains the maximum channel count across selected tracks");
    }

    public static void Ui(FamiStudioWindow window, string output, Action<bool, string> check, Action<FamiStudioWindow, string> capture = null)
    {
        Directory.CreateDirectory(output);
        var path = FixturePath(output);
        if (!File.Exists(path)) MakeFixture(path, check);
        var type = typeof(NsfFile).Assembly.GetType("FamiStudio.NsfImportDialog");
        var import = Activator.CreateInstance(type, new object[] { window, path });
        var dialog = (PropertyDialog)type.GetField("dialog", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(import);
        Project imported = null;
        type.GetMethod("ShowDialogAsync").Invoke(import, new object[] { window, (Action<Project>)(p => imported = p) });
        var props = (System.Collections.IList)typeof(PropertyPage).GetField("properties", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(dialog.Properties);
        var grid = (Grid)props[0].GetType().GetField("control").GetValue(props[0]);
        var rowHeight = (int)typeof(Grid).GetField("rowHeight", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(grid);
        var offsets = (int[])typeof(Grid).GetField("columnOffsets", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(grid);
        var widths = (int[])typeof(Grid).GetField("columnWidths", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(grid);
        grid.SendPointerDown(new PointerEventArgs(1, 10, rowHeight * 2 + rowHeight / 2));
        grid.SendPointerUp(new PointerEventArgs(1, 10, rowHeight * 2 + rowHeight / 2));
        check(dialog.Properties.GetPropertyValue<bool>(0, 0, 0) && dialog.Properties.GetPropertyValue<bool>(0, 1, 0) && !dialog.Properties.GetPropertyValue<bool>(0, 2, 0), "NSF dialog checkbox clicks select multiple rows independently");
        int durationX = offsets[2] + widths[2] / 2, durationY = rowHeight * 2 + rowHeight / 2;
        grid.SendPointerDown(new PointerEventArgs(1, durationX, durationY));
        grid.SendPointerUp(new PointerEventArgs(1, durationX, durationY));
        NumericUiTests.SubmitPopup(window, "2");
        check(dialog.Properties.GetPropertyValue<int>(0, 1, 2) == 2, "NSF per-song duration accepts typed seconds");
        capture?.Invoke(window, Path.Combine(output, Localization.LanguageCode + "-nsf-import.ppm"));
        dialog.Properties.SetPropertyValue(0, 0, 0, false);
        dialog.Properties.SetPropertyValue(0, 1, 0, false);
        NumericUiTests.ControlAt(dialog.Properties, 3).GrabDialogFocus();
        dialog.SendKeyDown(new KeyEventArgs(Keys.Enter, default, false, 0));
        check(window.TopDialog == dialog && imported == null, "empty NSF selection cannot confirm with Enter from a numeric field");
        dialog.SendKeyDown(new KeyEventArgs(Keys.KeypadEnter, default, false, 0));
        check(window.TopDialog == dialog && imported == null, "empty NSF selection cannot confirm with keypad Enter");
        dialog.Properties.SetPropertyValue(0, 0, 0, true);
        dialog.Properties.SetPropertyValue(0, 1, 0, true);
        dialog.Properties.SetPropertyValue(0, 0, 2, 1);
        dialog.Properties.SetPropertyValue(0, 1, 2, 1);
        dialog.Close(DialogResult.OK);
        check(imported != null && imported.Songs.Count == 2, "NSF dialog confirmation imports all checked rows into one project");
        check(window.TopDialog == null, "NSF progress dialog closes after capture");
        var cancelled = Activator.CreateInstance(type, new object[] { window, path });
        bool cancelledCalled = false;
        type.GetMethod("ShowDialogAsync").Invoke(cancelled, new object[] { window, (Action<Project>)(p => { cancelledCalled = true; check(p == null, "cancelled NSF selection returns no partial project"); }) });
        window.TopDialog.Close(DialogResult.Cancel);
        check(cancelledCalled && window.TopDialog == null, "cancelling the NSF selection closes the dialog normally");
    }
}
