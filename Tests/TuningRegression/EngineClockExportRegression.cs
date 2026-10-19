using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FamiStudio;

static class EngineClockExportRegression
{
    static int checks;
    static void Check(bool value, string message)
    { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS engine clock: " + message); }
    static void Macro(Envelope envelope, params sbyte[] values)
    { envelope.Length = values.Length; envelope.Loop = -1; Array.Copy(values, envelope.Values, values.Length); }
    static Song Song(Project project, string name, int length = 80)
    {
        var song = project.CreateSong(name); song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        song.SetLength(1); song.SetDefaultPatternLength(length); song.SetBeatLength(4); song.SetLoopPoint(0); return song;
    }
    static Project Fixture(string kind, bool pal)
    {
        var project = new Project { PalMode = pal };
        if (kind == "fds") project.SetExpansionAudioMask(ExpansionType.FdsMask);
        var song = Song(project, kind);
        if (kind == "fds")
        {
            var instrument = project.CreateInstrument(ExpansionType.Fds, "FDS modulation delay");
            Macro(instrument.VolumeEnvelope, 15, 13, 10, 8, 4); instrument.VolumeEnvelope.Loop = 1;
            Macro(instrument.PitchEnvelope, 0, 1, -1, 2); instrument.PitchEnvelope.Loop = 0;
            instrument.FdsModDelay = 5; instrument.FdsModDepth = 7; instrument.FdsModSpeed = 123;
            var pattern = song.GetChannelByType(ChannelType.FdsWave).GetOrCreatePattern(0);
            pattern.Notes[0] = new Note(46) { Instrument = instrument, Duration = 30 };
            pattern.Notes[30] = new Note(49) { Instrument = instrument, Duration = 50 };
            return project;
        }
        var absolute = project.CreateInstrument(0, "Absolute pitch and released volume");
        Macro(absolute.VolumeEnvelope, 15, 12, 9, 7, 4, 1, 0); absolute.VolumeEnvelope.Loop = 1; absolute.VolumeEnvelope.Release = 4;
        Macro(absolute.PitchEnvelope, 0, 2, -1, 1); absolute.PitchEnvelope.Loop = 0;
        Macro(absolute.Envelopes[EnvelopeType.DutyCycle], 0, 1, 2, 3); absolute.Envelopes[EnvelopeType.DutyCycle].Loop = 0;
        Macro(absolute.ArpeggioEnvelope, 0, 4, 7); absolute.ArpeggioEnvelope.Loop = 0;
        var relative = project.CreateInstrument(0, "Relative pitch and decay");
        Macro(relative.VolumeEnvelope, 15, 13, 10, 8, 6, 4); relative.VolumeEnvelope.Loop = 1;
        Macro(relative.PitchEnvelope, 1, 0, -1, 0); relative.PitchEnvelope.Relative = true; relative.PitchEnvelope.Loop = 0;
        Macro(relative.Envelopes[EnvelopeType.DutyCycle], 2);
        var first = song.Channels[0].GetOrCreatePattern(0);
        first.Notes[0] = new Note(46) { Instrument = absolute, Duration = 20, Release = 12 };
        first.Notes[24] = new Note(49) { Instrument = absolute, Duration = 20, Release = 10 };
        first.Notes[48] = new Note(46) { Instrument = absolute, Duration = 24, SlideNoteTarget = 53, Volume = 15, VolumeSlideTarget = 3 };
        var second = song.Channels[1].GetOrCreatePattern(0);
        second.Notes[0] = new Note(34) { Instrument = relative, Duration = 30 };
        second.Notes[8] = new Note { EnvelopeDecay = 3 };
        second.Notes[17] = new Note { EnvelopeDecay = 0 };
        second.Notes[32] = new Note(38) { Instrument = relative, Duration = 40, VibratoSpeed = 4, VibratoDepth = 3 };
        var noise = project.CreateInstrument(0, "Noise restart pulses");
        Macro(noise.NoisePeriodEnvelope, 2, 4, 7, 9); noise.NoisePeriodEnvelope.Loop = 0;
        Macro(noise.NoiseControlEnvelope, 0x3f, 0x3a, 0x37, 0x33); noise.NoiseControlEnvelope.Loop = 0;
        Macro(noise.NoiseRestartEnvelope, 2, 0, 0, 2, 0); noise.NoiseRestartEnvelope.Loop = 0;
        var drums = song.GetChannelByType(ChannelType.Noise).GetOrCreatePattern(0);
        drums.Notes[0] = new Note(49) { Instrument = noise, Duration = 38 };
        drums.Notes[40] = new Note(49) { Instrument = noise, Duration = 36 };
        if (kind == "tracker")
        {
            project.ConvertToFamiTrackerTempo(true); song.FamitrackerTempo = pal ? 117 : 143; song.FamitrackerSpeed = 3;
            first.Notes[0].NoteDelay = 1; first.Notes[24].CutDelay = 2;
            first.Notes[48].NoteDelay = 2; second.Notes[32].CutDelay = 1;
        }
        if (kind == "delayed-variant")
        {
            first.PlaybackDelay = 2;
            var variant = song.Channels[0].CreatePattern("Loop variant");
            variant.PlaybackDelay = 2;
            foreach (var entry in first.Notes) variant.Notes.Add(entry.Key, entry.Value.Clone());
            variant.Notes[0].Value = 53; first.LoopVariant = variant;
        }
        return project;
    }
    sealed class Trace
    {
        public readonly List<Dictionary<int, int>> Frames = new();
        public readonly List<(int Register, int Value)[]> Writes = new();
    }
    static Trace Render(Project project, string path, int frames, int track = 0)
    {
        Check(new NsfFile().Save(project, FamiToneKernel.FamiStudio, path, project.Songs.Select(s => s.Id).ToArray(), "Clock regression", "Original test", "", project.PalMode ? MachineType.PAL : MachineType.NTSC, false), "NSF export " + Path.GetFileName(path));
        var handle = NotSoFatso.NsfOpen(path); Check(handle != IntPtr.Zero, "CPU loads " + Path.GetFileName(path));
        var trace = new Trace(); var state = new Dictionary<int, int>(); var pending = new List<(int, int)>();
        NotSoFatso.WriteRegisterDelegate callback = (register, value) => { state[register] = value; pending.Add((register, value)); };
        try
        {
            NotSoFatso.NsfSetApuWriteCallback(handle, callback); NotSoFatso.NsfSetTrack(handle, track);
            NotSoFatso.NsfRunFrame(handle); pending.Clear();
            for (int f = 0; f < frames; f++)
            { NotSoFatso.NsfRunFrame(handle); trace.Frames.Add(new(state)); trace.Writes.Add(pending.ToArray()); pending.Clear(); }
        }
        finally { NotSoFatso.NsfClose(handle); GC.KeepAlive(callback); }
        return trace;
    }
    static void Compare(Trace baseline, Trace high, int divider, string label, string output)
    {
        var relevant = new HashSet<int>(new[] { 0x4000, 0x4002, 0x4003, 0x4004, 0x4006, 0x4007, 0x4008, 0x400a, 0x400b, 0x400c, 0x400e, 0x4080, 0x4082, 0x4083, 0x4084, 0x4085, 0x4086, 0x4087, 0x4089 });
        var errors = new List<string>();
        for (int frame = 0; frame < high.Frames.Count; frame++)
        {
            var expected = baseline.Frames[frame / divider]; var actual = high.Frames[frame];
            foreach (int register in relevant)
                if (expected.TryGetValue(register, out int value) && actual.GetValueOrDefault(register, -1) != value)
                    errors.Add($"tick {frame}, logic {frame / divider}, ${register:x4}: ${actual.GetValueOrDefault(register, -1):x2} != ${value:x2}");
        }
        var originalPulses = baseline.Writes.SelectMany((writes, tick) => writes.Where(w => w.Register == 0x400f).Select(w => (Tick: tick * divider, w.Value))).ToArray();
        var highPulses = high.Writes.SelectMany((writes, tick) => writes.Where(w => w.Register == 0x400f).Select(w => (Tick: tick, w.Value))).ToArray();
        if (errors.Count != 0 || !originalPulses.SequenceEqual(highPulses))
            File.WriteAllText(Path.Combine(output, label + "-differences.json"), JsonSerializer.Serialize(new { errors, originalPulses = originalPulses.Select(p => new[] { p.Tick, p.Value }), highPulses = highPulses.Select(p => new[] { p.Tick, p.Value }) }, new JsonSerializerOptions { WriteIndented = true }));
        Check(errors.Count == 0, label + " actual CPU register clocks remain unchanged: " + string.Join("; ", errors.Take(3)));
        Check(originalPulses.SequenceEqual(highPulses), label + " actual noise restart writes occur once, on the correct clock");
    }
    static string Export(Project project, string path)
    {
        Check(new FamitoneMusicFile(FamiToneKernel.FamiStudio, false).Save(project, project.Songs.Select(s => s.Id).ToArray(), AssemblyFormat.CA65, 8192, false, path, null, DpcmExportMode.Minimum, false, null, MachineType.NTSC), "assembly export " + Path.GetFileName(path));
        return File.ReadAllText(path);
    }
    static void Structure(string output)
    {
        var project = Fixture("apu", false); string normal = Export(project, Path.Combine(output, "normal.s"));
        Check(!normal.Contains("FAMISTUDIO_USE_UPDATE_RATE") && !normal.Contains("envelope update divider"), "1x requires no new opcode flag or macro prefix");
        project.UpdateRateMultiplier = 4;
        var second = project.DuplicateSong(project.Songs[0]); second.LogicRateMultiplier = 2;
        // Equal encoded programs with different clocks must not share a pointer.
        var fast = project.CreateInstrument(0, "Independent 4x macro");
        Macro(fast.VolumeEnvelope, 15, 12, 9, 7, 4, 1, 0); fast.VolumeEnvelope.Loop = 1; fast.VolumeEnvelope.Release = 4;
        second.Channels[2].GetOrCreatePattern(0).Notes[0] = new Note(46) { Instrument = fast, Duration = 32 };
        string assembly = Export(project, Path.Combine(output, "mixed-clocks.s"));
        Check(assembly.Contains("FAMISTUDIO_USE_UPDATE_RATE = 1"), "high-rate export declares the required engine feature");
        Check(Regex.IsMatch(assembly, @"song0ch0:\s*\.byte \$64,\s*\$04,\s*\$04") && Regex.IsMatch(assembly, @"song1ch0:\s*\.byte \$64,\s*\$04,\s*\$02"), "clock opcode occurs at channel-zero entry with each song's divider");
        Check(Regex.Matches(assembly, @"\$64,\s*\$04,\s*\$0[124]").Count == 2, "clock configuration is not repeated on loops or other channels");
        var blocks = Regex.Matches(assembly, @"\.byte \$(0[124]) ; envelope update divider\s*(@env\d+):\s*\.byte ([^\r\n]+)").Cast<Match>().ToArray();
        Check(blocks.Any(a => blocks.Any(b => a.Groups[3].Value == b.Groups[3].Value && a.Groups[1].Value != b.Groups[1].Value)), "identical bytecode with different dividers has distinct labels");
        var exporter = new FamitoneMusicFile(FamiToneKernel.FamiStudio, false);
        typeof(FamitoneMusicFile).GetField("project", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(exporter, project);
        var getKey = typeof(FamitoneMusicFile).GetMethod("GetEnvelopeKey", BindingFlags.NonPublic | BindingFlags.Instance);
        ulong Key(int rate, bool executable) => (ulong)getKey.Invoke(exporter, new object[] { new byte[] { 0xc0, 0x7f, 0, 1 }, rate, executable });
        Check(Key(1, true) != Key(2, true) && Key(2, true) != Key(4, true) && Key(1, false) != Key(4, true), "macro deduplication includes time base and distinguishes raw wave data");
        var fds = Fixture("fds", false); fds.UpdateRateMultiplier = 4;
        string fdsAssembly = Export(fds, Path.Combine(output, "fds-clocks.s"));
        var instrumentLine = fdsAssembly.Split('\n').First(line => line.Contains("FDS modulation delay"));
        Check(fdsAssembly.Contains("envelope update divider") && instrumentLine.Contains("@env"), "FDS export retains executable envelope pointers");
    }
    static void Mixed(string output, bool pal)
    {
        var project = new Project { PalMode = pal }; var song = Song(project, "Slow score fast volume", 16);
        project.UpdateRateMultiplier = 4; var instrument = project.CreateInstrument(0, "4x volume");
        Macro(instrument.VolumeEnvelope, 15, 12, 9, 6, 3, 0); instrument.VolumeEnvelope.Loop = 0;
        var pattern = song.Channels[0].GetOrCreatePattern(0);
        pattern.Notes[0] = new Note(46) { Instrument = instrument, Duration = 8 };
        pattern.Notes[8] = new Note(49) { Instrument = instrument, Duration = 8 };
        var trace = Render(project, Path.Combine(output, "mixed-" + pal + ".nsf"), 64);
        int[] volume = { 15, 12, 9, 6, 3, 0 };
        var periods = NesApu.GetNoteTableForChannelType(ChannelType.Square1, pal, 1);
        for (int tick = 0; tick < 64; tick++)
        {
            Check((trace.Frames[tick].GetValueOrDefault(0x4000) & 15) == volume[(tick % 32) % volume.Length], $"mixed macro advances at output tick {tick}, PAL {pal}");
            int period = trace.Frames[tick].GetValueOrDefault(0x4002) | ((trace.Frames[tick].GetValueOrDefault(0x4003) & 7) << 8);
            Check(period == periods[tick < 32 ? 46 : 49], $"mixed score keeps eight-video-frame note duration at tick {tick}, PAL {pal}");
        }
    }
    static void SharedVibrato(string output, bool pal)
    {
        var project = Fixture("apu", pal); project.UpdateRateMultiplier = 2;
        var second = project.DuplicateSong(project.Songs[0]); second.LogicRateMultiplier = 2;
        var baselines = Enumerable.Range(0, 2).Select(track => Render(project, Path.Combine(output, $"vibrato-{pal}-{track}-2.nsf"), 160, track)).ToArray();
        project.UpdateRateMultiplier = 4;
        foreach (int track in Enumerable.Range(0, 2))
        {
            var high = Render(project, Path.Combine(output, $"vibrato-{pal}-{track}-4.nsf"), 320, track);
            Compare(baselines[track], high, 2, $"vibrato-{pal}-track{track}", output);
        }
    }
    static byte[] ModelBytes(Project project)
    { var buffer = new ProjectSaveBuffer(project); project.Serialize(buffer); return buffer.GetBuffer(); }
    static void SoundEffects(string output)
    {
        var project = new Project(); var song = Song(project, "Original 1x SFX", 32); song.SetLoopPoint(-1);
        var instrument = project.CreateInstrument(0, "SFX pulse");
        Macro(instrument.VolumeEnvelope, 15, 12, 8, 4, 0);
        Macro(instrument.PitchEnvelope, 0, 2, -1); Macro(instrument.Envelopes[EnvelopeType.DutyCycle], 1);
        song.Channels[0].GetOrCreatePattern(0).Notes[0] = new Note(46) { Instrument = instrument, Duration = 24 };
        bool Save(Project p, string path, int format) => new FamitoneSoundEffectFile().Save(p, new[] { song.Id }, format, MachineType.Dual, FamiToneKernel.FamiStudio, path, null);
        foreach (int format in Enumerable.Range(0, 4))
        {
            string normal = Path.Combine(output, $"sfx-original-{format}.s"), high = Path.Combine(output, $"sfx-preserved-{format}.s");
            Check(Save(project, normal, format), "original SFX exports " + format);
            var mixed = project.DeepClone(); mixed.UpdateRateMultiplier = 4;
            var unusedSong = Song(mixed, "Unselected 4x song");
            var unusedInstrument = mixed.CreateInstrument(0, "Unselected 4x macro");
            unusedSong.Channels[0].GetOrCreatePattern(0).Notes[0] = new Note(49) { Instrument = unusedInstrument, Duration = 20 };
            var before = ModelBytes(mixed);
            Check(Save(mixed, high, format), "selected 1x SFX exports from 4x project despite unselected fast resources " + format);
            Check(File.ReadAllBytes(normal).SequenceEqual(File.ReadAllBytes(high)), "SFX bytes and both regional timings remain exactly identical " + format);
            Check(before.SequenceEqual(ModelBytes(mixed)), "SFX normalization leaves user project byte-exact " + format);
        }
        foreach (string kind in new[] { "song", "instrument", "arpeggio", "variant" })
        {
            var fast = project.DeepClone(); fast.UpdateRateMultiplier = 4; var selected = fast.GetSong(song.Id);
            if (kind == "song") selected.LogicRateMultiplier = 2;
            if (kind == "instrument") fast.Instruments[0].VolumeEnvelope.TickRateMultiplier = 4;
            if (kind == "arpeggio")
            {
                var arp = fast.CreateArpeggio(); Macro(arp.Envelope, 0, 4, 7);
                selected.Channels[0].PatternInstances[0].Notes[0].Arpeggio = arp;
            }
            if (kind == "variant")
            {
                selected.SetLoopPoint(0); var variant = selected.Channels[0].CreatePattern("Fast loop variation");
                var voice = fast.CreateInstrument(0, "4x variation voice"); Macro(voice.VolumeEnvelope, 15, 8, 0);
                variant.Notes[0] = new Note(49) { Instrument = voice, Duration = 20 };
                selected.Channels[0].PatternInstances[0].LoopVariant = variant;
            }
            string filename = Path.Combine(output, "sfx-rejected-" + kind + ".s");
            if (File.Exists(filename)) File.Delete(filename);
            var before = ModelBytes(fast);
            Check(!Save(fast, filename, AssemblyFormat.CA65) && !File.Exists(filename), "true fast SFX rejects unsupported clock before writing: " + kind);
            Check(before.SequenceEqual(ModelBytes(fast)), "rejected SFX leaves user project byte-exact: " + kind);
        }
    }
    public static void Run(string output)
    {
        Directory.CreateDirectory(output); checks = 0; Structure(output); SoundEffects(output);
        foreach (bool pal in new[] { false, true })
        {
            foreach (string kind in new[] { "apu", "tracker", "fds", "delayed-variant" })
            {
                var project = Fixture(kind, pal); int count = kind == "tracker" ? 300 : 160;
                var baseline = Render(project, Path.Combine(output, $"{kind}-{pal}-1.nsf"), count);
                foreach (int rate in new[] { 2, 4 })
                {
                    project.UpdateRateMultiplier = rate;
                    var high = Render(project, Path.Combine(output, $"{kind}-{pal}-{rate}.nsf"), count * rate);
                    Compare(baseline, high, rate, $"{kind}-{pal}-{rate}", output);
                }
            }
            Mixed(output, pal); SharedVibrato(output, pal);
        }
        File.WriteAllText(Path.Combine(output, "engine-clock-report.json"), JsonSerializer.Serialize(new { passed = true, checks }));
        Console.WriteLine($"All {checks} engine-clock export checks passed.");
    }
    public static void Demo(string output)
    {
        Directory.CreateDirectory(output);
        var project = new Project { Name = "UniformClockDemo", Author = "Original regression melody" };
        var song = Song(project, "Uniform IRQ melody", 96);
        var lead = project.CreateInstrument(0, "Pulse lead");
        Macro(lead.VolumeEnvelope, 15, 12, 10, 8, 4, 0); lead.VolumeEnvelope.Loop = 1; lead.VolumeEnvelope.Release = 4;
        Macro(lead.PitchEnvelope, 0, 1, 0, -1); lead.PitchEnvelope.Loop = 0;
        Macro(lead.Envelopes[EnvelopeType.DutyCycle], 2);
        var pattern = song.Channels[0].GetOrCreatePattern(0);
        pattern.Notes[0] = new Note(49) { Instrument = lead, Duration = 20, Release = 14 };
        pattern.Notes[24] = new Note(53) { Instrument = lead, Duration = 20, Release = 14 };
        pattern.Notes[48] = new Note(56) { Instrument = lead, Duration = 20, SlideNoteTarget = 61 };
        pattern.Notes[72] = new Note(56) { Instrument = lead, Duration = 20, Release = 14 };
        foreach (int rate in new[] { 1, 2, 4 })
        {
            project.UpdateRateMultiplier = rate;
            string assembly = Export(project, Path.Combine(output, $"music-{rate}.s"));
            Check(assembly.Contains("music_data_uniformclockdemo:"), "demo retains shared symbol");
            Check(new ProjectFile().Save(project, Path.Combine(output, $"music-{rate}.fms")), "demo project saves " + rate);
        }
    }
}
