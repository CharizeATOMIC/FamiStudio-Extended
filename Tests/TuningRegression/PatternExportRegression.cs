using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FamiStudio;

static class PatternExportRegression
{
    sealed class Player : BasePlayer
    {
        readonly Dictionary<int,int> registers = new();
        public readonly List<(int Period,int Volume)> Frames = new();
        public Player(bool pal, bool loop) : base(NesApu.APU_WAV_EXPORT,pal,false) { Loop=loop?LoopMode.Song:LoopMode.None;maxLoopCount=4; }
        public override void NotifyRegisterWrite(int apu,int address,int value,int metadata=0) => registers[address]=value;
        protected override short[] EndFrame() { var audio=base.EndFrame();Frames.Add((registers.GetValueOrDefault(0x4002)|((registers.GetValueOrDefault(0x4003)&7)<<8),registers.GetValueOrDefault(0x4000)&15));return audio; }
        public void Render(Song song) { try { BeginPlaySong(song);while(PlaySongFrame()) {} } finally { Shutdown(); } }
    }
    static int checks;
    static void Check(bool ok,string message) { if(!ok)throw new Exception(message);checks++;Console.WriteLine("PASS pattern export: "+message); }
    static Project Fixture(int scenario,bool sourcePal)
    {
        var project=new Project{PalMode=sourcePal};
        if(scenario==7) project.TempoMode=TempoType.FamiTracker;
        var instrument=project.CreateInstrument(ExpansionType.None,"Programmed pulse");
        instrument.VolumeEnvelope.Length=3;instrument.VolumeEnvelope.Values[0]=15;instrument.VolumeEnvelope.Values[1]=11;instrument.VolumeEnvelope.Values[2]=7;
        var song=project.CreateSong("Delayed editable events");song.ChangeFamiStudioTempoGroove(new[]{1},false);
        int length=scenario==4?34:16;
        song.SetDefaultPatternLength(length);song.SetBeatLength(2);song.SetLength(3);song.SetLoopPoint(scenario==6?-1:1);
        var channel=song.Channels[0];
        var intro=channel.GetOrCreatePattern(0);intro.PlaybackDelay=scenario==1?10:2;
        intro.Notes[0]=new Note(37){Instrument=instrument,Duration=3};
        intro.Notes[length-2]=new Note(40){Instrument=instrument,Duration=4};
        var body=channel.GetOrCreatePattern(1);body.PlaybackDelay=scenario==1?1:7;
        body.Notes[0]=new Note(44){Instrument=instrument,Duration=3};
        body.Notes[4]=new Note(46){Instrument=instrument,Duration=2};
        var tail=channel.GetOrCreatePattern(2);tail.PlaybackDelay=body.PlaybackDelay;
        tail.Notes[length-2]=new Note(49){Instrument=instrument,Duration=4};
        if(scenario==2 || scenario==5 || scenario==7) {
            var variant=channel.CreatePattern("Changed repeat");variant.PlaybackDelay=3;
            variant.Notes[0]=new Note(51){Instrument=instrument,Duration=5};
            variant.Notes[8]=new Note(53){Instrument=instrument,Duration=3};body.LoopVariant=variant;
        }
        if(scenario==3) {
            song.SetPatternCustomSettings(0,length,4,new[]{2,3});
            song.SetPatternCustomSettings(1,length,4,new[]{2,3});
        }
        if(scenario==4) for(int p=0;p<3;p++)song.SetPatternCustomSettings(p,length,34,new[]{17});
        if(scenario==7) {
            foreach(var pattern in channel.Patterns) pattern.PlaybackDelay=0;
            song.FamitrackerTempo=sourcePal?125:150;song.FamitrackerSpeed=3;
        }
        if(scenario==8) {
            // A delay on a different voice enables owner-aware ends globally.
            // Square 1's tail must stop after the loop even though it has no delay.
            foreach(var pattern in channel.Patterns) pattern.PlaybackDelay=0;
            song.Channels[1].GetOrCreatePattern(1).PlaybackDelay=7;
            tail.Notes[length-2].Duration=6;
            body.Notes.Remove(0);
        }
        return project;
    }
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);Settings.SquareSmoothVibrato=false;checks=0;
        foreach(int scenario in Enumerable.Range(0,9))
        foreach(bool pal in new[]{false,true})
        {
            var project=Fixture(scenario,scenario==5?!pal:pal);var song=project.Songs[0];
            var player=new Player(pal,song.LoopPoint>=0);player.Render(song);
            string path=Path.Combine(output,$"delay-{scenario}-{pal}.nsf");
            Check(new FamitoneMusicFile(FamiToneKernel.FamiStudio,false).Save(project,new[]{song.Id},AssemblyFormat.ASM6,0x4000,false,Path.ChangeExtension(path,".asm"),Path.ChangeExtension(path,".dmc"),DpcmExportMode.All,false,null,pal?MachineType.PAL:MachineType.NTSC),"assembly export");
            Check(new NsfFile().Save(project,FamiToneKernel.FamiStudio,path,new[]{song.Id},"Delay test","Test","",pal?MachineType.PAL:MachineType.NTSC,false),$"scenario {scenario} {(pal?"PAL":"NTSC")} export");
            IntPtr handle=NotSoFatso.NsfOpen(path);Check(handle!=IntPtr.Zero,"NSF opens");
            var registers=new Dictionary<int,int>();NotSoFatso.WriteRegisterDelegate callback=(address,value)=>registers[address]=value;
            try {
                NotSoFatso.NsfSetApuWriteCallback(handle,callback);NotSoFatso.NsfSetTrack(handle,0);
                NotSoFatso.NsfRunFrame(handle); // NSF INIT precedes the first music tick.
                for(int frame=0;frame<player.Frames.Count;frame++) {
                    NotSoFatso.NsfRunFrame(handle);
                    var expected=player.Frames[frame];int volume=registers.GetValueOrDefault(0x4000)&15;
                    int period=NotSoFatso.NsfGetState(handle,0,NotSoFatso.STATE_PERIOD,0);
                    Check(volume==expected.Volume && (volume==0 || period==expected.Period),$"scenario {scenario} {(pal?"PAL":"NTSC")} frame {frame}: expected {expected}, actual ({period},{volume})");
                }
            } finally { NotSoFatso.NsfClose(handle);GC.KeepAlive(callback); }
            Check(song.Channels[0].PatternInstances[0].PlaybackDelay==(scenario==7||scenario==8?0:scenario==1?10:2),"export preserves source pattern delay");
        }
        var unsupported=Fixture(7,false);unsupported.Songs[0].Channels[0].PatternInstances[0].PlaybackDelay=1;
        var rejected=Path.Combine(output,"unsupported-delay.nsf");
        Check(!new NsfFile().Save(unsupported,FamiToneKernel.FamiStudio,rejected,new[]{unsupported.Songs[0].Id},"Unsupported","Test","",MachineType.NTSC,false),"FamiTracker delay fails explicitly without parsing stale assembly");
        var decay=Fixture(0,false);decay.Songs[0].Channels[0].PatternInstances[0].Notes[3]=new Note{EnvelopeDecay=2};
        Check(!new FamitoneMusicFile(FamiToneKernel.FamiTone2,false).Save(decay,new[]{decay.Songs[0].Id},AssemblyFormat.ASM6,0x4000,false,Path.Combine(output,"unsupported-decay.asm"),null,DpcmExportMode.All,false,null,MachineType.NTSC),"FamiTone2 rejects envelope decay");
        Console.WriteLine($"PASS all {checks} pattern-export checks");
    }
}
