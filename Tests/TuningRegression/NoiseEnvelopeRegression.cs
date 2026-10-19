using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FamiStudio;

static class NoiseEnvelopeRegression
{
    sealed class NoiseCapture : BasePlayer
    {
        readonly List<(int register, int value)> pending = new();
        readonly Dictionary<int, int> state = new();
        public readonly List<(int register, int value)[]> Writes = new();
        public readonly List<Dictionary<int, int>> Frames = new();
        public readonly List<short> Audio = new();
        public NoiseCapture(bool pal = false) : base(NesApu.APU_WAV_EXPORT, pal, false) { Loop = LoopMode.None; }
        public override void NotifyRegisterWrite(int apu, int register, int value, int metadata = 0)
        {
            pending.Add((register, value)); state[register] = value;
        }
        protected override short[] EndFrame()
        {
            var samples = base.EndFrame(); Audio.AddRange(samples);
            Writes.Add(pending.ToArray()); pending.Clear(); Frames.Add(new(state));
            return samples;
        }
        public void Render(Song song) { BeginPlaySong(song); pending.Clear(); while (PlaySongFrame()) { } Shutdown(); }
    }

    static int checks;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS noise: " + message); }
    static void Macro(Instrument instrument, int type, params int[] values)
    {
        var e = instrument.Envelopes[type]; e.Length = values.Length;
        for (int i = 0; i < values.Length; i++) e.Values[i] = (sbyte)values[i];
    }
    static Project Make(out Instrument instrument, out Pattern pattern, int frames = 16)
    {
        var p = new Project(); instrument = p.CreateInstrument(ExpansionType.None, "Hardware noise");
        var song = p.CreateSong("Noise macros"); song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        song.SetDefaultPatternLength(frames); song.SetBeatLength(4); song.SetLength(1); song.SetLoopPoint(-1);
        pattern = song.GetChannelByType(ChannelType.Noise).GetOrCreatePattern(0);
        pattern.Notes[0] = new Note(9) { Instrument = instrument, Duration = frames };
        return p;
    }
    static NoiseCapture Render(Project p, bool pal = false) { var player = new NoiseCapture(pal); player.Render(p.Songs[0]); return player; }
    static int[] RestartFrames(NoiseCapture player) => player.Writes.SelectMany((w, frame) => w.Where(v => v.register == 0x400f).Select(v => frame)).ToArray();

    static void CheckNsf(string output, string label, Project project, bool pal)
    {
        var editor = Render(project, pal);
        string filename = Path.Combine(output, $"noise-{label}-{(pal ? "pal" : "ntsc")}.nsf");
        Check(new NsfFile().Save(project, FamiToneKernel.FamiStudio, filename, new[] { project.Songs[0].Id }, "Noise macros", "Test", "", pal ? MachineType.PAL : MachineType.NTSC, false), $"NSF exports {label}, PAL {pal}");
        var handle = NotSoFatso.NsfOpen(filename);
        Check(handle != IntPtr.Zero, $"NSF opens {label}, PAL {pal}");
        var current = new Dictionary<int, int>();
        var pending = new List<(int register, int value)>();
        var frames = new List<Dictionary<int, int>>();
        var writes = new List<(int register, int value)[]>();
        NotSoFatso.WriteRegisterDelegate callback = (register, value) =>
        {
            if (register != 0x400c && register != 0x400e && register != 0x400f) return;
            current[register] = value; pending.Add((register, value));
        };
        try
        {
            NotSoFatso.NsfSetApuWriteCallback(handle, callback);
            NotSoFatso.NsfSetTrack(handle, 0);
            // NotSoFatso executes the NSF INIT routine on its first frame.
            NotSoFatso.NsfRunFrame(handle); pending.Clear();
            for (int frame = 0; frame < editor.Frames.Count; frame++)
            {
                NotSoFatso.NsfRunFrame(handle); frames.Add(new(current));
                writes.Add(pending.ToArray()); pending.Clear();
            }
        }
        finally { NotSoFatso.NsfClose(handle); GC.KeepAlive(callback); }
        static int Control(int value) => (value & 0x1f) == 0x10 ? 0x10 : value & 0x3f;
        bool controls = Enumerable.Range(0, frames.Count).All(f => frames[f].TryGetValue(0x400c, out int c) && Control(c) == Control(editor.Frames[f][0x400c]));
        bool periods = Enumerable.Range(0, frames.Count).All(f => frames[f].TryGetValue(0x400e, out int e) && e == editor.Frames[f][0x400e]);
        var enginePulses = writes.SelectMany((w, f) => w.Where(v => v.register == 0x400f).Select(v => (frame: f, value: v.value))).ToArray();
        var editorPulses = editor.Writes.SelectMany((w, f) => w.Where(v => v.register == 0x400f).Select(v => (frame: f, value: v.value))).ToArray();
        if (!controls || !periods || !enginePulses.SequenceEqual(editorPulses))
        {
            File.WriteAllLines(filename + ".registers.txt", Enumerable.Range(0, frames.Count).Select(f => $"{f}: editor C={editor.Frames[f].GetValueOrDefault(0x400c):X2} E={editor.Frames[f].GetValueOrDefault(0x400e):X2} F=[{string.Join(',', editor.Writes[f].Where(w => w.register == 0x400f).Select(w => w.value))}] / NSF C={frames[f].GetValueOrDefault(0x400c):X2} E={frames[f].GetValueOrDefault(0x400e):X2} F=[{string.Join(',', writes[f].Where(w => w.register == 0x400f).Select(w => w.value))}]"));
        }
        Check(controls, $"NSF CPU exact hardware-control sequence: {label}, PAL {pal}");
        Check(periods, $"NSF CPU exact period/mode sequence: {label}, PAL {pal}");
        Check(enginePulses.SequenceEqual(editorPulses), $"NSF CPU exact restart frames/lengths: {label}, PAL {pal}");
    }

