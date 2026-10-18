using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FamiStudio;

static class EnvelopeDecayRegression
{
    static readonly int[] ChannelTypes = { ChannelType.Square1, ChannelType.Vrc6Square1, ChannelType.Vrc7Fm1, ChannelType.FdsWave, ChannelType.Mmc5Square1, ChannelType.N163Wave1, ChannelType.S5BSquare1, ChannelType.EPSMSquare1 };
    sealed class Capture : BasePlayer
    {
        readonly Dictionary<int,int> registers = new();
        readonly ChipVolume volume;
        public readonly List<Dictionary<int,int>> Frames = new();
        public readonly List<int> Levels = new();
        public Capture(bool pal = false, bool stereo = false, int chip = 0) : base(NesApu.APU_WAV_EXPORT, pal, stereo) { Loop = LoopMode.None; volume = new ChipVolume(chip); }
        public override void NotifyRegisterWrite(int apu, int register, int value, int metadata = 0) { registers[register] = value; volume.Write(register, value); }
        protected override short[] EndFrame() { var samples = base.EndFrame(); Frames.Add(new(registers)); Levels.Add(volume.Value); return samples; }
        public void Render(Song song) { BeginPlaySong(song); while (PlaySongFrame()) {} Shutdown(); }
    }
    sealed class ChipVolume
    {
        readonly int chip;
        int address;
        public int Value;
        public ChipVolume(int value) { chip = value; }
        public void Write(int register, int value)
        {
            if (chip == ExpansionType.None && register == 0x4000 || chip == ExpansionType.Vrc6 && register == 0x9000 || chip == ExpansionType.Mmc5 && register == 0x5000) Value = value & 15;
            if (chip == ExpansionType.Fds && register == 0x4080) Value = value & 63;
            if (chip == ExpansionType.Vrc7) { if (register == 0x9010) address = value; if (register == 0x9030 && address == 0x30) Value = 15 - (value & 15); }
            if (chip == ExpansionType.N163)
            {
                if (register == 0xf800) address = value;
                if (register == 0x4800) { if ((address & 127) == 0x7f) Value = value & 15; if ((address & 128) != 0) address = 128 | ((address + 1) & 127); }
            }
            if (chip == ExpansionType.S5B) { if (register == NesApu.S5B_ADDR) address = value; if (register == NesApu.S5B_DATA && address == 8) Value = value & 15; }
            if (chip == ExpansionType.EPSM) { if (register == NesApu.EPSM_ADDR0) address = value; if (register == NesApu.EPSM_DATA0 && address == 8) Value = value & 15; }
        }
    }
    static int checks;
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS envelope decay: " + label); }
    static Project Make(out Pattern pattern, out Instrument instrument, int chip = 0)
    {
        var p = new Project(); if (chip != 0) p.SetExpansionAudioMask(1 << (chip - 1), 1);
        instrument = p.CreateInstrument(chip, "Decay");
        var volume = instrument.VolumeEnvelope; volume.Length = 6;
        for (int i = 0; i < 6; i++) volume.Values[i] = (sbyte)(12 - i * 2);
        var pitch = instrument.PitchEnvelope; pitch.Length = 8; pitch.Loop = 0;
        for (int i = 0; i < 8; i++) pitch.Values[i] = (sbyte)i;
        var song = p.CreateSong("Envelope decay"); song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        song.SetDefaultPatternLength(24); song.SetBeatLength(4); song.SetLength(1); song.SetLoopPoint(-1);
        pattern = song.GetChannelByType(ChannelTypes[chip]).GetOrCreatePattern(0);
        pattern.Notes[0] = new Note(46) { Instrument = instrument, Duration = 24 };
        return p;
    }
    static Capture Render(Project p) { var c = new Capture(); c.Render(p.Songs[0]); return c; }
    static int[] Volumes(Capture c) => c.Frames.Select(f => f[0x4000] & 15).ToArray();

