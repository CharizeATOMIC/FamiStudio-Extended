using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using FamiStudio;

static class PcmSampleRateExportRegression
{
    static readonly List<string> checks = new();
    static readonly byte[] Existing = { 0x50, 0x43, 0x4d, 0x13 };
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message + ": " + Log.GetLastMessage(LogSeverity.Error));
        checks.Add(message);
        Console.WriteLine("PASS PCM export rate: " + message);
    }
    static byte[] Snapshot(Project project)
    {
        var buffer = new ProjectSaveBuffer(project);
        project.Serialize(buffer);
        return buffer.GetBuffer();
    }
    static Project Fixture(bool pal, int destination)
    {
        var p = new Project { PalMode = pal, Name = "PCM compatibility", Author = "Synthetic regression" };
        int expansion = destination == 1 ? ExpansionType.Mmc5 : destination >= 2 ? ExpansionType.Vrc6 : ExpansionType.None;
        if (expansion != ExpansionType.None) p.SetExpansionAudioMask(1 << (expansion - 1));
        if (destination == 1) p.Mmc5PcmEnabled = true;
        var sample = p.CreatePcmSampleFromWavData("Original WAV",
            Enumerable.Range(0, 4097).Select(i => (short)(Math.Sin(i * 2 * Math.PI * 440 / 22050) * 23000)).ToArray(), 22050);
        sample.VolumeAdjust = 81;
        sample.FinePitch = 1.125f;
        sample.Process();
        var instrument = p.CreateInstrument(expansion, "Mapped PCM");
        instrument.MapPcmSample(49, sample, 7, false);
        instrument.MapPcmSample(50, sample, -5, true);
        var song = p.CreateSong("Phrase");
        song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        song.SetDefaultPatternLength(24); song.SetBeatLength(6); song.SetLength(1); song.SetLoopPoint(-1);
        var channel = song.GetChannelByType(new[] { ChannelType.Dpcm, ChannelType.Mmc5Pcm, ChannelType.Vrc6Square1, ChannelType.Vrc6Square2 }[destination]);
        channel.GetOrCreatePattern(0).Notes[0] = new Note(49) { Instrument = instrument, Duration = 8 };
        channel.GetOrCreatePattern(0).Notes[12] = new Note(50) { Instrument = instrument, Duration = 8, Volume = 11 };
        return p;
    }
    static bool Save(Project p, string format, string path, int? rate)
    {
        var ids = p.Songs.Select(s => s.Id).ToArray();
        int machine = p.PalMode ? MachineType.PAL : MachineType.NTSC;
        if (format == "nsf" || format == "nsfe")
            return new NsfFile().Save(p, FamiToneKernel.FamiStudio, path, ids, p.Name, p.Author, p.Copyright, machine, format == "nsfe", rate);
        if (format == "nes") return new RomFile().Save(p, path, ids, p.Name, p.Author, p.PalMode, rate);
        return new FamitoneMusicFile(FamiToneKernel.FamiStudio, false).Save(p, ids, AssemblyFormat.CA65,
            -1, false, path, Path.ChangeExtension(path, ".dmc"), DpcmExportMode.All, false, null, machine, rate);
    }
    static void CaptureEqual(Mmc5PcmExportData a, Mmc5PcmExportData b, string context)
    {
        Check(a.SampleRate == b.SampleRate && a.BankData.SequenceEqual(b.BankData), context + " exact resampled source bytes and clock");
        Check(a.Samples.Select(s => (s.Offset, s.Data.Length, s.Loop)).SequenceEqual(b.Samples.Select(s => (s.Offset, s.Data.Length, s.Loop))), context + " sample length, loop and bank placement");
        Check(a.Tracks.Count == b.Tracks.Count && a.Tracks.All(kv =>
        {
            var other = b.Tracks[kv.Key]; var track = kv.Value;
            return track.EndTick == other.EndTick && track.LoopTick == other.LoopTick &&
                track.Events.Select(e => (e.Tick, e.Command, e.SampleIndex, e.Destination)).SequenceEqual(
                    other.Events.Select(e => (e.Tick, e.Command, e.SampleIndex, e.Destination)));
        }), context + " attack, stop, gain and score duration stay exact");
    }
    static void CpuEqual(string a, string b, string context)
    {
        int[][] Run(string path)
        {
            var handle = NotSoFatso.NsfOpen(path);
            if (handle == IntPtr.Zero) throw new Exception("Cannot open " + path);
            var result = new List<int[]>();
            NotSoFatso.WriteRegisterDelegate callback = (reg, value) => result.Add(new[] {
                NotSoFatso.NsfGetState(handle, -1, NotSoFatso.STATE_PLAYCALLS, 0), reg, value });
            try
            {
                NotSoFatso.NsfSetApuWriteCallback(handle, callback); NotSoFatso.NsfSetTrack(handle, 0);
                for (int i = 0; i < 30; i++) NotSoFatso.NsfRunFrame(handle);
            }
            finally { NotSoFatso.NsfClose(handle); GC.KeepAlive(callback); }
            return result.ToArray();
        }
        var first = Run(a); var second = Run(b);
        Check(first.Length > 100 && first.Length == second.Length && first.Zip(second).All(p => p.First.SequenceEqual(p.Second)), context + " actual NSF2 CPU register sequence is identical");
    }
    static void CliCases(Project p, string output)
    {
        var type = typeof(Project).Assembly.GetType("FamiStudio.CommandLineInterface");
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var option in new[] { "", "-pcm-rate:8000", "-pcm-rate:8k", "-pcm-rate", "-pcm-rate:4k", "-pcm-rate:4000" })
        {
            var path = Path.Combine(output, "cli-" + (option.Length == 0 ? "missing" : option.Replace(":", "-")) + ".nsf");
            File.WriteAllBytes(path, Existing);
            var cli = Activator.CreateInstance(type, new object[] { new[] { "unused.fms", "nsf-export", path, option } });
            type.GetField("project", flags).SetValue(cli, p);
            type.GetMethod("NsfExport", flags).Invoke(cli, new object[] { path });
            bool accepted = option == "-pcm-rate:4k" || option == "-pcm-rate:4000";
            Check(accepted != File.ReadAllBytes(path).SequenceEqual(Existing), "CLI " + (option.Length == 0 ? "omitted choice refuses export" : option));
        }
    }
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        checks.Clear();
        var hashes = new List<object>();
        foreach (bool pal in new[] { false, true })
        foreach (int destination in Enumerable.Range(0, 4))
        {
            string context = (pal ? "PAL" : "NTSC") + " DAC" + destination;
            var baseline = Fixture(pal, destination);
            var ids = baseline.Songs.Select(s => s.Id).ToArray();
            var expected = Mmc5PcmExportData.Capture(baseline, ids, pal);
            var original = Snapshot(baseline);
            var baseFolder = Path.Combine(output, context, "baseline"); Directory.CreateDirectory(baseFolder);
            foreach (var format in new[] { "nsf", "nsfe", "nes", "s" })
                Check(Save(baseline, format, Path.Combine(baseFolder, "music." + format), null), context + " baseline " + format);
            Check(Snapshot(baseline).SequenceEqual(original), context + " baseline export preserves project");
            foreach (int multiplier in new[] { 2, 4, 8 })
            {
                var high = baseline.DeepClone(); high.PcmRateMultiplier = multiplier;
                var before = Snapshot(high);
                var folder = Path.Combine(output, context, "rate" + multiplier); Directory.CreateDirectory(folder);
                try { Mmc5PcmExportData.Capture(high, ids, pal); throw new Exception("Unconfirmed high-rate capture succeeded"); }
                catch (InvalidOperationException) { Check(true, context + " high-rate capture requires explicit choice " + multiplier); }
                var actual = Mmc5PcmExportData.Capture(high, ids, pal, pcmSampleRate: 4000);
                CaptureEqual(expected, actual, context + " rate" + multiplier);
                foreach (var format in new[] { "nsf", "nsfe", "nes", "s" })
                {
                    var path = Path.Combine(folder, "music." + format);
                    File.WriteAllBytes(path, Existing);
                    Check(!Save(high, format, path, null), context + " high-rate " + format + " refuses implicit conversion " + multiplier);
                    Check(File.ReadAllBytes(path).SequenceEqual(Existing), context + " refusal preserves destination " + format + " " + multiplier);
                    Check(!Save(high, format, path, 8000), context + " unsupported hardware rate rejected " + format + " " + multiplier);
                    Check(File.ReadAllBytes(path).SequenceEqual(Existing), context + " unsupported rate preserves destination " + format + " " + multiplier);
                    Check(Save(high, format, path, 4000), context + " explicit compatibility export " + format + " " + multiplier);
                    var reference = Path.Combine(baseFolder, "music." + format);
                    Check(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(reference)), context + " exact baseline hardware artifact " + format + " " + multiplier);
                    hashes.Add(new { path, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() });
                    if (format == "nsf" && multiplier == 8) CpuEqual(reference, path, context);
                    if (format == "s")
                    {
                        var pcmPath = Path.ChangeExtension(path, ".pcm");
                        Check(File.ReadAllBytes(pcmPath).SequenceEqual(File.ReadAllBytes(Path.ChangeExtension(reference, ".pcm"))), context + " engine PCM sidecar exact " + multiplier);
                    }
                }
                Check(Snapshot(high).SequenceEqual(before), context + " all export paths preserve high-rate project/source/mappings " + multiplier);
                if (!pal && destination == 0 && multiplier == 2) CliCases(high, folder);
            }
            try { Mmc5PcmExportData.Capture(baseline, ids, pal, sampleRate: 8000); throw new Exception("Capture allowed unsupported hardware clock"); }
            catch (InvalidOperationException) { Check(true, context + " low-level high hardware clock rejected"); }
        }
        File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new { passed = true, checks = checks.Count, results = checks, artifacts = hashes }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS " + checks.Count + " PCM sample-rate export checks");
    }
}
