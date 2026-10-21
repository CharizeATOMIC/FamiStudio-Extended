using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using FamiStudio;

static class SongEditingRateRegression
{
    sealed class LegacyWriter : ProjectSaveBuffer, ProjectBuffer
    {
        public LegacyWriter(Project project) : base(project) { }
        int ProjectBuffer.Version => 1009;
    }
    static int checks;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; Console.WriteLine("PASS editing grid: " + message); }
    static byte[] Bytes(Project project) { var b = new ProjectSaveBuffer(project, ProjectBufferFlags.UndoRedo); project.Serialize(b); return b.GetBuffer(); }
    sealed class Player : BasePlayer
    {
        public readonly List<short> Audio = new();
        public readonly List<(int Tick,int Address,int Value)> Writes = new();
        int tick;
        public Player(bool pal) : base(NesApu.Create(), pal, false) { Loop = LoopMode.None; }
        protected override short[] EndFrame() { var audio = base.EndFrame(); Audio.AddRange(audio); tick++; return audio; }
        public override void NotifyRegisterWrite(int apu, int register, int value, int metadata = 0) { Writes.Add((tick,register,value)); }
        public void Render(Song song,int frames=20000)
        {
            // Native FDS state survives a soft Reset; compare cold
            // instances so repeated identical projects have identical samples.
            try { BeginPlaySong(song); for(int i=0;i<frames && PlaySongFrame();i++) {} }
            finally { Shutdown(); NesApu.CurrentSample[apuIndex]=null; NesApu.Destroy(apuIndex); }
        }
    }
    static Project Fixture(bool pal, int padding = GroovePaddingType.Middle, bool reverse = false, bool ft = false)
    {
        var project = new Project { PalMode = pal, TempoMode = ft ? TempoType.FamiTracker : TempoType.FamiStudio };
        var instrument = project.CreateInstrument(ExpansionType.None, "Grid fixture");
        instrument.VolumeEnvelope.Length = 5; instrument.VolumeEnvelope.Loop = 1; instrument.VolumeEnvelope.Release = 3;
        new sbyte[] {15,12,10,7,0}.CopyTo(instrument.VolumeEnvelope.Values,0);
        var song = project.CreateSong("Timing"); song.SetLength(2); song.SetLoopPoint(-1);
        song.SetDefaultPatternLength(ft ? 40 : 200); song.SetBeatLength(ft ? 4 : 20);
        if(ft) { song.FamitrackerSpeed=3; song.FamitrackerTempo=pal?125:150; }
        else { song.ChangeFamiStudioTempoGroove(reverse?new[]{6,5}:new[]{5,6},false); song.SetGroovePaddingMode(padding); }
        var pattern = song.Channels[0].GetOrCreatePattern(0);
        pattern.Notes[0] = new Note(46) { Instrument=instrument, Duration=160, Release=136, SlideNoteTarget=53, Volume=14, VolumeSlideTarget=3 };
        pattern.Notes[8] = new Note { VibratoDepth=2,VibratoSpeed=1 };
        pattern.Notes[20] = new Note { EnvelopeDecay=1 };
        pattern.Notes[26] = new Note { EnvelopeDecay=0 };
        if(!ft)
        {
            pattern.Notes[140]=new Note(53) { Instrument=instrument,Duration=30,Release=20,Volume=15 };
            song.SetPatternCustomSettings(1,80,20,new[]{5,6},padding);
            var second=song.Channels[0].GetOrCreatePattern(1); second.PlaybackDelay=3;
            second.Notes[0]=new Note(49){Instrument=instrument,Duration=24,Release=12};
            var variant=song.Channels[0].CreatePattern("Loop alternative");variant.PlaybackDelay=2;
            variant.Notes[3]=new Note(48){Instrument=instrument,Duration=9,Release=6};second.LoopVariant=variant;
        }
        else { pattern.Notes[14]=new Note{Speed=5}; pattern.Notes[20]=new Note{EnvelopeDecay=2}; }
        project.UpdateRateMultiplier=4;
        return project;
    }
    static void Compare(Project project,string label)
    {
        var baseline=new Player(project.PalMode);baseline.Render(project.Songs[0]);
        var old=Bytes(project);float bpm=project.Songs[0].BPM;
        Check(project.TryChangeEditingRate(4,out var error),label+" upgrade: "+error);
        Check(project.Songs[0].LogicRateMultiplier==4&&project.Songs[0].EffectRateMultiplier==1&&project.Songs[0].BPM==bpm,label+" grid/effect/tempo");
        var actual=new Player(project.PalMode);actual.Render(project.Songs[0]);
        Check(actual.Writes.SequenceEqual(baseline.Writes),label+" every ordered register write preserves its output tick");
        Check(actual.Audio.SequenceEqual(baseline.Audio),label+" PCM bit exact");
        Check(project.TryChangeEditingRate(2,out error)&&project.TryChangeEditingRate(1,out error),label+" 4 to 2 to 1 exact downgrade: "+error);
        project.UpdateRateMultiplier=4;
        Check(Bytes(project).SequenceEqual(old),label+" complete serialized round trip");
    }
    static void Reject(Project p,int rate,string label)
    {
        var before=Bytes(p);Check(!p.TryChangeEditingRate(rate,out var error)&&!string.IsNullOrEmpty(error),label+" rejected with reason");
        Check(Bytes(p).SequenceEqual(before),label+" atomic refusal");
    }
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        foreach(bool pal in new[]{false,true})foreach(int padding in new[]{GroovePaddingType.Beginning,GroovePaddingType.Middle,GroovePaddingType.End})
            foreach(bool reverse in new[]{false,true})Compare(Fixture(pal,padding,reverse),$"FS PAL={pal} padding={padding} reverse={reverse}");
        foreach(bool pal in new[]{false,true})Compare(Fixture(pal,ft:true),"FT native PAL="+pal);
        foreach(bool pal in new[]{false,true})foreach(int destination in new[]{0,1,2,3})
        {
            var pcmProject=new Project{PalMode=pal};
            pcmProject.SetExpansionAudioMask(ExpansionType.Mmc5Mask|ExpansionType.Vrc6Mask,pcmEnabled:true);
            var sample=pcmProject.CreatePcmSampleFromWavData("Synthetic PCM",Enumerable.Range(0,441).Select(n=>(short)(22000*Math.Sin(n*.17))).ToArray(),22050);
            var i=pcmProject.CreateInstrument(destination==0?ExpansionType.None:destination==1?ExpansionType.Mmc5:ExpansionType.Vrc6,"PCM clock");
            i.MapPcmSample(37,sample,loop:true);
            var s=pcmProject.CreateSong("PCM timing");s.SetLength(1);s.SetLoopPoint(-1);s.SetDefaultPatternLength(32);s.SetBeatLength(4);s.ChangeFamiStudioTempoGroove(new[]{1},false);
            int type=new[]{ChannelType.Dpcm,ChannelType.Mmc5Pcm,ChannelType.Vrc6Square1,ChannelType.Vrc6Square2}[destination];
            var notes=s.GetChannelByType(type).GetOrCreatePattern(0).Notes;
            notes[0]=new Note(37){Instrument=i,Duration=16};notes[4]=new Note{Volume=8};notes[20]=new Note(37){Instrument=i,Duration=8,Volume=15};
            pcmProject.UpdateRateMultiplier=4;
            Compare(pcmProject,$"PCM destination={destination} PAL={pal}");
        }
        var slow=Fixture(false);slow.Songs[0].ChangeFamiStudioTempoGroove(new[]{18,19},false);
        slow.Songs[0].SetDefaultPatternLength(180);slow.Songs[0].SetBeatLength(72);
        Compare(slow,"largest authored mixed groove scales to 72/76");
        foreach(bool pal in new[]{false,true})
        {
            var fds=Fixture(pal);fds.SetExpansionAudioMask(ExpansionType.FdsMask);
            var i=fds.CreateInstrument(ExpansionType.Fds,"FDS delay");i.FdsModDelay=5;i.FdsModDepth=3;i.FdsModSpeed=256;
            foreach(var envelope in i.Envelopes) if(envelope!=null) envelope.TickRateMultiplier=1;
            fds.Songs[0].GetChannelByType(ChannelType.FdsWave).GetOrCreatePattern(0).Notes[0]=new Note(46){Instrument=i,Duration=40};
            Compare(fds,"FDS authored modulation delay PAL="+pal);
            var loop=Fixture(pal);loop.Songs[0].SetLoopPoint(0);
            var beforeLoop=new Player(pal){Loop=LoopMode.Song};beforeLoop.Render(loop.Songs[0],4000);
            Check(loop.TryChangeEditingRate(4,out _),"loop/variant conversion PAL="+pal);
            var afterLoop=new Player(pal){Loop=LoopMode.Song};afterLoop.Render(loop.Songs[0],4000);
            Check(beforeLoop.Writes.SequenceEqual(afterLoop.Writes)&&beforeLoop.Audio.SequenceEqual(afterLoop.Audio),"actual repeated loop variants and delayed ends preserve PCM PAL="+pal);
        }
        var p=Fixture(false);p.Songs[0].SetDefaultPatternLength(600);Reject(p,4,"pattern capacity");
        p=Fixture(false);p.Songs[0].Channels[0].Patterns[0].PlaybackDelay=64;Reject(p,4,"delay capacity");
        p=Fixture(false);p.Songs[0].Channels[0].Patterns[0].Notes[0].Duration=20000;Reject(p,4,"duration capacity");
        p=Fixture(false,ft:true);p.Songs[0].FamitrackerTempo=151;Reject(p,4,"fractional tracker accumulator");
        Check(p.TryChangeEditingRate(1,out _),"same-rate nonnative FT remains editable");
        p=Fixture(false);Check(p.TryChangeEditingRate(4,out _),"prepare subframe editing");
        var pat=p.Songs[0].Channels[0].Patterns[0];pat.Notes[1]=new Note{Volume=1};pat.Notes[2]=new Note{Volume=15};
        var player=new Player(false);player.Render(p.Songs[0]);
        var a=player.Writes.Last(w=>w.Tick==1&&w.Address==0x4000).Value&15;
        var b=player.Writes.Last(w=>w.Tick==2&&w.Address==0x4000).Value&15;
        Check(a!=b,"adjacent grid rows 1 and 2 produce different 240 Hz hardware ticks");
        Reject(p,2,"unaligned new subframe note");
        p=Fixture(false);Check(p.TryChangeEditingRate(4,out _),"prepare macro downgrade");
        var env=p.Instruments[0].VolumeEnvelope;Check(EnvelopeClockConversion.TryConvert(env,4,false),"existing exact macro expansion");
        Check(p.TryChangeEditingRate(1,out _)&&env.TickRateMultiplier==1&&env.Length==5,"project downshift also restores an exactly reducible macro");
        p=Fixture(false);p.Instruments[0].VolumeEnvelope.TickRateMultiplier=4;Reject(p,1,"nonreducible high-rate macro");
        p=Fixture(false);Check(p.TryChangeEditingRate(4,out _),"prepare new high-rate instrument");
        var constant=p.CreateInstrument(ExpansionType.None,"New constant instrument");
        Check(constant.VolumeEnvelope.TickRateMultiplier==4&&constant.VolumeEnvelope.Length==1,"new instrument uses one held value at 4x");
        Check(p.TryChangeEditingRate(1,out _)&&constant.VolumeEnvelope.TickRateMultiplier==1&&constant.VolumeEnvelope.Length==1,"held default instrument downshifts exactly without inventing timed transitions");
        p=Fixture(false);p.Songs[0].Channels[0].Patterns[0].Notes[0].Release=100;Check(p.TryChangeEditingRate(4,out _),"storage fixture conversion");
        var filename=Path.Combine(output,"editing-rate.fms");new ProjectFile().Save(p,filename);var loaded=new ProjectFile().Load(filename);
        Check(loaded!=null&&loaded.Songs[0].LogicRateMultiplier==4&&loaded.Songs[0].EffectRateMultiplier==1,"binary persists independent effect clock");
        var textfile=Path.Combine(output,"editing-rate.txt");new FamistudioTextFile().Save(p,textfile,null,false);var textProject=new FamistudioTextFile().Load(textfile);
        Check(textProject!=null&&textProject.Songs[0].EffectRateMultiplier==1&&textProject.Songs[0].Groove.SequenceEqual(new[]{20,24}),"text preserves scaled mixed groove and effects");
        p=Fixture(false);var unused=p.Songs[0].Channels[1].CreatePattern("Unused editable pattern");
        unused.Notes[7]=new Note(49){Instrument=p.Instruments[0],Duration=9,Release=6};
        Check(p.TryChangeEditingRate(4,out _)&&unused.Notes.ContainsKey(28)&&unused.Notes[28].Duration==36&&unused.Notes[28].Release==24,"unused pattern data is also subdivided without deletion");
        p=Fixture(false);var legacy=new LegacyWriter(p);p.Serialize(legacy);var restored=new Project();restored.Serialize(new ProjectLoadBuffer(restored,legacy.GetBuffer(),1009));
        Check(restored.Songs[0].EffectRateMultiplier==restored.Songs[0].LogicRateMultiplier&&restored.Songs[0].EffectRateMultiplier==1,"actual 1009 layout defaults effect clock to the saved logic clock");
        var oldText=File.ReadAllLines(textfile).Select(line=>System.Text.RegularExpressions.Regex.Replace(line," EffectRateMultiplier=\"[^\"]*\"", "")).ToArray();
        File.WriteAllLines(Path.Combine(output,"old-clock.txt"),oldText);
        var oldTextProject=new FamistudioTextFile().Load(Path.Combine(output,"old-clock.txt"));
        Check(oldTextProject.Songs[0].EffectRateMultiplier==4,"legacy text without an effects field inherits its authored logic clock");
        Console.WriteLine($"PASS all {checks} song-editing-rate checks");
    }
}