    static void CheckNsf(string output)
    {
        foreach (int chip in Enumerable.Range(0, ExpansionType.Count))
        foreach (bool pal in new[] { false, true })
        foreach (int release in chip == 0 ? new[] { 0, 1, 4 } : new[] { 0, 4 })
        {
            var p = Make(out var pattern, out var instrument, chip);
            pattern.Notes[3] = new Note { EnvelopeDecay = 2 };
            pattern.Notes[5] = new Note(46) { Instrument = instrument, HasAttack = false, Duration = 19 };
            pattern.Notes[7] = new Note { EnvelopeDecay = 3 };
            pattern.Notes[10] = new Note { EnvelopeDecay = 0 };
            pattern.Notes[12] = new Note(46) { Instrument = instrument, Duration = 12, EnvelopeDecay = 1 };
            pattern.Notes[16] = new Note(46) { Instrument = instrument, Duration = 8 };
            if (release != 0)
            {
                instrument.VolumeEnvelope.Loop = release == 1 ? 0 : 2; instrument.VolumeEnvelope.Release = release;
                pattern.Notes[5].Release = 3; pattern.Notes[12].Release = 2;
            }
            string label = $"chip {chip}, PAL {pal}, release {release}";
            var editor = new Capture(pal, p.OutputsStereoAudio, chip); editor.Render(p.Songs[0]);
            string path = Path.Combine(output, $"decay-{chip}-{pal}-{release}.nsf");
            Check(new NsfFile().Save(p, FamiToneKernel.FamiStudio, path, new[] { p.Songs[0].Id }, "Decay", "Test", "", pal ? MachineType.PAL : MachineType.NTSC, false), "NSF exports " + label);
            var handle = NotSoFatso.NsfOpen(path); Check(handle != IntPtr.Zero, "NSF opens " + label);
            var hardware = new ChipVolume(chip); var actual = new List<int>();
            NotSoFatso.WriteRegisterDelegate callback = (register, value) => hardware.Write(register, value);
            try
            {
                NotSoFatso.NsfSetApuWriteCallback(handle, callback); NotSoFatso.NsfSetTrack(handle, 0); NotSoFatso.NsfRunFrame(handle);
                for (int frame = 0; frame < editor.Levels.Count; frame++)
                {
                    NotSoFatso.NsfRunFrame(handle);
                    // The emulator callback exposes only APU-address writes;
                    // expansion chips have their own independently decoded state.
                    int level = chip == 0 ? hardware.Value : NotSoFatso.NsfGetState(handle, ChannelTypes[chip], NotSoFatso.STATE_VOLUME, 0);
                    if (chip == ExpansionType.Vrc7) level = 15 - level;
                    actual.Add(level);
                }
            }
            finally { NotSoFatso.NsfClose(handle); GC.KeepAlive(callback); }
            if (!actual.SequenceEqual(editor.Levels)) File.WriteAllLines(path + ".levels.txt", Enumerable.Range(0, actual.Count).Select(f => $"{f}: editor={editor.Levels[f]} NSF={actual[f]}"));
            Check(actual.SequenceEqual(editor.Levels), "NSF CPU matches every volume-register frame: " + label);
        }
    }

