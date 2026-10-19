using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FamiStudio;

static class EnvelopeClockRegression
{
    static int checks;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS envelope clock: " + label); }
    sealed class Probe : ChannelState
    {
        public Probe(int output, int logic = 1) : base(null, 0, ChannelType.Square1, 440) { ConfigureUpdateRates(output, logic); }
        public override void UpdateAPU() { noteTriggered = noteReleased = false; }
        public int Value(int type) => envelopeValues[type];
        public int ClockIndex(int type) => envelopeIdx[type];
    }
    sealed class Capture : BasePlayer
    {
        readonly Dictionary<int,int> values = new();
        int epsm0, epsm1, s5b, vrc7, n163;
        public readonly List<Dictionary<int,int>> Frames = new();
        public readonly List<int> Sizes = new();
        public Capture(bool pal, bool stereo) : base(NesApu.APU_WAV_EXPORT, pal, stereo) { Loop = LoopMode.None; }
        public override void NotifyRegisterWrite(int apu, int register, int value, int metadata = 0)
        {
            if(register==NesApu.EPSM_ADDR0) { epsm0=value; return; }
            if(register==NesApu.EPSM_ADDR1) { epsm1=value; return; }
            if(register==NesApu.EPSM_DATA0) { values[0x10000+epsm0]=value; return; }
            if(register==NesApu.EPSM_DATA1) { values[0x10100+epsm1]=value; return; }
            if(register==NesApu.S5B_ADDR) { s5b=value; return; }
            if(register==NesApu.S5B_DATA) { values[0x10200+s5b]=value; return; }
            if(register==0x9010) { vrc7=value; return; }
            if(register==0x9030) { values[0x10300+vrc7]=value; return; }
            if(register==0xf800) { n163=value; return; }
            if(register==0x4800) { values[0x10400+(n163&127)]=value; if((n163&128)!=0)n163=128|((n163+1)&127);return; }
            values[register]=value;
        }
        protected override short[] EndFrame() { var data = base.EndFrame(); Frames.Add(new(values)); Sizes.Add(data.Length); return data; }
        public void Render(Song song) { try { BeginPlaySong(song); while (PlaySongFrame()) {} } finally { Shutdown(); } }
    }
    static void Macro(Envelope env, int rate, params sbyte[] values)
    {
        env.Length = values.Length; env.Loop = -1; env.TickRateMultiplier = rate;
        Array.Copy(values, env.Values, values.Length);
    }
    static void Model(string output)
    {
        var old = new ProjectFile().Load("Tests/TuningRegression/Fixtures/format1004-dpcm.fms");
        Check(old.Instruments.SelectMany(i => i.Envelopes).Where(e => e != null).All(e => e.TickRateMultiplier == 1), "old binary restores each macro to its original 1x clock");
        var p = new Project { UpdateRateMultiplier = 4 }; var instrument = p.CreateInstrument(0); var arp = p.CreateArpeggio();
        Check(instrument.Envelopes.Where(e => e != null).All(e => e.TickRateMultiplier == 4) && arp.Envelope.TickRateMultiplier == 4, "new instrument and independent arpeggio use the selected author clock");
        Macro(instrument.VolumeEnvelope, 2, 15, 10, 5, 0); Macro(instrument.PitchEnvelope, 1, 0, 1, 0, -1);
        instrument.PitchEnvelope.Loop = 0; var song = p.CreateSong(); song.SetLength(1);
        song.Channels[0].GetOrCreatePattern(0).Notes[0] = new Note(46) { Instrument = instrument, Arpeggio = arp, Duration = 4 };
        var clone = p.DeepClone();
        Check(clone.Instruments[0].VolumeEnvelope.TickRateMultiplier == 2 && clone.Arpeggios[0].Envelope.TickRateMultiplier == 4, "deep clone preserves independent clocks");
        Check(instrument.VolumeEnvelope.ShallowClone().TickRateMultiplier == 2, "macro clone preserves authored clock");
        var different = instrument.VolumeEnvelope.ShallowClone(); different.TickRateMultiplier = 1;
        Check(!Envelope.AreIdentical(EnvelopeType.Volume, instrument.VolumeEnvelope, different), "envelope merging distinguishes equal samples with different durations");
        var wave = new Envelope(EnvelopeType.WaveformRepeat); Macro(wave, 2, 2, 3);
        Check(wave.CreateWaveIndexEnvelope().TickRateMultiplier == 2, "compiled wave-index envelope preserves its clock");
        bool rejected = false; try { different.TickRateMultiplier = 3; } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "unsupported macro clocks are rejected");
        var undo = new ProjectSaveBuffer(p, ProjectBufferFlags.UndoRedo); p.Serialize(undo); instrument.VolumeEnvelope.TickRateMultiplier = 4;
        p.Serialize(new ProjectLoadBuffer(p, undo.GetBuffer(), Project.Version, ProjectBufferFlags.UndoRedo));
        Check(p.Instruments[0].VolumeEnvelope.TickRateMultiplier == 2, "undo restores each macro clock");
        string file = Path.Combine(output, "envelope-clocks.fms");
        Check(new ProjectFile().Save(p, file), "mixed-clock binary saves");
        var loaded = new ProjectFile().Load(file);
        Check(loaded.Instruments[0].VolumeEnvelope.TickRateMultiplier == 2 && loaded.Instruments[0].PitchEnvelope.TickRateMultiplier == 1, "mixed-clock binary round trip");
        file = Path.ChangeExtension(file, "txt"); Check(new FamistudioTextFile().Save(p, file, new[] { song.Id }, false), "mixed-clock text saves");
        loaded = new FamistudioTextFile().Load(file);
        Check(loaded.Instruments[0].VolumeEnvelope.TickRateMultiplier == 2 && loaded.Arpeggios[0].Envelope.TickRateMultiplier == 4, "mixed-clock text and arpeggio round trip");
        Check(loaded.Instruments[0].Envelopes[EnvelopeType.NoisePeriod].TickRateMultiplier == 4, "disabled macro retains author clock through text editing");
    }
    static void Dividers()
    {
        foreach (int output in new[] { 1,2,4 }) foreach (int rate in new[] { 1,2,4 }.Where(r => r <= output))
        {
            var p = new Project(); var i = p.CreateInstrument(0); Macro(i.VolumeEnvelope, rate, 15,12,9,6,3,0);
            var probe = new Probe(output); probe.PlayNote(new Note(46) { Instrument = i }); int divider = output / rate;
            var values = new List<int>(); for (int t=0;t<6*divider;t++) { probe.Update(t % output == 0); values.Add(probe.Value(0)); }
            Check(values.SequenceEqual(new[] {15,12,9,6,3,0}.SelectMany(v => Enumerable.Repeat(v, divider))), $"absolute macro {rate}x advances once per {divider} output ticks at {output}x");
            Macro(i.PitchEnvelope, rate, 1,2,-1); i.PitchEnvelope.Relative = true; i.PitchEnvelope.Loop = 0;
            probe = new Probe(output); probe.PlayNote(new Note(46) { Instrument = i }); values.Clear();
            for(int t=0;t<6*divider;t++) { probe.Update(t%output==0); values.Add(probe.Value(EnvelopeType.Pitch)); }
            Check(values.SequenceEqual(new[]{1,3,2,3,5,4}.SelectMany(v=>Enumerable.Repeat(v,divider))), $"relative pitch {rate}x never accumulates on held output ticks at {output}x");
            Macro(i.Envelopes[EnvelopeType.NoiseEnvelopeRestart], rate, 2,2); probe = new Probe(output); probe.PlayNote(new Note(46) { Instrument = i }); values.Clear();
            for(int t=0;t<4*divider;t++) { probe.Update(t%output==0); values.Add(probe.Value(EnvelopeType.NoiseEnvelopeRestart)); }
            Check(values.Select((v,t)=>v==(t==0||t==divider?2:0)).All(v=>v), $"hardware restart {rate}x emits each pulse exactly once at {output}x");
        }
        {
            var p=new Project();var i=p.CreateInstrument(0);Macro(i.VolumeEnvelope,1,15,12,9,6,3,0);i.VolumeEnvelope.Loop=1;i.VolumeEnvelope.Release=3;
            Macro(i.PitchEnvelope,2,1,2,3,4);i.PitchEnvelope.Loop=0; var probe=new Probe(4);
            probe.PlayNote(new Note(46){Instrument=i}); for(int t=0;t<3;t++)probe.Update(t==0);
            probe.PlayNote(new Note(Note.NoteRelease));probe.Update(false);
            Check(probe.Value(0)==6&&probe.Value(2)==2,"release jumps immediately while unrelated pitch countdown keeps its phase");
            probe.Update(true); Check(probe.Value(0)==6&&probe.Value(2)==3,"release resets only the macro that actually jumped");
            probe.PlayNote(new Note(46){Instrument=i,HasAttack=false});probe.Update(false);
            Check(probe.Value(0)==6&&probe.Value(2)==3,"no-attack notes preserve each independent clock");
            probe.PlayNote(new Note(46){Instrument=i});probe.Update(false);
            Check(probe.Value(0)==15&&probe.Value(2)==1,"new attack resets all relevant clocks immediately");
        }
        {
            var p=new Project();p.SetExpansionAudioMask(ExpansionType.FdsMask);var i=p.CreateInstrument(ExpansionType.Fds);
            Macro(i.Envelopes[EnvelopeType.WaveformRepeat],1,2,3);i.Envelopes[EnvelopeType.WaveformRepeat].Loop=0;
            var slow=new Probe(1);var fast=new Probe(4);slow.PlayNote(new Note(46){Instrument=i});fast.PlayNote(new Note(46){Instrument=i});
            var expected=new List<int>();var actual=new List<int>();
            for(int tick=0;tick<16;tick++){slow.Update();expected.Add(slow.ClockIndex(EnvelopeType.WaveformRepeat));for(int sub=0;sub<4;sub++){fast.Update(sub==0);actual.Add(fast.ClockIndex(EnvelopeType.WaveformRepeat));}}
            Check(actual.SequenceEqual(expected.SelectMany(v=>Enumerable.Repeat(v,4))),"waveform repeats cross their boundaries only on the macro clock");
        }
        {
            var p=new Project();var i=p.CreateInstrument(0);Macro(i.VolumeEnvelope,1,12,10,8,6);var slow=new Probe(1);var fast=new Probe(4);
            slow.PlayNote(new Note(46){Instrument=i});fast.PlayNote(new Note(46){Instrument=i});var expected=new List<int>();var actual=new List<int>();
            for(int tick=0;tick<12;tick++)
            {
                if(tick==2) { slow.PlayNote(new Note{EnvelopeDecay=2}); fast.PlayNote(new Note{EnvelopeDecay=2}); }
                if(tick==7) { slow.PlayNote(new Note{EnvelopeDecay=0}); fast.PlayNote(new Note{EnvelopeDecay=0}); }
                slow.Update(); expected.Add(slow.Value(0));for(int sub=0;sub<4;sub++){fast.Update(sub==0);actual.Add(fast.Value(0));}
            }
            Check(actual.SequenceEqual(expected.SelectMany(v=>Enumerable.Repeat(v,4))),"decay and cancellation follow song logic clock independently of output rate");
            slow=new Probe(1);fast=new Probe(4);var vib=new Note(46){Instrument=i,VibratoSpeed=6,VibratoDepth=4};slow.PlayNote(vib);fast.PlayNote(vib);
            expected.Clear();actual.Clear();for(int tick=0;tick<24;tick++){slow.Update();expected.Add(slow.Value(2));for(int sub=0;sub<4;sub++){fast.Update(sub==0);actual.Add(fast.Value(2));}}
            Check(actual.SequenceEqual(expected.SelectMany(v=>Enumerable.Repeat(v,4))),"score vibrato uses the song's authored clock");
        }
    }
    static Project Fixture(int chip,bool pal)
    {
        var p=new Project{PalMode=pal};if(chip!=0)p.SetExpansionAudioMask(1<<(chip-1),1);
        var i=p.CreateInstrument(chip);Macro(i.VolumeEnvelope,1,15,12,9,6,3,0);i.VolumeEnvelope.Loop=1;i.VolumeEnvelope.Release=4;
        Macro(i.PitchEnvelope,1,0,1,2,1,0,-1,-2,-1);i.PitchEnvelope.Loop=0;
        var song=p.CreateSong();song.ChangeFamiStudioTempoGroove(new[]{1},false);song.SetDefaultPatternLength(32);song.SetBeatLength(4);song.SetLength(1);song.SetLoopPoint(-1);
        var channel=song.Channels.First(c=>c.Expansion==chip&&c.SupportsEffect(Note.EffectFinePitch));var pattern=channel.GetOrCreatePattern(0);
        pattern.Notes[0]=new Note(46){Instrument=i,Duration=32,Release=10};pattern.Notes[12]=new Note(48){Instrument=i,Duration=20};pattern.Notes[18]=new Note{EnvelopeDecay=3};pattern.Notes[24]=new Note{EnvelopeDecay=0};
        if(chip==ExpansionType.Fds){i.FdsModDelay=3;i.FdsModDepth=7;i.FdsModSpeed=123;}
        return p;
    }
    static void Playback()
    {
        foreach(bool pal in new[]{false,true})foreach(int chip in Enumerable.Range(0,ExpansionType.Count))
        {
            var p=Fixture(chip,pal);var baseline=new Capture(pal,p.OutputsStereoAudio);baseline.Render(p.Songs[0]);
            foreach(int output in new[]{2,4})
            {
                p.UpdateRateMultiplier=output;var high=new Capture(pal,p.OutputsStereoAudio);high.Render(p.Songs[0]);
                Check(high.Frames.Count==baseline.Frames.Count*output,$"chip {chip}, PAL {pal}, output {output}x preserves score duration");
                Check(Math.Abs(high.Sizes.Sum()-baseline.Sizes.Sum())<=4,$"chip {chip}, PAL {pal}, output {output}x preserves rendered sample count");
                var mismatch=new List<string>();for(int frame=0;frame<high.Frames.Count;frame++)
                {
                    var original=baseline.Frames[frame/output];var actual=high.Frames[frame];
                    foreach(var item in original)if(!actual.TryGetValue(item.Key,out var value)||value!=item.Value)mismatch.Add($"f{frame} r{item.Key:x} {value}!={item.Value}");
                }
                Check(mismatch.Count==0,$"chip {chip}, PAL {pal}, output {output}x preserves every latched envelope/control frame: {string.Join(";",mismatch.Take(3))}");
            }
        }
    }
    public static void Run(string output)
    {
        checks=0;Directory.CreateDirectory(output);Model(output);Dividers();Playback();Console.WriteLine($"PASS all {checks} envelope-clock checks");
    }
}
