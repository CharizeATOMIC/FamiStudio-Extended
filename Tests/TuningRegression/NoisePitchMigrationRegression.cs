using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FamiStudio;

static class NoisePitchMigrationRegression
{
    sealed class Capture : BasePlayer
    {
        readonly Dictionary<int, int> state = new();
        readonly List<(int, int)> pending = new();
        public readonly List<Dictionary<int, int>> Frames = new();
        public readonly List<(int, int)[]> Writes = new();
        public readonly List<short> Audio = new();
        public Capture(bool pal) : base(NesApu.APU_WAV_EXPORT, pal, false) { Loop = LoopMode.None; }
        public override void NotifyRegisterWrite(int apu, int register, int value, int metadata = 0)
        { state[register] = value; pending.Add((register, value)); }
        protected override short[] EndFrame()
        {
            var samples = base.EndFrame(); Audio.AddRange(samples);
            Frames.Add(new(state)); Writes.Add(pending.ToArray()); pending.Clear(); return samples;
        }
        public void Render(Song song)
        { try { BeginPlaySong(song); pending.Clear(); while (PlaySongFrame()) { } } finally { Shutdown(); } }
    }

    static int checks;
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS noise pitch migration: " + message); }
    static byte[] Model(Project project)
    { var buffer = new ProjectSaveBuffer(project); project.Serialize(buffer); return buffer.GetBuffer(); }
    static byte[] StoredData(Project project)
    {
        // Channel.Serialize removes unused patterns, even for UndoRedo. Snapshot
        // instrument/arpeggio data and each stored pattern directly instead.
        var buffer = new ProjectSaveBuffer(project);
        project.SerializeInstrumentState(buffer); project.SerializeArpeggioState(buffer);
        foreach (var song in project.Songs)
        foreach (var channel in song.Channels)
        {
            int count = channel.Patterns.Count; buffer.Serialize(ref count);
            foreach (var pattern in channel.Patterns) pattern.Serialize(buffer);
            foreach (var instance in channel.PatternInstances)
            { int id = instance?.Id ?? -1; buffer.Serialize(ref id); }
        }
        return buffer.GetBuffer();
    }
    static void Macro(Envelope envelope, params sbyte[] values)
    { envelope.Length = values.Length; envelope.Loop = -1; Array.Copy(values, envelope.Values, values.Length); }
    static Project Make(out Instrument instrument, out Pattern pattern)
    {
        var project = new Project(); instrument = project.CreateInstrument(0, "Legacy noise");
        instrument.NoisePeriodFollowsNote = true; Macro(instrument.NoisePeriodEnvelope, 9, 2, 0, 15);
        var song = project.CreateSong("Noise pitch migration"); song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        song.SetDefaultPatternLength(48); song.SetBeatLength(4); song.SetLength(1); song.SetLoopPoint(-1);
        pattern = song.GetChannelByType(ChannelType.Noise).GetOrCreatePattern(0);
        pattern.Notes[0] = new Note(49) { Instrument = instrument, Duration = 48 };
        return project;
    }
    static Capture Render(Project project)
    { var capture = new Capture(project.PalMode); capture.Render(project.Songs[0]); return capture; }
    static IEnumerable<(int Tick, int Value)> Pulses(Capture capture) => capture.Writes.SelectMany((w, f) => w.Where(v => v.Item1 == 0x400f).Select(v => (f, v.Item2)));
    static void Rejected(Project p, Instrument i, NoisePitchMigrationFailure reason, string label)
    {
        var before = StoredData(p);
        var actual = NoisePitchMigration.GetFailure(p, i);
        Check(actual == reason, label + " query gives explicit reason: " + actual + " (expected " + reason + ")");
        Check(!NoisePitchMigration.TryConvert(p, i, out var failure) && failure == reason, label + " refuses conversion");
        Check(before.SequenceEqual(StoredData(p)), label + " leaves instruments, arpeggios and all stored pattern data unchanged");
    }