    static void CheckNsfMatrix(string output)
    {
        foreach (bool pal in new[] { false, true })
        foreach (string label in new[] { "legacy", "tail-pulse", "consecutive", "zero", "loop", "no-attack", "re-attack", "control-only", "legacy-transition", "stop" })
        {
            var p = Make(out var i, out var pattern, 24);
            if (label != "legacy")
            {
                Macro(i, EnvelopeType.NoisePeriod, 2, 4, 6, 8, 10, 12, 14);
                Macro(i, EnvelopeType.NoiseEnvelope, 0x0f, 0x0e, 0x2d, 0x3c);
                Macro(i, EnvelopeType.DutyCycle, 0, 1, 0);
                Macro(i, EnvelopeType.NoiseEnvelopeRestart, 0, 0, 2);
                if (label == "consecutive") Macro(i, EnvelopeType.NoiseEnvelopeRestart, 1, 32, 2, 2);
                if (label == "zero") Macro(i, EnvelopeType.NoiseEnvelopeRestart, 0);
                if (label == "loop") { Macro(i, EnvelopeType.NoiseEnvelopeRestart, 2, 0); i.NoiseRestartEnvelope.Loop = 0; }
                if (label == "control-only") i.NoiseRestartEnvelope.Length = 0;
                if (label is "no-attack" or "re-attack" or "legacy-transition")
                {
                    pattern.Notes[0].Duration = 2;
                    pattern.Notes[2] = new Note(5) { Instrument = label == "legacy-transition" ? p.CreateInstrument(ExpansionType.None, "Plain") : i, Duration = 22, HasAttack = label != "no-attack" };
                }
                if (label == "stop") pattern.Notes[4] = new Note(Note.NoteStop);
            }
            CheckNsf(output, label, p, pal);
        }
    }

