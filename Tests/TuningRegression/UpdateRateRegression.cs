using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Reflection;
using FamiStudio;

static class UpdateRateRegression
{
    static int checks;
    static void Check(bool value,string message) { if(!value)throw new Exception(message);checks++;Console.WriteLine("PASS update rate: "+message); }
    sealed class Player:BasePlayer
    {
        public readonly List<short> Audio=new(); public readonly List<int> Sizes=new();
        public readonly Dictionary<int,int> Registers=new();
        public Player(bool pal,bool stereo=false):base(NesApu.APU_WAV_EXPORT,pal,stereo) {Loop=LoopMode.None;}
        protected override short[] EndFrame() {var a=base.EndFrame();Sizes.Add(a.Length);Audio.AddRange(a);return a;}
        public override void NotifyRegisterWrite(int apu,int register,int value,int metadata=0){Registers[register]=value;}
        public void Render(Song s) {try {BeginPlaySong(s);while(PlaySongFrame()) {}}finally {Shutdown();}}
    }
    static Project Fixture(int rate,bool pal=false,bool subframes=false)
    {
        var p=new Project{PalMode=pal,UpdateRateMultiplier=rate};var i=p.CreateInstrument(0,"Synthetic square");
        i.VolumeEnvelope.Length=1;i.VolumeEnvelope.Values[0]=15;i.Envelopes[EnvelopeType.DutyCycle].Length=1;i.Envelopes[EnvelopeType.DutyCycle].Values[0]=2;
        var s=p.CreateSong("Update clock");s.SetLength(1);s.SetDefaultPatternLength(240);s.SetBeatLength(24);s.ChangeFamiStudioTempoGroove(new[]{1},false);s.SetLoopPoint(-1);
        var pat=s.Channels[0].GetOrCreatePattern(0);pat.Notes[0]=new Note(46){Instrument=i,Duration=240};
        if(subframes) for(int n=1;n<240;n++)pat.Notes[n]=new Note{Volume=(byte)(n%2==0?15:0)};
        return p;
    }
    static double Frequency(List<short> pcm)
    {
        var edges=new List<int>();for(int n=1001;n<pcm.Count-100;n++)if(pcm[n-1]<=0&&pcm[n]>0)edges.Add(n);
        return (edges.Count-1)*44100.0/(edges[^1]-edges[0]);
    }
    static bool SubframeContrast(Player player)
    {
        double on=0,off=0;int offset=0;
        for(int tick=0;tick<player.Sizes.Count;tick++)
        {
            int size=player.Sizes[tick],start=offset+size/4,count=size/2;
            if(tick>20)
            {
                double mean=player.Audio.Skip(start).Take(count).Average(v=>(double)v);
                double energy=player.Audio.Skip(start).Take(count).Average(v=>(v-mean)*(v-mean));
                if((tick&1)==0)on+=energy;else off+=energy;
            }
            offset+=size;
        }
        return on>off*2;
    }
    sealed class PullAudioStream:IAudioStream
    {
        GetBufferDataCallback callback;
        public bool IsPlaying{get;private set;} public bool Stereo=>true;
        public bool RecreateOnDeviceChanged=>false;public int ImmediatePlayPosition=>0;
        public void Start(GetBufferDataCallback fill,StreamStartingCallback start){callback=fill;IsPlaying=true;start();}
        public short[] Read()=>callback(out _);public void Stop(){IsPlaying=false;}
        public void PlayImmediate(short[] data,int sampleRate,float volume,int channel=0){}public void Dispose()=>Stop();
    }
    static void PreviewAndAudioExport()
    {
        var p=Fixture(4);p.SetExpansionAudioMask(ExpansionType.Vrc6Mask);
        var counts=Enumerable.Repeat(1,ExpansionType.Count).ToArray();counts[ExpansionType.Vrc6]=2;p.SetExpansionInstances(counts);
        var channel=p.Songs[0].GetChannelByType(ChannelType.Vrc6Square1,1);
        var instrument=p.CreateInstrument(ExpansionType.Vrc6);instrument.VolumeEnvelope.Length=1;instrument.VolumeEnvelope.Values[0]=15;
        using var stream=new PullAudioStream();var preview=new InstrumentPlayer(stream,false,44100,true,0);
        preview.Start(p,false);preview.PlayNote(channel.Index,new Note(46){Instrument=instrument,Duration=64});
        var previewAudio=new List<short>();for(int tick=0;tick<40;tick++)previewAudio.AddRange(stream.Read());
        preview.Stop();preview.Shutdown();
        Check(previewAudio.Any(v=>v!=0)&&Math.Abs(previewAudio.Count/2.0/44100-40.0/NesApu.FpsNTSC/4)<.002,"keyboard preview of a second expansion chip has real 4x duration");
        channel.GetOrCreatePattern(0).Notes[0]=new Note(46){Instrument=instrument,Duration=240};
        p.Songs[0].SetLoopPoint(0);
        var stems=new List<short[]>();AudioExportUtils.Save(p.Songs[0],"unused",44100,4,1,(1L<<channel.Index)|1,true,false,false,null,0,false,false,(data,n,file)=>stems.Add(data));
        Check(stems.Count==2&&stems.All(s=>s.Length==44100&&s.Any(v=>v!=0)),"one-second separate audio exports preserve duration for base and virtual-chip stems");
        var legacy=Fixture(1);legacy.UpdateRateMultiplier=4;
        var undo=new ProjectSaveBuffer(legacy,ProjectBufferFlags.UndoRedo);legacy.Serialize(undo);legacy.UpdateRateMultiplier=1;
        legacy.Serialize(new ProjectLoadBuffer(legacy,undo.GetBuffer(),Project.Version,ProjectBufferFlags.UndoRedo));
        Check(legacy.UpdateRateMultiplier==4&&legacy.Songs[0].LogicRateMultiplier==1,"project undo restores the output clock without changing authored timing");
    }
    static void PreserveExistingMusic(string output)
    {
        foreach(bool pal in new[]{false,true})foreach(bool ft in new[]{false,true})
        {
            var p=new Project{PalMode=pal,TempoMode=ft?TempoType.FamiTracker:TempoType.FamiStudio};
            var i=p.CreateInstrument(0);i.VolumeEnvelope.Length=6;i.VolumeEnvelope.Loop=1;i.VolumeEnvelope.Release=4;
            for(int n=0;n<6;n++)i.VolumeEnvelope.Values[n]=(sbyte)(15-n*3);
            var s=p.CreateSong("Preserved "+(ft?"FamiTracker":"FamiStudio"));s.SetLength(1);s.SetLoopPoint(-1);s.SetDefaultPatternLength(24);s.SetBeatLength(4);
            if(!ft)s.ChangeFamiStudioTempoGroove(new[]{1},false);else{s.FamitrackerSpeed=3;s.FamitrackerTempo=150;}
            var pattern=s.Channels[0].GetOrCreatePattern(0);pattern.Notes[0]=new Note(46){Instrument=i,Duration=24,Release=16};
            pattern.Notes[4]=new Note{VibratoDepth=3,VibratoSpeed=6};pattern.Notes[8]=new Note{EnvelopeDecay=2};pattern.Notes[12]=new Note{EnvelopeDecay=0};
            var baseline=new Player(pal);baseline.Render(s);float bpm=s.BPM;
            foreach(int rate in new[]{2,4,1})
            {
                p.UpdateRateMultiplier=rate;var actual=new Player(pal);actual.Render(s);
                Check(s.LogicRateMultiplier==1&&s.BPM==bpm,$"existing {(ft?"FT":"FS")} tempo stays unchanged at output {rate}x, PAL {pal}");
                Check(actual.Audio.SequenceEqual(baseline.Audio),$"existing {(ft?"FT":"FS")} music, vibrato, decay and release have identical PCM at output {rate}x, PAL {pal}");
            }
            p.UpdateRateMultiplier=4;
            Check(!new NsfFile().Save(p,FamiToneKernel.FamiStudio,Path.Combine(output,"cross-region.nsf"),new[]{s.Id},"","","",pal?MachineType.NTSC:MachineType.PAL,false),"high-rate cross-region NSF rejects temporal adaptation");
            Check(!new NsfFile().Save(p,FamiToneKernel.FamiStudio,Path.Combine(output,"dual-region.nsfe"),new[]{s.Id},"","","",MachineType.Dual,true),"high-rate dual-region NSFe explicitly rejects loss");
            var created=p.CreateSong("New high-resolution song");Check(created.LogicRateMultiplier==4,"new song adopts the output clock without changing existing songs");
            var error=p.GetUpdateRateError(1);Check(error!=null&&error.Contains(created.Name),"lower-rate refusal identifies the incompatible song");
            bool rejected=false;try{p.UpdateRateMultiplier=1;}catch(ArgumentException){rejected=true;}
            Check(rejected&&p.UpdateRateMultiplier==4&&created.LogicRateMultiplier==4,"refused output change leaves the project untouched");
        }
        foreach(bool relative in new[]{false,true})
        {
            var e=new Envelope(relative?EnvelopeType.Pitch:EnvelopeType.Volume){Length=4,Relative=relative,Loop=1,Release=3};
            for(int n=0;n<4;n++)e.Values[n]=(sbyte)(n+1);var before=e.ShallowClone();
            Check(EnvelopeClockConversion.TryConvert(e,4,false)&&e.Length==16&&e.Loop==4&&e.Release==(relative?-1:12),"macro clock upgrade keeps duration and loop/release boundaries");
            Check(EnvelopeClockConversion.TryConvert(e,1,false)&&Envelope.AreIdentical(relative?EnvelopeType.Pitch:EnvelopeType.Volume,e,before),"macro clock round trip is lossless for "+(relative?"relative":"absolute")+" values");
            e.TickRateMultiplier=4;var rejected=e.ShallowClone();
            Check(!EnvelopeClockConversion.TryConvert(e,1,false)&&Envelope.AreIdentical(relative?EnvelopeType.Pitch:EnvelopeType.Volume,e,rejected),"non-aligning macro downgrade refuses without mutation");
        }
        var pulse=new Envelope(EnvelopeType.NoiseEnvelopeRestart){Length=2};pulse.Values[0]=2;pulse.Values[1]=32;
        Check(EnvelopeClockConversion.TryConvert(pulse,4,false,true)&&pulse.Values.Take(8).SequenceEqual(new sbyte[]{2,0,0,0,32,0,0,0}),"restart conversion inserts silence instead of retriggering the hardware");
        var large=new Envelope(EnvelopeType.Volume){Length=256};
        Check(!EnvelopeClockConversion.TryConvert(large,4,false)&&large.Length==256&&large.TickRateMultiplier==1,"macro capacity overflow refuses without truncation");
        var repeat=new Envelope(EnvelopeType.WaveformRepeat){Length=2,Loop=1};repeat.Values[0]=1;repeat.Values[1]=3;var originalRepeat=repeat.ShallowClone();
        Check(EnvelopeClockConversion.TryConvert(repeat,4,true)&&repeat.Length==2&&repeat.Loop==1&&repeat.Values.Take(2).SequenceEqual(new sbyte[]{4,12}),"waveform hold counts scale without adding waveforms");
        Check(EnvelopeClockConversion.TryConvert(repeat,1,true)&&Envelope.AreIdentical(EnvelopeType.WaveformRepeat,repeat,originalRepeat),"waveform hold clocks round trip without timing loss");
        repeat.Values[1]=4;originalRepeat=repeat.ShallowClone();
        Check(!EnvelopeClockConversion.TryConvert(repeat,4,true)&&Envelope.AreIdentical(EnvelopeType.WaveformRepeat,repeat,originalRepeat),"waveform hold overflow refuses without mutation");
    }
    static void ExpansionClocks()
    {
        foreach(bool pal in new[]{false,true})
        for(int chip=ExpansionType.Vrc6;chip<ExpansionType.Count;chip++)
        {
            Dictionary<int,int> normal=null;
            foreach(int rate in new[]{1,2,4})
            {
                var p=Fixture(rate,pal);p.SetExpansionAudioMask(1<<(chip-1),8);
                var song=p.Songs[0];song.Channels[0].PatternInstances[0].Notes.Clear();
                var channel=song.Channels.First(c=>c.Expansion==chip&&c.SupportsEffect(Note.EffectFinePitch));
                var instrument=p.CreateInstrument(chip);channel.GetOrCreatePattern(0).Notes[0]=new Note(46){Instrument=instrument,Duration=240};
                var player=new Player(pal,p.OutputsStereoAudio);player.Render(song);
                int channels=p.OutputsStereoAudio?2:1;
                Check(player.Audio.Any(v=>v!=0)&&Math.Abs(player.Audio.Count/(44100.0*channels)-240.0/(p.UpdatesPerSecond))<.002,$"expansion {chip}, PAL {pal}, {rate}x renders audible PCM with exact duration");
                Check(player.Sizes.Max()-player.Sizes.Min()<=channels*2,$"expansion {chip}, PAL {pal}, {rate}x has uniform subframes");
                if(normal==null)normal=player.Registers;
                else Check(normal.OrderBy(k=>k.Key).SequenceEqual(player.Registers.OrderBy(k=>k.Key)),$"expansion {chip}, PAL {pal}, {rate}x retains every programmed pitch/control register");
            }
        }
    }
    static int ActiveDpcmCalls(string file,int period)
    {
        // Original synthetic NSF. Hardware consumes a 257-byte DPCM sample at
        // a fixed CPU clock while PLAY contains only RTS. Its lifetime in PLAY
        // intervals therefore measures real CPU scheduling, not header parsing.
        var data=new byte[128+0x4101];System.Text.Encoding.ASCII.GetBytes("NESM").CopyTo(data,0);data[4]=26;data[5]=1;data[6]=1;data[7]=1;
        void Word(int offset,int value){data[offset]=(byte)value;data[offset+1]=(byte)(value>>8);}
        Word(8,0x8000);Word(10,0x8000);Word(12,0x8040);Word(110,period);Word(120,19997);
        byte[] init={0xa9,0x0f,0x8d,0x15,0x40,0x8d,0x10,0x40,0xa9,0,0x8d,0x12,0x40,0xa9,16,0x8d,0x13,0x40,0xa9,31,0x8d,0x15,0x40,0x60};
        init.CopyTo(data,128);data[128+0x40]=0x60;for(int n=128+0x4000;n<data.Length;n++)data[n]=(byte)(n*37);
        File.WriteAllBytes(file,data);var h=NotSoFatso.NsfOpen(file);Check(h!=IntPtr.Zero,"synthetic CPU clock NSF opens "+period);
        int active=0;try{NotSoFatso.NsfSetTrack(h,0);for(int n=0;n<32;n++){NotSoFatso.NsfRunFrame(h);if(NotSoFatso.NsfGetState(h,4,NotSoFatso.STATE_DPCMACTIVE,0)!=0)active++;}}finally{NotSoFatso.NsfClose(h);}return active;
    }
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);checks=0;
        Check(new Project().UpdateRateMultiplier==1,"new and legacy default is 1x");
        bool invalid=false;try{new Project().UpdateRateMultiplier=3;}catch(ArgumentOutOfRangeException){invalid=true;}Check(invalid,"reject invalid multiplier");
        Check(new ProjectFile().Load("Tests/TuningRegression/Fixtures/format1004-dpcm.fms").UpdateRateMultiplier==1,"old format restores 1x");
        foreach(bool pal in new[]{false,true}) foreach(int rate in new[]{1,2,4})
        {
            var p=Fixture(rate,pal);var s=p.Songs[0];var player=new Player(pal);player.Render(s);
            double expected=240.0/((pal?NesApu.FpsPAL:NesApu.FpsNTSC)*rate);
            Check(player.Sizes.Count==240,"logical tick count "+pal+" "+rate);
            Check(Math.Abs(player.Audio.Count/44100.0-expected)<.002,"actual PCM duration follows rate "+pal+" "+rate);
            Check(player.Sizes.Max()-player.Sizes.Min()<=2,"uniform audio subframes "+pal+" "+rate);
            Check(Math.Abs(Frequency(player.Audio)-440)<3,"chip pitch unchanged "+pal+" "+rate);
            Check(Math.Abs(s.Duration.TotalSeconds-expected)<.001,"song duration uses logical clock "+pal+" "+rate);
            var one=Fixture(1,pal);Check(Math.Abs(s.BPM-one.Songs[0].BPM*rate)<.001,"BPM reflects real clock "+pal+" "+rate);
            string fms=Path.Combine(output,$"rate-{pal}-{rate}.fms"),txt=Path.ChangeExtension(fms,"txt");
            Check(new ProjectFile().Save(p,fms)&&new FamistudioTextFile().Save(p,txt,new[]{s.Id},false),"binary and text save "+pal+" "+rate);
            Check(new ProjectFile().Load(fms).UpdateRateMultiplier==rate&&new FamistudioTextFile().Load(txt).UpdateRateMultiplier==rate&&p.DeepClone().UpdateRateMultiplier==rate,"binary/text/clone retain rate "+pal+" "+rate);
            foreach(bool nsfe in new[]{false,true})
            {
                string path=Path.ChangeExtension(fms,nsfe?"nsfe":"nsf");
                Check(new NsfFile().Save(p,FamiToneKernel.FamiStudio,path,new[]{s.Id},"Original update test","","",pal?MachineType.PAL:MachineType.NTSC,nsfe),"NSF/NSFe export "+pal+" "+rate+" "+nsfe);
                var handle=NotSoFatso.NsfOpen(path);Check(handle!=IntPtr.Zero,"NSF/NSFe decoder accepts RATE "+pal+" "+rate+" "+nsfe);
                try{Check(NotSoFatso.NsfGetClockSpeed(handle)==(int)Math.Round((pal?19997.0:16639.0)/rate),"call period roundtrip "+pal+" "+rate+" "+nsfe);}finally{NotSoFatso.NsfClose(handle);}
            }
            var alternating=Fixture(rate,pal,true);var fast=new Player(pal);fast.Render(alternating.Songs[0]);
            Check(!player.Audio.SequenceEqual(fast.Audio)&&SubframeContrast(fast),"alternating subframe on/off events remain audible in distinct time windows "+pal+" "+rate);
            var metadataPlayer=new VideoMetadataPlayer(44100,pal,false,false,1);var metadata=metadataPlayer.GetVideoMetadata(s,-1);metadataPlayer.Shutdown();
            Check(metadata.Length>=239&&metadata.Length<=240&&metadata.Zip(metadata.Skip(1),(a,b)=>b.wavOffset-a.wavOffset).All(n=>n>=player.Sizes.Min()&&n<=player.Sizes.Max()),"video metadata follows actual subframe audio offsets "+pal+" "+rate);
            if(rate>1) {
                Check(!new RomFile().Save(p,Path.Combine(output,"unsupported.nes"),new[]{s.Id},"","",pal),"old NES template rejects high rate");
                Check(!new FdsFile().Save(p,Path.Combine(output,"unsupported.fds"),new[]{s.Id},"","",pal),"old FDS template rejects high rate");
                Check(!new FamitrackerTextFile().Save(p,Path.Combine(output,"unsupported-ft.txt"),new[]{s.Id}),"legacy tracker export rejects loss");
                Check(!new MidiFileWriter().Save(p,Path.Combine(output,"unsupported.mid"),s.Id,MidiExportInstrumentMode.Channel,new int[s.Channels.Length],false,false,2),"MIDI export rejects loss");
            }
        }
        int a=ActiveDpcmCalls(Path.Combine(output,"clock-1.nsf"),16639),b=ActiveDpcmCalls(Path.Combine(output,"clock-2.nsf"),8320),c=ActiveDpcmCalls(Path.Combine(output,"clock-4.nsf"),4160);
        Console.WriteLine($"Hardware DPCM lifetime: {a}/{b}/{c} calls");
        Check(a>=2&&Math.Abs(b-a*2)<=2&&Math.Abs(c-a*4)<=4&&c>b&&b>a,"CPU truly honors 1x/2x/4x intervals using fixed-clock DPCM lifetime");
        var high=Fixture(4);var stereoSong=high.Songs[0];stereoSong.Channels[0].StereoEnabled=true;stereoSong.Channels[0].Pan=0;
        var stereoPlayer=new Player(false,true);stereoPlayer.Render(stereoSong);Check(stereoPlayer.Audio.Count>0&&stereoPlayer.Audio.Skip(12000).Where((v,n)=>(n&1)==1).All(v=>v==0),"stereo stems share exact subframe clocks");
        var imported=new NsfFile().Load(Path.Combine(output,"rate-False-4.nsf"),0,1,240,0,false,false,true,false);
        Check(imported!=null&&imported.UpdateRateMultiplier==4,"high-rate NSF import retains the clock");
        Check(!Fixture(1).MergeProject(Fixture(4)),"incompatible song merge refuses timing loss");
        var macroSource=new Project{UpdateRateMultiplier=4};var macro=macroSource.CreateInstrument(0,"High-rate imported macro");macro.VolumeEnvelope.Length=2;
        var macroTarget=Fixture(1);int sourceId=macro.Id;
        Check(!macroTarget.MergeProject(macroSource)&&macro.Id==sourceId&&macroTarget.UpdateRateMultiplier==1,"instrument-only high-rate import refuses before mutating IDs or output clock");
        var emptyHigh=new Project{UpdateRateMultiplier=4};emptyHigh.CreateInstrument(0,"Existing high-rate macro");
        Check(emptyHigh.MergeProject(Fixture(1))&&emptyHigh.UpdateRateMultiplier==4&&emptyHigh.Songs[0].LogicRateMultiplier==1,"song import into existing high-rate instruments keeps both clocks intact");
        string badText=Path.Combine(output,"incompatible-clocks.txt");
        File.WriteAllText(badText,File.ReadAllText(Path.Combine(output,"rate-False-4.txt")).Replace("UpdateRateMultiplier=\"4\"","UpdateRateMultiplier=\"1\""));
        Check(new FamistudioTextFile().Load(badText)==null,"text import rejects song and macro clocks above the declared output clock");
        var oldGroove=Fixture(1);oldGroove.Songs[0].ChangeFamiStudioTempoGroove(new[]{48},false);
        var tempoProps=(TempoProperties)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TempoProperties));
        typeof(TempoProperties).GetField("song",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(tempoProps,oldGroove.Songs[0]);
        var tempi=(TempoInfo[])typeof(TempoProperties).GetMethod("GetAvailableTempos",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(tempoProps,new object[]{4,new[]{48}});
        Check(FamiStudioTempoUtils.FindTempoFromGroove(tempi,new[]{48})>=0,"tempo menu retains a 4x groove after returning to 1x");
        PreviewAndAudioExport();
        ExpansionClocks();
        PreserveExistingMusic(output);
        Console.WriteLine($"All {checks} update-rate checks passed.");
    }
}
