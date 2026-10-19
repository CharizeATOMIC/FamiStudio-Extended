using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FamiStudio;

static class NoiseFollowRegression
{
    sealed class OldWriter : ProjectSaveBuffer, ProjectBuffer
    {
        public OldWriter(Project p) : base(p) { }
        int ProjectBuffer.Version => 1006;
    }
    sealed class Capture : BasePlayer
    {
        readonly Dictionary<int, int> state = new();
        readonly List<(int, int)> pending = new();
        public readonly List<Dictionary<int, int>> Frames = new();
        public readonly List<(int, int)[]> Writes = new();
        public Capture(bool pal) : base(NesApu.APU_WAV_EXPORT, pal, false) { Loop = LoopMode.None; }
        public override void NotifyRegisterWrite(int apu, int register, int value, int metadata = 0)
        { state[register] = value; pending.Add((register, value)); }
        protected override short[] EndFrame()
        {
            var samples = base.EndFrame(); Frames.Add(new(state)); Writes.Add(pending.ToArray()); pending.Clear(); return samples;
        }
        public void Render(Song song) { BeginPlaySong(song); pending.Clear(); while (PlaySongFrame()) { } Shutdown(); }
    }
    static int checks;
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS noise follow: " + message); }
    static void Macro(Envelope env, params sbyte[] values)
    { env.Length = values.Length; env.Loop = -1; Array.Copy(values, env.Values, values.Length); }
    static Project Make(out Instrument instrument, out Pattern pattern, int length = 48)
    {
        var p = new Project(); instrument = p.CreateInstrument(0, "Noise period keyboard");
        var song = p.CreateSong("Noise period keyboard"); song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        song.SetDefaultPatternLength(length); song.SetBeatLength(4); song.SetLength(1); song.SetLoopPoint(-1);
        pattern = song.GetChannelByType(ChannelType.Noise).GetOrCreatePattern(0);
        pattern.Notes[0] = new Note(49) { Instrument = instrument, Duration = length };
        return p;
    }
    static Capture Render(Project p) { var c = new Capture(p.PalMode); c.Render(p.Songs[0]); return c; }
    static int[] Periods(Capture c) => c.Frames.Select(f => f.GetValueOrDefault(0x400e)).ToArray();
    static IEnumerable<(int Tick, int Value)> Pulses(Capture c) => c.Writes.SelectMany((w, f) => w.Where(v => v.Item1 == 0x400f).Select(v => (f, v.Item2)));