    public static int Run(string output)
    {
        checks = 0; output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var p = Make(out var instrument, out var pattern);
        foreach (int type in new[] { EnvelopeType.NoisePeriod, EnvelopeType.NoiseEnvelope, EnvelopeType.NoiseEnvelopeRestart })
        {
            Check(instrument.Envelopes[type].Length == 0 && instrument.Envelopes[type].IsEmpty(type), "new hardware macro is disabled by default: " + type);
            Macro(instrument, type, 0);
            Check(!instrument.Envelopes[type].IsEmpty(type), "all-zero nonempty macro remains active: " + type);
            instrument.Envelopes[type].ResetToDefault(type);
            Check(instrument.Envelopes[type].Length == 0, "reset disables optional macro: " + type);
        }
        foreach (int chip in Enumerable.Range(1, ExpansionType.Count - 1))
        {
            var other = new Instrument(p, 100 + chip, chip, "Other");
            Check(other.NoiseControlEnvelope == null && other.NoisePeriodEnvelope == null && other.NoiseRestartEnvelope == null, "noise macros apply only to base APU instruments: " + chip);
        }
        var legacy = Render(p);
        Check(RestartFrames(legacy).Length == 0, "default playback adds no timer-high writes");
        Check(legacy.Frames.All(f => f[0x400e] == 6 && f[0x400c] == 0x3f), "default note and volume paths are unchanged");
        Macro(instrument, EnvelopeType.NoisePeriod, 0, 15, 8);
        Macro(instrument, EnvelopeType.NoiseEnvelope, 0, 63, 47);
        Macro(instrument, EnvelopeType.NoiseEnvelopeRestart, 1, 32);
        Macro(instrument, EnvelopeType.DutyCycle, 1);
        pattern.Notes[0].Volume = 1;
        var active = Render(p);
        Check(active.Frames[0][0x400e] == 0x80 && active.Frames[1][0x400e] == 0x8f && active.Frames[2][0x400e] == 0x88, "absolute noise periods preserve the mode bit");
        Check(active.Frames[0][0x400c] == 0 && active.Frames[1][0x400c] == 63 && active.Frames[2][0x400c] == 47, "hardware control keeps all six bits despite ordinary note volume");
        Check(RestartFrames(active).SequenceEqual(new[] { 0, 1 }), "nonlooping restart pulses occur only on the two requested frames");
        Check(active.Writes[0].Any(w => w == (0x400f, 0)) && active.Writes[1].Any(w => w == (0x400f, 248)), "restart values 1 and 32 encode both length-table endpoints");
        Macro(instrument, EnvelopeType.NoiseEnvelopeRestart, 2, 0); instrument.NoiseRestartEnvelope.Loop = 0;
        var loop = Render(p);
        Check(RestartFrames(loop).SequenceEqual(Enumerable.Range(0, 8).Select(i => i * 2)), "explicit restart loop repeats its pulses");
        Macro(instrument, EnvelopeType.NoiseEnvelopeRestart, 0); instrument.NoiseRestartEnvelope.Loop = -1;
        Check(RestartFrames(Render(p)).Length == 0, "explicit all-zero restart suppresses automatic retriggering");
        instrument.NoiseRestartEnvelope.Length = 0;
        Check(RestartFrames(Render(p)).SequenceEqual(new[] { 0 }), "hardware control without restart macro restarts once on attack");
        Check(Render(p).Writes[0].Contains((0x400f, 8)), "automatic retrigger uses the longest length-table entry");

        Macro(instrument, EnvelopeType.NoisePeriod, 1, 2, 3, 4, 5);
        Macro(instrument, EnvelopeType.NoiseEnvelopeRestart, 2, 0, 0, 2);
        pattern.Notes[0].Duration = 2;
        pattern.Notes[2] = new Note(5) { Instrument = instrument, Duration = 14, HasAttack = false };
        var tied = Render(p);
        Check(tied.Frames[2][0x400e] == 0x83 && RestartFrames(tied).SequenceEqual(new[] { 0, 3 }), "no-attack note preserves macro positions and restart timing");
        pattern.Notes[2].HasAttack = true;
        var attacked = Render(p);
        Check(attacked.Frames[2][0x400e] == 0x81 && RestartFrames(attacked).SequenceEqual(new[] { 0, 2, 5 }), "new attack restarts the hardware macros");
        var plain = p.CreateInstrument(ExpansionType.None, "Legacy noise");
        pattern.Notes[2].Instrument = plain;
        var restored = Render(p);
        Check(restored.Writes[2].Contains((0x400f, 8)) && restored.Frames[2][0x400c] == 0x31, "return to a legacy instrument restores length and inherited ordinary volume");
        pattern.Notes[4] = new Note(Note.NoteStop);
        var stopped = Render(p);
        Check(stopped.Frames[4][0x400c] == 0xf0 && stopped.Writes.Skip(4).All(w => !w.Any(v => v.register == 0x400f)), "note stop gates hardware noise and emits no further restart pulses");

        var clone = p.DeepClone();
        Check(clone.Instruments[0].ComputeCRC() == instrument.ComputeCRC(), "clone preserves all optional macro data");
        var undo = new ProjectSaveBuffer(p, ProjectBufferFlags.UndoRedo); p.Serialize(undo);
        instrument.NoisePeriodEnvelope.Values[0] = 14;
        p.Serialize(new ProjectLoadBuffer(p, undo.GetBuffer(), Project.Version, ProjectBufferFlags.UndoRedo));
        Check(p.Instruments[0].NoisePeriodEnvelope.Values[0] == 1, "undo restores hardware-noise values");
        string filename = Path.Combine(output, "noise-current-format.fms");
        Check(new ProjectFile().Save(p, filename), "current format saves hardware-noise envelopes");
        var loaded = new ProjectFile().Load(filename);
        Check(loaded != null && loaded.Instruments[0].ComputeCRC() == p.Instruments[0].ComputeCRC(), "binary round trip preserves macro lengths, loops and values");
        Check(BitConverter.ToInt32(File.ReadAllBytes(filename), 4) == Project.Version, "new noise files declare the current project format");
        foreach (string fixture in new[] { "format1001.fms", "format1002.fms" })
        {
            var old = new ProjectFile().Load(Path.Combine("Tests", "TuningRegression", "Fixtures", fixture));
            Check(old != null && old.Instruments.Where(i => i.IsRegular).All(i => i.NoiseControlEnvelope.Length == 0 && i.NoisePeriodEnvelope.Length == 0 && i.NoiseRestartEnvelope.Length == 0), "old project gets disabled noise macros: " + fixture);
        }
        foreach (int type in new[] { EnvelopeType.NoisePeriod, EnvelopeType.NoiseEnvelope, EnvelopeType.NoiseEnvelopeRestart })
        {
            Envelope.GetMinMaxValueForType(instrument, type, out int min, out int max);
            Check(min == 0 && max == (type == EnvelopeType.NoisePeriod ? 15 : type == EnvelopeType.NoiseEnvelope ? 63 : 32), "editable numeric range matches hardware semantics: " + type);
        }
        Check(Envelope.GetDisplayValue(instrument, EnvelopeType.NoiseEnvelope, 0) != "0" && Envelope.GetDisplayValue(instrument, EnvelopeType.NoiseEnvelopeRestart, 2).Contains("254"), "value labels explain decay and actual length ticks");
        CheckNsfMatrix(output);
        Console.WriteLine($"All {checks} noise-envelope checks passed.");
        return checks;
    }
}