    static void Boundaries(string output)
    {
        var p = Make(out var i, out var pattern); var bytes = Model(p);
        Check(NoisePitchMigration.GetFailure(p, i) == NoisePitchMigrationFailure.None, "following noise-only instrument is convertible");
        Check(bytes.SequenceEqual(Model(p)), "successful eligibility query is read only");
        i.NoisePeriodEnvelope.Loop = 1; p.UpdateRateMultiplier = 4; i.NoisePeriodEnvelope.TickRateMultiplier = 2;
        Check(NoisePitchMigration.TryConvert(p, i, out var failure) && failure == NoisePitchMigrationFailure.None, "explicit migration succeeds");
        Check(i.ArpeggioEnvelope.Values.Take(i.ArpeggioEnvelope.Length).SequenceEqual(new sbyte[] { 5, 12, 14, -1 }), "native arpeggio is exactly 14 minus legacy period");
        Check(i.ArpeggioEnvelope.Loop == 1 && i.ArpeggioEnvelope.TickRateMultiplier == 2 && !i.ArpeggioEnvelope.Relative, "loop and independent macro clock are preserved");
        Check(i.NoisePeriodEnvelope.Length == 0 && !i.NoisePeriodFollowsNote && pattern.Notes[0].Value == 49, "legacy override is disabled without rewriting score notes");
        Rejected(p, i, NoisePitchMigrationFailure.NoLegacyPeriod, "already migrated instrument");
        string binary = Path.Combine(output, "native-noise.fms"), text = Path.Combine(output, "native-noise.txt");
        Check(new ProjectFile().Save(p, binary), "native result saves in the existing format");
        var binaryLoaded = new ProjectFile().Load(binary);
        Check(Model(p).SequenceEqual(Model(binaryLoaded)), "binary round trip retains the complete migrated model");
        Check(new FamistudioTextFile().Save(p, text, new[] { p.Songs[0].Id }, false), "native result exports to text");
        var textLoaded = new FamistudioTextFile().Load(text);
        Check(Render(p).Audio.SequenceEqual(Render(textLoaded).Audio), "text round trip retains exact editor PCM");
        Check(File.ReadAllLines(text).Where(l => l.Contains("Type=\"NoisePeriod\"")).All(l => l.Contains("Length=\"0\"")) && !File.ReadAllText(text).Contains("NoisePeriodFollowsNote="), "text has no active legacy pitch representation");
        Check(Model(p).SequenceEqual(Model(p.DeepClone())), "clone retains migrated envelopes");

        p = Make(out i, out pattern); bytes = Model(p);
        var undo = new ProjectSaveBuffer(p, ProjectBufferFlags.UndoRedo); p.Serialize(undo);
        NoisePitchMigration.TryConvert(p, i, out _);
        p.Serialize(new ProjectLoadBuffer(p, undo.GetBuffer(), Project.Version, ProjectBufferFlags.UndoRedo));
        Check(bytes.SequenceEqual(Model(p)), "undo restores the complete legacy model");

        p = Make(out i, out pattern); i.NoisePeriodFollowsNote = false;
        Check(NoisePitchMigration.GetFailure(p, i) == NoisePitchMigrationFailure.None, "fixed periods can migrate when every authored note is C4");
        pattern.Notes[0].Value = 50; Rejected(p, i, NoisePitchMigrationFailure.FixedPeriodNeedsC4, "fixed period on another key");
        pattern.Notes[0].Value = 49; pattern.Notes[0].SlideNoteTarget = 52;
        Rejected(p, i, NoisePitchMigrationFailure.FixedPeriodNeedsC4, "fixed period with slide");

        p = Make(out i, out pattern); Macro(i.ArpeggioEnvelope, 0, 1);
        Rejected(p, i, NoisePitchMigrationFailure.NonzeroInstrumentArpeggio, "existing nonzero arpeggio");
        p = Make(out i, out pattern); pattern.Notes[0].Arpeggio = p.CreateArpeggio();
        Rejected(p, i, NoisePitchMigrationFailure.IndependentArpeggio, "even an all-zero independent arpeggio overrides the instrument");
        p = Make(out i, out pattern); pattern.Notes[5] = new Note { Arpeggio = p.CreateArpeggio(), Volume = 12 };
        Rejected(p, i, NoisePitchMigrationFailure.IndependentArpeggio, "effect-only event with an inherited instrument and arpeggio");
        p = Make(out i, out pattern); i.NoisePeriodFollowsNote = false;
        pattern.Notes[5] = new Note(50) { Duration = 12 };
        Rejected(p, i, NoisePitchMigrationFailure.FixedPeriodNeedsC4, "different key inherits the fixed-period instrument");
        p = Make(out i, out pattern); i.NoisePeriodEnvelope.Values[0] = 16;
        Rejected(p, i, NoisePitchMigrationFailure.UnsupportedMacro, "invalid legacy hardware value");

        p = Make(out i, out pattern);
        var unused = p.Songs[0].GetChannelByType(ChannelType.Square1).CreatePattern();
        unused.Notes[200] = new Note(49) { Instrument = i, Duration = 1 };
        Rejected(p, i, NoisePitchMigrationFailure.SharedWithOtherChannels, "unused melodic pattern outside current length");
        p = Make(out i, out pattern);
        var variant = p.Songs[0].GetChannelByType(ChannelType.Noise).CreatePattern();
        variant.Notes[0] = new Note(49) { Instrument = i, Duration = 1, Arpeggio = p.CreateArpeggio() };
        pattern.LoopVariant = variant;
        Rejected(p, i, NoisePitchMigrationFailure.IndependentArpeggio, "loop-variant independent arpeggio");

        p = Make(out i, out pattern); var duplicate = p.DuplicateInstrument(i);
        pattern.Notes[12] = new Note(49) { Instrument = duplicate, Duration = 12, HasAttack = false };
        Rejected(p, i, NoisePitchMigrationFailure.NoAttackCompatibility, "conversion would change an equivalent-instrument tied switch");
        Macro(duplicate.VolumeEnvelope, 14, 13);
        Check(NoisePitchMigration.GetFailure(p, i) == NoisePitchMigrationFailure.None, "already distinct envelopes keep their existing attack behavior");

        for (int key = 1; key <= 96; key++)
        for (int raw = 0; raw < 16; raw++)
        {
            int normal = (key & 15) ^ 15;
            Check(((raw + normal - 14) & 15) == (((key + 14 - raw) & 15) ^ 15), $"algebra matches key {key}, legacy period {raw}");
        }
    }