    public static int Run(string output)
    {
        checks = 0; Directory.CreateDirectory(output);
        var p = Make(out var pattern, out var instrument);
        var original = Render(p);
        pattern.Notes[3] = new Note { EnvelopeDecay = 2 };
        var decay = Render(p); var v = Volumes(decay);
        Check(v.Take(8).SequenceEqual(new[] { 12,10,8,8,7,7,6,6 }), "period two freezes previous envelope level and decrements every second tick");
        Check(decay.Frames.Zip(original.Frames).All(pair => pair.First[0x4002] == pair.Second[0x4002]), "pitch envelope continues while volume is frozen");
        Check(v.Skip(18).All(x => x == 0), "decay saturates at zero");
        pattern.Notes[3].EnvelopeDecay = 1;
        Check(Volumes(Render(p))[3] == 7, "period one decrements on the effect's own tick");
        pattern.Notes[3].EnvelopeDecay = 255;
        Check(Volumes(Render(p)).Skip(3).All(x => x == 8), "maximum period preserves value before its first decrement");
        pattern.Notes[3].EnvelopeDecay = 2;
        pattern.Notes[7] = new Note { EnvelopeDecay = 0 };
        Check(Volumes(Render(p)).Skip(7).Take(3).SequenceEqual(new[] { 6,4,2 }), "zero resumes volume at the frozen next macro step");
        pattern.Notes.Remove(7);
        pattern.Notes[5] = new Note(46) { Instrument = instrument, Duration = 19, HasAttack = false };
        Check(Volumes(Render(p)).Take(8).SequenceEqual(v.Take(8)), "no-attack notes preserve active decay and its counter");
        pattern.Notes[5].HasAttack = true;
        Check(Volumes(Render(p)).Skip(5).Take(3).SequenceEqual(new[] { 12,10,8 }), "new attack cancels decay and restarts the volume macro");
        pattern.Notes[5].EnvelopeDecay = 1;
        Check(Volumes(Render(p))[5] == 11, "same-tick attack and decay start from the new macro's first level");
        pattern.Notes.Remove(5); pattern.Notes[6] = new Note { EnvelopeDecay = 3 };
        Check(Volumes(Render(p)).Skip(6).Take(4).SequenceEqual(new[] { 7,7,6,6 }), "a new period starts from the currently decayed value and resets the divider");
        pattern.Notes.Remove(6); instrument.VolumeEnvelope.Loop = 2; instrument.VolumeEnvelope.Release = 4;
        pattern.Notes[0].Release = 7;
        Check(Volumes(Render(p)).Skip(7).Take(2).SequenceEqual(new[] { 4,2 }), "volume release cancels decay and jumps to the release macro");
        pattern.Notes[0].Release = 0; pattern.Notes[3].Volume = 8;
        Check(Volumes(Render(p))[3] == 4, "ordinary note volume still multiplies the decaying envelope");
        var note = pattern.Notes[3];
        Check(note.HasValidEffectValue(Note.EffectEnvelopeDecay) && note.GetEffectValue(Note.EffectEnvelopeDecay) == 2, "generic effect accessor exposes the value");
        var copied = note.Clone(); copied.ClearEffectValue(Note.EffectEnvelopeDecay);
        Check(!copied.HasEnvelopeDecay && note.HasEnvelopeDecay, "clone and effect clearing are independent");
        copied.SetEffectValue(Note.EffectEnvelopeDecay, 999);
        Check(copied.EnvelopeDecay == 255 && Note.GetEffectMaxValue(p.Songs[0], p.Songs[0].Channels[0], Note.EffectEnvelopeDecay) == 255, "effect editor clamps to byte range");
        var clone = p.DeepClone();
        Check(clone.Songs[0].Channels[0].Patterns[0].Notes[3].EnvelopeDecay == 2, "project cloning preserves the decay effect");
        string path = Path.Combine(output, "envelope-decay.fms");
        Check(new ProjectFile().Save(p, path), "binary project saves");
        var loaded = new ProjectFile().Load(path);
        Check(loaded.Songs[0].Channels[0].Patterns[0].Notes[3].EnvelopeDecay == 2, "format 1003 reads the effect back");
        Check(Volumes(Render(loaded)).SequenceEqual(Volumes(Render(p))), "binary round trip preserves actual playback levels");
        Check(!p.Songs[0].GetChannelByType(ChannelType.Dpcm).SupportsEffect(Note.EffectEnvelopeDecay), "DPCM does not expose software volume decay");
        var epsm = new Project(); epsm.SetExpansionAudioMask(ExpansionType.EPSMMask, 1); var epsmSong = epsm.CreateSong("Rhythm");
        Check(epsmSong.Channels.Where(c => c.IsEPSMRythmChannel).All(c => !c.SupportsEffect(Note.EffectEnvelopeDecay)), "EPSM rhythm channels do not expose continuous software volume decay");
        CheckNsf(output);
        Console.WriteLine($"All {checks} envelope-decay checks passed."); return checks;
    }
}
