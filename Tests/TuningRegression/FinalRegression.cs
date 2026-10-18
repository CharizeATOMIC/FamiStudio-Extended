using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using FamiStudio;

static class FinalRegression
{
    public static void Run(string output, Action<bool, string> check)
    {
        var p = Program.MakeProject();
        p.SetExpansionAudioMask(ExpansionType.N163Mask, 2);
        var counts = Enumerable.Repeat(1, ExpansionType.Count).ToArray();
        counts[ExpansionType.N163] = 2;
        p.SetExpansionInstances(counts);
        var a = p.CreateInstrument(ExpansionType.N163, "Wave A");
        var b = p.CreateInstrument(ExpansionType.N163, "Wave B");
        a.N163WaveAutoPos = b.N163WaveAutoPos = true;
        var s = p.Songs[0];
        s.GetChannelByType(ChannelType.N163Wave1, 1).GetOrCreatePattern(0).Notes[0] = new Note(46) { Instrument = a, Duration = 64 };
        s.GetChannelByType(ChannelType.N163Wave2, 1).GetOrCreatePattern(0).Notes[0] = new Note(49) { Instrument = b, Duration = 64 };
        check(p.AutoAssignN163WavePositions(out var positions, 1) && positions[a.Id] != positions[b.Id], "second N163 allocates simultaneous waves independently");
        var player = new CapturePlayer(false, false);
        player.Render(s);
        check(player.Audio.Any(v => v != 0), "second N163 renders its simultaneous waves");

        var original = s.GetChannelByType(ChannelType.N163Wave1, 1).PatternInstances[0];
        var save = new ProjectSaveBuffer(p, ProjectBufferFlags.Clipboard);
        typeof(ClipboardUtils).GetMethod("SavePatternList", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { save, new List<Pattern> { original } });
        var target = p.CreateSong("Paste target");
        var load = new ProjectLoadBuffer(p, save.GetBuffer(), Project.Version, ProjectBufferFlags.Clipboard);
        typeof(ClipboardUtils).GetMethod("LoadAndMergePatternList", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { load, target, false, ClipboardImportFlags.CreateMissing });
        var pasted = target.GetChannelByType(ChannelType.N163Wave1, 1).GetPattern(original.Name);
        check(pasted != null && pasted.Channel.ChipInstance == 1 && pasted.Notes[0].Value == 46, "clipboard preserves duplicate chip ownership without using the system clipboard");

        p = Program.MakeProject(); s = p.Songs[0];
        check(!new FamitrackerTextFile().Save(p, Path.Combine(output, "unsupported-ft.txt"), new[] { s.Id }), "FamiTracker text rejects unsupported custom tuning");
        string midi = Path.Combine(output, "unsupported-tuning.mid");
        new MidiFileWriter().Save(p, midi, s.Id, MidiExportInstrumentMode.Channel, new int[s.Channels.Length], true, false, 2);
        check(!File.Exists(midi), "MIDI rejects unsupported custom tuning");
        foreach (bool pal in new[] { false, true })
        {
            check(new RomFile().Save(p, Path.Combine(output, $"tuning-{pal}.nes"), new[] { s.Id }, "Tuning", "Test", pal), "NES image exports with custom tuning");
            check(new FdsFile().Save(p, Path.Combine(output, $"tuning-{pal}.fds"), new[] { s.Id }, "Tuning", "Test", pal), "FDS image exports with custom tuning");
        }
        var plain = p.CreateSong("Project tuning");
        plain.SetLength(1);
        plain.Channels[0].GetOrCreatePattern(0).Notes[0] = new Note(46) { Instrument = p.Instruments[0], Duration = 64 };
        string nsfPath = Path.Combine(output, "track-switch.nsf");
        check(new NsfFile().Save(p, FamiToneKernel.FamiStudio, nsfPath, new[] { s.Id, plain.Id }, "Switch", "Test", "", MachineType.NTSC, false), "mixed tuning tracks export");
        var handle = NotSoFatso.NsfOpen(nsfPath);
        foreach (int track in new[] { 0, 1, 0 })
        {
            NotSoFatso.NsfSetTrack(handle, track);
            for (int frame = 0; frame < 10; frame++) NotSoFatso.NsfRunFrame(handle);
            int expected = NesApu.GetNoteTableForChannelType(ChannelType.Square1, false, 1, track == 0 ? 432 : 440, track == 0 ? new int[12] : null)[46];
            check(NotSoFatso.NsfGetState(handle, 0, NotSoFatso.STATE_PERIOD, 0) == expected, "NSF track switch resets tuning pointers");
        }
        NotSoFatso.NsfClose(handle);

        p = Program.MakeProject();
        p.SetExpansionAudioMask(ExpansionType.Vrc6Mask);
        counts = Enumerable.Repeat(1, ExpansionType.Count).ToArray(); counts[ExpansionType.Vrc6] = 2; p.SetExpansionInstances(counts);
        var previewChannel = p.Songs[0].GetChannelByType(ChannelType.Vrc6Square1, 1);
        previewChannel.StereoEnabled = true; previewChannel.Pan = 100;
        using var stream = new PullAudioStream();
        var preview = new InstrumentPlayer(stream, false, 44100, true, 0);
        preview.Start(p, false);
        preview.PlayNote(previewChannel.Index, new Note(46) { Instrument = p.CreateInstrument(ExpansionType.Vrc6), Duration = 64 });
        stream.Read();
        // This field normally comes from the selected channel in the GUI.
        typeof(InstrumentPlayer).GetField("previewChannel", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(preview, previewChannel);
        short[] audio = null;
        for (int frame = 0; frame < 20; frame++) audio = stream.Read();
        check(audio.Where((v, i) => (i & 1) == 0).All(v => v == 0) && audio.Where((v, i) => (i & 1) == 1).Any(v => v != 0), "keyboard preview plays the second chip with its right pan");
        previewChannel.Pan = 0;
        audio = stream.Read();
        check(audio.Where((v, i) => (i & 1) == 1).All(v => v == 0) && audio.Where((v, i) => (i & 1) == 0).Any(v => v != 0), "keyboard preview follows live pan changes");
        preview.Stop(); preview.Shutdown();
    }

    sealed class PullAudioStream : IAudioStream
    {
        GetBufferDataCallback callback;
        public bool IsPlaying { get; private set; }
        public bool Stereo => true;
        public bool RecreateOnDeviceChanged => false;
        public int ImmediatePlayPosition => 0;
        public void Start(GetBufferDataCallback fill, StreamStartingCallback start) { callback = fill; IsPlaying = true; start(); }
        public short[] Read() => callback(out _);
        public void Stop() { IsPlaying = false; }
        public void PlayImmediate(short[] data, int sampleRate, float volume, int channel = 0) { }
        public void Dispose() => Stop();
    }
}
