using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FamiStudio;

// Original generated music. Compare identical output clocks, with only the score
// resolution changed by the public transactional conversion API.
static class SongGridClockExportRegression
{
    static int checks;
    static void Check(bool value, string message)
    { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS grid export: " + message); }
    static void Macro(Envelope e, params sbyte[] values)
    { e.Length = values.Length; e.Loop = -1; Array.Copy(values, e.Values, values.Length); }

    static Project Fixture(string kind, bool pal)
    {
        var p = new Project { PalMode = pal };
        if (kind == "fds") p.SetExpansionAudioMask(ExpansionType.FdsMask);
        var s = p.CreateSong(kind);
        s.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        s.SetLength(1); s.SetDefaultPatternLength(128); s.SetBeatLength(8); s.SetLoopPoint(0);
        var i = p.CreateInstrument(kind == "fds" ? ExpansionType.Fds : ExpansionType.None, "Original clock fixture");
        Macro(i.VolumeEnvelope, 15, 13, 11, 9, 6, 3, 0); i.VolumeEnvelope.Loop = 1; i.VolumeEnvelope.Release = 4;
        Macro(i.PitchEnvelope, 0, 2, -1, 0); i.PitchEnvelope.Loop = 0;
        if (kind == "fds") { i.FdsModDelay = 5; i.FdsModDepth = 9; i.FdsModSpeed = 127; }
        else Macro(i.Envelopes[EnvelopeType.DutyCycle], 2);
        var pattern = s.GetChannelByType(kind == "fds" ? ChannelType.FdsWave : ChannelType.Square1).GetOrCreatePattern(0);
        pattern.Notes[0] = new Note(46) { Instrument = i, Duration = 90, SlideNoteTarget = 53, Volume = 15, VolumeSlideTarget = 4 };
        if (kind == "fds")
        {
            // Allow initial stream setup to complete before the wave-table
            // upload, whose combined cold-start cost exceeds a 4x CPU tick.
            pattern.Notes.Remove(0);
            pattern.Notes[4] = new Note(46) { Instrument = i, Duration = 86 };
        }
        pattern.Notes[90] = new Note(53) { Instrument = i, Duration = 34, Release = 23, VibratoSpeed = 1, VibratoDepth = 4 };
        if (kind != "fds")
        {
            var other = s.Channels[1].GetOrCreatePattern(0);
            other.Notes[0] = new Note(34) { Instrument = i, Duration = 128, VibratoSpeed = 1, VibratoDepth = 4 };
            other.Notes[17] = new Note { EnvelopeDecay = 1 };
            other.Notes[30] = new Note { EnvelopeDecay = 0 };
            other.Notes[41] = new Note { EnvelopeDecay = 3 };
            other.Notes[67] = new Note { EnvelopeDecay = 0 };
            var noise = p.CreateInstrument(ExpansionType.None, "Sparse hardware restart");
            Macro(noise.NoisePeriodEnvelope, 2, 5, 7, 3); noise.NoisePeriodEnvelope.Loop = 0;
            Macro(noise.NoiseControlEnvelope, 63, 58, 53); noise.NoiseControlEnvelope.Loop = 0;
            Macro(noise.NoiseRestartEnvelope, 2, 0, 0, 0, 2, 0); noise.NoiseRestartEnvelope.Loop = 0;
            var drums = s.GetChannelByType(ChannelType.Noise).GetOrCreatePattern(0);
            drums.Notes[0] = new Note(49) { Instrument = noise, Duration = 61 };
            drums.Notes[64] = new Note(49) { Instrument = noise, Duration = 60 };
        }
        if (kind.StartsWith("groove-"))
        {
            bool reversed = kind.EndsWith("reversed");
            s.ChangeFamiStudioTempoGroove(reversed ? new[] { 6, 5 } : new[] { 5, 6 }, false);
            s.SetDefaultPatternLength(120);
            s.SetGroovePaddingMode(kind.Contains("begin") ? GroovePaddingType.Beginning : kind.Contains("end") ? GroovePaddingType.End : GroovePaddingType.Middle);
        }
        if (kind == "delayed-variant")
        {
            pattern.PlaybackDelay = 3;
            var variant = s.Channels[0].CreatePattern("Loop variant"); variant.PlaybackDelay = 3;
            foreach (var n in pattern.Notes) variant.Notes.Add(n.Key, n.Value.Clone());
            variant.Notes[90].Value = 56; pattern.LoopVariant = variant;
        }
        if (kind == "tracker")
        {
            p.ConvertToFamiTrackerTempo(true); s.FamitrackerTempo = pal ? 125 : 150; s.FamitrackerSpeed = 2;
            pattern.Notes[90].CutDelay = 1;
            pattern.Notes[32] = new Note { Speed = 3 }; pattern.Notes[64] = new Note { Speed = 2 };
        }
        return p;
    }

    sealed class Trace
    {
        public readonly List<Dictionary<int, int>> Frames = new();
        public readonly List<(int Register, int Value)[]> Writes = new();
        public readonly List<short> Audio = new();
    }
    sealed class Player : BasePlayer
    {
        public readonly Trace Trace = new();
        readonly Dictionary<int, int> registers = new();
        readonly List<(int, int)> writes = new();
        public Player(bool pal) : base(NesApu.Create(), pal, false) { Loop = LoopMode.Song; }
        public override void NotifyRegisterWrite(int apu, int register, int value, int metadata = 0)
        { registers[register] = value; writes.Add((register, value)); }
        protected override short[] EndFrame()
        { var pcm = base.EndFrame(); Trace.Audio.AddRange(pcm); Trace.Frames.Add(new(registers)); Trace.Writes.Add(writes.ToArray()); writes.Clear(); return pcm; }
        public void Render(Song song, int ticks)
        { try { BeginPlaySong(song); for (int t = 0; t < ticks; t++) if (!PlaySongFrame()) throw new Exception("Unexpected end"); } finally { Shutdown(); NesApu.Destroy(apuIndex); } }
    }
    static Trace Editor(Project p, int ticks)
    {
        // A fresh dynamic APU gives both projects the same cold synthesis state.
        var player = new Player(p.PalMode); player.Render(p.Songs[0], ticks); return player.Trace;
    }
    static Trace Cpu(Project p, string path, int ticks)
    {
        Check(new NsfFile().Save(p, FamiToneKernel.FamiStudio, path, p.Songs.Select(s => s.Id).ToArray(), "Grid clock fixture", "Original synthetic regression", "", p.PalMode ? MachineType.PAL : MachineType.NTSC, false), "NSF exports " + Path.GetFileName(path));
        var handle = NotSoFatso.NsfOpen(path); Check(handle != IntPtr.Zero, "CPU opens " + Path.GetFileName(path));
        var trace = new Trace(); var registers = new Dictionary<int, int>(); var writes = new List<(int, int)>();
        NotSoFatso.WriteRegisterDelegate callback = (r, v) => { registers[r] = v; writes.Add((r, v)); };
        try
        {
            NotSoFatso.NsfSetApuWriteCallback(handle, callback); NotSoFatso.NsfSetTrack(handle, 0); NotSoFatso.NsfRunFrame(handle); writes.Clear();
            for (int t = 0; t < ticks; t++)
            {
                NotSoFatso.NsfRunFrame(handle);
                registers[-1] = NotSoFatso.NsfGetState(handle, ChannelType.Square1, NotSoFatso.STATE_PERIOD, 0);
                trace.Frames.Add(new(registers)); trace.Writes.Add(writes.ToArray()); writes.Clear();
            }
        }
        finally { NotSoFatso.NsfClose(handle); GC.KeepAlive(callback); }
        return trace;
    }
    static void Compare(Trace before, Trace after, string name, string output, bool audio)
    {
        int[] relevant = { 0x4000, 0x4002, 0x4003, 0x4004, 0x4006, 0x4007, 0x400c, 0x400e, 0x4080, 0x4082, 0x4083, 0x4084, 0x4085, 0x4086, 0x4087 };
        var errors = new List<string>();
        for (int t = 0; t < before.Frames.Count; t++) foreach (int r in relevant)
            if (before.Frames[t].TryGetValue(r, out int v) && after.Frames[t].GetValueOrDefault(r, -1) != v)
                errors.Add($"tick {t}: ${r:x4} {after.Frames[t].GetValueOrDefault(r, -1):x2} != {v:x2}");
        var pulsesBefore = before.Writes.SelectMany((w, t) => w.Where(v => v.Register == 0x400f).Select(v => (t, v.Value))).ToArray();
        var pulsesAfter = after.Writes.SelectMany((w, t) => w.Where(v => v.Register == 0x400f).Select(v => (t, v.Value))).ToArray();
        bool samePulses = pulsesBefore.SequenceEqual(pulsesAfter);
        bool sameAudio = !audio || before.Audio.SequenceEqual(after.Audio);
        var writeDifferences = Enumerable.Range(0, before.Writes.Count).Where(t => !before.Writes[t].SequenceEqual(after.Writes[t])).Take(8).Select(t => new { tick = t, before = before.Writes[t].Select(w => $"{w.Register:x4}:{w.Value:x2}"), after = after.Writes[t].Select(w => $"{w.Register:x4}:{w.Value:x2}") }).ToArray();
        if (errors.Count != 0 || !samePulses || !sameAudio)
            File.WriteAllText(Path.Combine(output, name + "-differences.json"), JsonSerializer.Serialize(new { errors = errors.Take(1000), samePulses, sameAudio, beforeSamples = before.Audio.Count, afterSamples = after.Audio.Count, writeDifferences, firstAudioDifference = Enumerable.Range(0, Math.Min(before.Audio.Count, after.Audio.Count)).FirstOrDefault(n => before.Audio[n] != after.Audio[n], -1) }, new JsonSerializerOptions { WriteIndented = true }));
        Check(errors.Count == 0, name + " registers retain their real timing: " + string.Join("; ", errors.Take(4)));
        Check(samePulses, name + " ordered restart pulses retain their exact tick");
        if (audio) Check(sameAudio && after.Audio.Any(v => v != 0), name + " non-silent PCM is byte-exact");
    }
    static void BeginningAlignment(string output)
    {
        foreach (bool pal in new[] { false, true })
        foreach (bool delayed in new[] { false, true })
        foreach (int rate in new[] { 1, 2, 4 })
        {
            var p = new Project { PalMode = pal };
            var s = p.CreateSong("Beginning row zero");
            s.ChangeFamiStudioTempoGroove(new[] { 6, 5 }, false); s.SetGroovePaddingMode(GroovePaddingType.Beginning);
            s.SetLength(2); s.SetDefaultPatternLength(30); s.SetBeatLength(5); s.SetLoopPoint(1);
            var i = p.CreateInstrument(ExpansionType.None, "Beginning alignment square");
            Macro(i.VolumeEnvelope, 15); Macro(i.Envelopes[EnvelopeType.DutyCycle], 2);
            for (int pat = 0; pat < 2; pat++)
            {
                var pattern = s.Channels[0].GetOrCreatePattern(pat);
                pattern.PlaybackDelay = delayed ? 2 : 0;
                foreach (int row in new[] { 0, 1, 7, 19, 29 })
                    pattern.Notes[row] = new Note((byte)(40 + row % 12 + pat)) { Instrument = i, Duration = row == 29 ? 1 : 5 };
                if (delayed && pat == 1)
                {
                    var variant = s.Channels[0].CreatePattern("Beginning loop variation"); variant.PlaybackDelay = 2;
                    foreach (var note in pattern.Notes) variant.Notes.Add(note.Key, note.Value.Clone());
                    variant.Notes[0].Value = 58; pattern.LoopVariant = variant;
                }
            }
            if (rate != 1) Check(p.TryChangeEditingRate(rate, out string error), "Beginning conversion " + rate + ": " + error);
            string name = $"beginning-alignment-{pal}-{delayed}-{rate}";
            int ticks = 150 * rate;
            var editor = Editor(p, ticks); var cpu = Cpu(p, Path.Combine(output, name + ".nsf"), ticks);
            var errors = new List<string>();
            for (int t = 0; t < ticks; t++)
            {
                int expectedVolume = editor.Frames[t].GetValueOrDefault(0x4000) & 15;
                int actualVolume = cpu.Frames[t].GetValueOrDefault(0x4000) & 15;
                int Period(Dictionary<int, int> frame) => frame.TryGetValue(-1, out int actual) ? actual : frame.GetValueOrDefault(0x4002) | ((frame.GetValueOrDefault(0x4003) & 7) << 8);
                if (expectedVolume != actualVolume || (expectedVolume > 0 && Period(editor.Frames[t]) != Period(cpu.Frames[t])))
                    errors.Add($"tick {t}: volume {actualVolume}/{expectedVolume}, period {Period(cpu.Frames[t])}/{Period(editor.Frames[t])}");
            }
            if (errors.Count != 0) File.WriteAllText(Path.Combine(output, name + "-differences.json"), JsonSerializer.Serialize(errors.Take(100)));
            Check(errors.Count == 0, name + " actual CPU matches editor from row zero through repeated loops: " + string.Join("; ", errors.Take(3)));
        }
    }
    public static void Run(string output)
    {
        Directory.CreateDirectory(output); checks = 0; Settings.SquareSmoothVibrato = false;
        foreach (bool pal in new[] { false, true })
        foreach (string kind in new[] { "apu", "fds", "groove-begin", "groove-middle", "groove-end", "groove-begin-reversed", "delayed-variant", "tracker" })
        foreach (int rate in new[] { 2, 4 })
        {
            string name = $"{kind}-{(pal ? "pal" : "ntsc")}-{rate}";
            var original = Fixture(kind, pal); original.UpdateRateMultiplier = rate;
            var converted = original.DeepClone();
            Check(converted.TryChangeEditingRate(rate, out string error), name + " public conversion succeeds: " + error);
            Check(converted.Songs[0].LogicRateMultiplier == rate && converted.Songs[0].EffectRateMultiplier == 1, name + " score resolution changes while the effect clock stays 1x");
            int ticks = (kind == "tracker" ? 620 : 300) * rate;
            var editorBefore = Editor(original, ticks);
            if (kind == "fds") Compare(editorBefore, Editor(original, ticks), name + "-repeat", output, true);
            Compare(editorBefore, Editor(converted, ticks), name + "-editor", output, true);
            Compare(Cpu(original, Path.Combine(output, name + "-before.nsf"), ticks), Cpu(converted, Path.Combine(output, name + "-after.nsf"), ticks), name + "-cpu", output, false);
        }
        BeginningAlignment(output);
        File.WriteAllText(Path.Combine(output, "song-grid-clock-report.json"), JsonSerializer.Serialize(new { passed = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS all {checks} song-grid clock export checks");
    }
}