    static void Model(string output)
    {
        var p = Make(out var instrument, out _); Macro(instrument.NoisePeriodEnvelope, 0, 15, 3);
        Check(!instrument.NoisePeriodFollowsNote, "new instruments retain fixed hardware periods by default");
        uint fixedCrc = instrument.ComputeCRC(); instrument.NoisePeriodFollowsNote = true;
        Check(instrument.ComputeCRC() != fixedCrc, "instrument CRC distinguishes fixed and following periods");
        Check(p.DeepClone().Instruments[0].NoisePeriodFollowsNote, "project clone retains mode");
        var undo = new ProjectSaveBuffer(p, ProjectBufferFlags.UndoRedo); p.Serialize(undo);
        instrument.NoisePeriodFollowsNote = false;
        p.Serialize(new ProjectLoadBuffer(p, undo.GetBuffer(), Project.Version, ProjectBufferFlags.UndoRedo));
        instrument = p.Instruments[0]; Check(instrument.NoisePeriodFollowsNote, "undo restores mode");
        var old = new OldWriter(p); instrument.Serialize(old);
        instrument.Serialize(new ProjectLoadBuffer(p, old.GetBuffer(), 1006));
        Check(!instrument.NoisePeriodFollowsNote, "reading actual format-1006 layout clears mode on an existing object");
        instrument.NoisePeriodFollowsNote = true;
        string binary = Path.Combine(output, "noise-follow.fms"), text = Path.Combine(output, "noise-follow.txt");
        Check(new ProjectFile().Save(p, binary), "format 1007 saves");
        Check(new ProjectFile().Load(binary)?.Instruments[0].NoisePeriodFollowsNote == true, "binary round trip preserves mode");
        Check(new FamistudioTextFile().Save(p, text, new[] { p.Songs[0].Id }, false), "text saves mode");
        var loaded = new FamistudioTextFile().Load(text);
        Check(loaded?.Instruments[0].NoisePeriodFollowsNote == true, "text round trip preserves mode");
        Check(Periods(Render(p)).SequenceEqual(Periods(Render(loaded))), "text round trip preserves playback");
        instrument.NoisePeriodFollowsNote = false;
        Check(new FamistudioTextFile().Save(p, text, new[] { p.Songs[0].Id }, false) && !File.ReadAllText(text).Contains("NoisePeriodFollowsNote="), "default mode needs no text attribute");
        Check(!new FamistudioTextFile().Load(text).Instruments[0].NoisePeriodFollowsNote, "missing text attribute remains fixed");
        instrument.NoisePeriodFollowsNote = true;
        var duplicate = p.DuplicateInstrument(instrument); duplicate.NoisePeriodFollowsNote = false;
        p.DuplicateInstrument(instrument); p.MergeIdenticalInstruments();
        Check(p.Instruments.Count == 2 && p.Instruments.Select(i => i.NoisePeriodFollowsNote).Distinct().Count() == 2, "deduplication merges equal modes and keeps different modes separate");
        var destination = new Project();
        Check(destination.MergeProject(p.DeepClone()) && destination.Instruments.Any(i => i.NoisePeriodFollowsNote), "project import retains mode");
        var clipboard = new ProjectSaveBuffer(p, ProjectBufferFlags.Clipboard);
        typeof(ClipboardUtils).GetMethod("SaveInstrumentList", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { clipboard, new HashSet<Instrument> { instrument } });
        destination = new Project();
        typeof(ClipboardUtils).GetMethod("LoadAndMergeInstrumentList", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new ProjectLoadBuffer(destination, clipboard.GetBuffer(), Project.Version, ProjectBufferFlags.Clipboard), false, ClipboardImportFlags.CreateMissing });
        Check(destination.Instruments.Single().NoisePeriodFollowsNote, "clipboard import retains mode");
        foreach (string file in Directory.GetFiles(Path.Combine("Tests", "TuningRegression", "Fixtures"), "*.fms"))
        {
            var fixture = new ProjectFile().Load(file);
            if (fixture != null) Check(fixture.Instruments.All(i => !i.NoisePeriodFollowsNote), "old fixture remains fixed: " + Path.GetFileName(file));
        }
    }

    static void Keyboard()
    {
        Check(Note.GetFriendlyName(49) == "C4", "reference note is the visible C4 key");
        foreach (int mode in new[] { 0, 1 })
        for (int raw = 0; raw < 16; raw++)
        {
            var p = Make(out var i, out var pattern, 32); Macro(i.NoisePeriodEnvelope, (sbyte)raw); Macro(i.Envelopes[EnvelopeType.DutyCycle], (sbyte)mode);
            pattern.Notes.Clear();
            for (int n = 0; n < 16; n++) pattern.Notes[n * 2] = new Note(41 + n) { Instrument = i, Duration = 2 };
            Check(Periods(Render(p)).All(v => v == (raw | mode << 7)), $"fixed rate {raw}, mode {mode}, ignores all 16 key positions");
            i.NoisePeriodFollowsNote = true; var followed = Periods(Render(p));
            Check(followed.Select((value, frame) => value == ((((raw + (((41 + frame / 2) & 15) ^ 15) - 14) & 15)) | (mode << 7))).All(v => v), $"follow rate {raw}, mode {mode}, all 16 offsets wrap correctly");
            Check(followed[16] == (raw | mode << 7), $"C4 preserves rate {raw}, mode {mode}");
        }
        var disabled = Make(out var inactive, out _); var original = Periods(Render(disabled)); inactive.NoisePeriodFollowsNote = true;
        Check(original.SequenceEqual(Periods(Render(disabled))), "follow setting without a period macro has no effect");
    }

    static Project Scenario(string scenario, bool pal, int rate, bool follow)
    {
        var p = Make(out var i, out var pattern); p.PalMode = pal; i.NoisePeriodFollowsNote = follow;
        Macro(i.NoisePeriodEnvelope, 0, 15, 5, 9); i.NoisePeriodEnvelope.Loop = 1;
        Macro(i.NoiseControlEnvelope, 0x3f, 0x3a, 0x37, 0x33); i.NoiseControlEnvelope.Loop = 1;
        Macro(i.NoiseRestartEnvelope, 2, 2, 0, 0); i.NoiseRestartEnvelope.Loop = 0;
        Macro(i.Envelopes[EnvelopeType.DutyCycle], 0, 1, 0); i.Envelopes[EnvelopeType.DutyCycle].Loop = 0;
        pattern.Notes[0].Duration = 12;
        pattern.Notes[12] = new Note(55) { Instrument = i, Duration = 12, HasAttack = false };
        pattern.Notes[24] = new Note(42) { Instrument = i, Duration = 12 };
        pattern.Notes[36] = new Note(Note.NoteStop);
        if (scenario == "arpeggio") { Macro(i.ArpeggioEnvelope, -12, 0, 7, 16); i.ArpeggioEnvelope.Loop = 0; }
        if (scenario == "independent-arpeggio")
        {
            var arp = p.CreateArpeggio(); Macro(arp.Envelope, -16, -1, 3, 12); arp.Envelope.Loop = 0;
            foreach (var note in pattern.Notes.Values.Where(n => n.IsMusical)) note.Arpeggio = arp;
        }
        if (scenario == "slides")
        { pattern.Notes[0].SlideNoteTarget = 58; pattern.Notes[24].SlideNoteTarget = 33; }
        if (scenario == "switch-mode" || scenario == "switch-tie")
        {
            var second = p.DuplicateInstrument(i); second.NoisePeriodFollowsNote = !follow;
            pattern.Notes[12].Instrument = second; pattern.Notes[12].HasAttack = scenario == "switch-mode";
        }
        if (scenario == "constant-zero") Macro(i.NoisePeriodEnvelope, 0);
        if (scenario == "period-disabled") i.NoisePeriodEnvelope.Length = 0;
        p.UpdateRateMultiplier = rate;
        if (scenario == "fast-macros")
            foreach (var env in i.Envelopes.Where(e => e != null && e.Length > 0)) env.TickRateMultiplier = rate;
        return p;
    }

    static void Cpu(string output, Project p, string label)
    {
        var editor = Render(p); var crc = p.Instruments.Select(i => i.ComputeCRC()).ToArray();
        string filename = Path.Combine(output, label + ".nsf");
        Check(new NsfFile().Save(p, FamiToneKernel.FamiStudio, filename, new[] { p.Songs[0].Id }, "Noise follow", "Original test", "", p.PalMode ? MachineType.PAL : MachineType.NTSC, false), "NSF exports " + label);
        Check(crc.SequenceEqual(p.Instruments.Select(i => i.ComputeCRC())), "export encoding does not mutate model " + label);
        var handle = NotSoFatso.NsfOpen(filename); Check(handle != IntPtr.Zero, "NSF CPU opens " + label);
        var state = new Dictionary<int, int>(); var pulses = new List<(int, int)>(); var frames = new List<Dictionary<int, int>>(); int tick = -1;
        NotSoFatso.WriteRegisterDelegate callback = (register, value) =>
        { state[register] = value; if (register == 0x400f && tick >= 0) pulses.Add((tick, value)); };
        try
        {
            NotSoFatso.NsfSetApuWriteCallback(handle, callback); NotSoFatso.NsfSetTrack(handle, 0); NotSoFatso.NsfRunFrame(handle);
            for (tick = 0; tick < editor.Frames.Count; tick++) { NotSoFatso.NsfRunFrame(handle); frames.Add(new(state)); }
        }
        finally { NotSoFatso.NsfClose(handle); GC.KeepAlive(callback); }
        static int Volume(int v) => (v & 0x1f) == 0x10 ? 0x10 : v & 0x3f;
        var differences = Enumerable.Range(0, frames.Count).Where(f => frames[f].GetValueOrDefault(0x400e) != editor.Frames[f].GetValueOrDefault(0x400e) || Volume(frames[f].GetValueOrDefault(0x400c)) != Volume(editor.Frames[f].GetValueOrDefault(0x400c))).ToArray();
        if (differences.Length != 0) File.WriteAllLines(filename + ".differences.txt", differences.Select(f => $"{f}: editor E={editor.Frames[f].GetValueOrDefault(0x400e):X2} C={editor.Frames[f].GetValueOrDefault(0x400c):X2}; NSF E={frames[f].GetValueOrDefault(0x400e):X2} C={frames[f].GetValueOrDefault(0x400c):X2}"));
        Check(differences.Length == 0, "CPU periods/modes/control match every editor output tick " + label);
        Check(pulses.SequenceEqual(Pulses(editor)), "CPU consecutive/looped restart events match every editor tick " + label);
    }

    public static void Run(string output)
    {
        checks = 0; Directory.CreateDirectory(output); Model(output); Keyboard();
        foreach (bool pal in new[] { false, true })
        foreach (int rate in new[] { 1, 2, 4 })
        foreach (bool follow in new[] { false, true })
        foreach (string scenario in new[] { "arpeggio", "independent-arpeggio", "slides", "switch-mode", "switch-tie", "constant-zero", "period-disabled", "fast-macros" })
            Cpu(output, Scenario(scenario, pal, rate, follow), $"{scenario}-{(pal ? "pal" : "ntsc")}-{rate}x-{(follow ? "follow" : "fixed")}");
        Console.WriteLine($"PASS all {checks} noise-follow checks");
    }
}