    static Project Scenario(string scenario, bool pal, int rate)
    {
        var p = Make(out var i, out var pattern); p.PalMode = pal;
        i.NoisePeriodEnvelope.Loop = 1;
        Macro(i.NoiseControlEnvelope, 0x3f, 0x3a, 0x37, 0x33); i.NoiseControlEnvelope.Loop = 1;
        Macro(i.NoiseRestartEnvelope, 2, 2, 0, 0); i.NoiseRestartEnvelope.Loop = 0;
        Macro(i.Envelopes[EnvelopeType.DutyCycle], 0, 1, 0); i.Envelopes[EnvelopeType.DutyCycle].Loop = 0;
        pattern.Notes[0].Duration = 12;
        pattern.Notes[12] = new Note(55) { Instrument = i, Duration = 12, HasAttack = false };
        pattern.Notes[24] = new Note(42) { Instrument = i, Duration = 12 };
        pattern.Notes[36] = new Note(Note.NoteStop);
        if (scenario == "slides") { pattern.Notes[0].SlideNoteTarget = 58; pattern.Notes[24].SlideNoteTarget = 33; }
        if (scenario == "constant-zero") Macro(i.NoisePeriodEnvelope, 0);
        if (scenario == "zero-arpeggio") Macro(i.NoisePeriodEnvelope, 14, 14, 14);
        if (scenario == "fixed")
        { i.NoisePeriodFollowsNote = false; foreach (var note in pattern.Notes.Values.Where(n => n.IsMusical)) note.Value = 49; }
        p.UpdateRateMultiplier = rate;
        if (scenario == "fast-macros")
            foreach (var env in i.Envelopes.Where(e => e != null && e.Length > 0)) env.TickRateMultiplier = rate;
        return p;
    }

    static (List<Dictionary<int, int>> Frames, List<(int, int)> Pulses) Cpu(Project p, string filename, int frameCount)
    {
        Check(new NsfFile().Save(p, FamiToneKernel.FamiStudio, filename, new[] { p.Songs[0].Id }, "Noise pitch migration", "Original test", "", p.PalMode ? MachineType.PAL : MachineType.NTSC, false), "NSF exports " + Path.GetFileName(filename));
        var handle = NotSoFatso.NsfOpen(filename); if (handle == IntPtr.Zero) throw new Exception("NSF open failed.");
        var state = new Dictionary<int, int>(); var pulses = new List<(int, int)>(); var frames = new List<Dictionary<int, int>>(); int tick = -1;
        NotSoFatso.WriteRegisterDelegate callback = (register, value) =>
        { state[register] = value; if (register == 0x400f && tick >= 0) pulses.Add((tick, value)); };
        try
        {
            NotSoFatso.NsfSetApuWriteCallback(handle, callback); NotSoFatso.NsfSetTrack(handle, 0); NotSoFatso.NsfRunFrame(handle);
            for (tick = 0; tick < frameCount; tick++) { NotSoFatso.NsfRunFrame(handle); frames.Add(new(state)); }
        }
        finally { NotSoFatso.NsfClose(handle); GC.KeepAlive(callback); }
        return (frames, pulses);
    }
    static bool NoiseEqual(List<Dictionary<int, int>> a, List<Dictionary<int, int>> b)
    {
        static int Control(int v) => (v & 0x1f) == 0x10 ? 0x10 : v & 0x3f;
        return a.Count == b.Count && Enumerable.Range(0, a.Count).All(f => a[f].GetValueOrDefault(0x400e) == b[f].GetValueOrDefault(0x400e) && Control(a[f].GetValueOrDefault(0x400c)) == Control(b[f].GetValueOrDefault(0x400c)));
    }
    static void Playback(string output, Project p, string label)
    {
        var before = Render(p);
        var beforeCpu = Cpu(p, Path.Combine(output, label + "-legacy.nsf"), before.Frames.Count);
        Check(NoisePitchMigration.TryConvert(p, p.Instruments[0], out var failure), "convert playback fixture " + label + ": " + failure);
        var after = Render(p);
        Check(before.Audio.SequenceEqual(after.Audio), "bit-identical editor PCM " + label);
        Check(NoiseEqual(before.Frames, after.Frames) && Pulses(before).SequenceEqual(Pulses(after)), "editor periods, mode, control and every restart event match " + label);
        var afterCpu = Cpu(p, Path.Combine(output, label + "-native.nsf"), after.Frames.Count);
        Check(NoiseEqual(beforeCpu.Frames, afterCpu.Frames) && beforeCpu.Pulses.SequenceEqual(afterCpu.Pulses), "real NSF CPU retains periods, mode, control and restart events " + label);
        Check(NoiseEqual(after.Frames, afterCpu.Frames) && Pulses(after).SequenceEqual(afterCpu.Pulses), "native NSF agrees with editor on every output tick " + label);
    }
    public static void Run(string output)
    {
        checks = 0; Directory.CreateDirectory(output); Boundaries(output);
        foreach (bool pal in new[] { false, true })
        foreach (int rate in new[] { 1, 2, 4 })
        foreach (string scenario in new[] { "loop-and-tie", "slides", "constant-zero", "zero-arpeggio", "fixed", "fast-macros" })
            Playback(output, Scenario(scenario, pal, rate), $"{scenario}-{(pal ? "pal" : "ntsc")}-{rate}x");
        Console.WriteLine($"PASS all {checks} noise-pitch-migration checks");
    }
}
