using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FamiStudio;

// Execute the exported 6502 stream and compare it with the actual editor player.
static class ApuInstrumentExportRegression
{
    sealed class Capture : BasePlayer
    {
        readonly List<(int Address, int Value)> pending = new();
        readonly Dictionary<int,int> registers = new();
        public readonly List<int[]> Periods = new();
        public readonly List<int[]> Volumes = new();
        public readonly List<(int Address,int Value)[]> Writes = new();
        static readonly MethodInfo Period = typeof(ChannelState).GetMethod("GetPeriod", BindingFlags.Instance | BindingFlags.NonPublic);
        public Capture(bool pal) : base(NesApu.APU_WAV_EXPORT,pal,false) { Loop=LoopMode.Song;maxLoopCount=3; }
        public override void NotifyRegisterWrite(int apu,int address,int value,int metadata=0) { registers[address]=value;pending.Add((address,value)); }
        protected override short[] EndFrame()
        {
            var samples=base.EndFrame();
            Periods.Add(channelStates.Take(3).Select(c=>(int)Period.Invoke(c,null)).ToArray());
            Volumes.Add(new[]{registers.GetValueOrDefault(0x4000)&15,registers.GetValueOrDefault(0x4004)&15,(registers.GetValueOrDefault(0x4008)&127)!=0?15:0});
            Writes.Add(pending.ToArray());pending.Clear();return samples;
        }
        public void Render(Song song) { try { BeginPlaySong(song);pending.Clear();while(PlaySongFrame()){} } finally { Shutdown(); } }
    }
    static int checks;
    static void Check(bool ok,string label) { if(!ok)throw new Exception(label);checks++;Console.WriteLine("PASS APU instrument export: "+label); }
    static void Envelope(Instrument i,int type,bool relative,params int[] values)
    {
        var e=i.Envelopes[type];e.Length=values.Length;e.Relative=relative;
        for(int k=0;k<values.Length;k++)e.Values[k]=(sbyte)values[k];
    }
    static Project Fixture(int scenario,bool pal)
    {
        var p=new Project{PalMode=pal};
        var plain=p.CreateInstrument(0,"Legacy");var active=p.CreateInstrument(0,"APU flags");
        active.PitchClampToTimerByte=scenario!=0;active.ResetPhaseOnAttack=scenario!=1;
        int[] pitch=scenario==3?new[]{63,63,63,63,63,63,63,63,-64,-64,-64,-64,-64,-64,-64,-64}:new[]{0,20,20,-20,-20,63,-64,0};
        Envelope(active,EnvelopeType.Pitch,scenario==3,pitch);active.PitchEnvelope.Loop=0;
        if(scenario==4) { Envelope(active,EnvelopeType.Arpeggio,false,0,1,0,1);active.ArpeggioEnvelope.Loop=0; }
        if(scenario!=5) { p.ChipTuning.SetValue(0,46,506);p.ChipTuning.SetValue(0,47,259); }
        if(scenario==7) { p.ChipTuning.SetValue(0,46,2040);p.ChipTuning.SetValue(0,47,8); }
        var song=p.CreateSong("APU articulation");song.ChangeFamiStudioTempoGroove(new[]{1},false);
        song.SetLength(2);song.SetDefaultPatternLength(32);song.SetBeatLength(4);song.SetLoopPoint(0);
        for(int c=0;c<3;c++)for(int pat=0;pat<2;pat++)
        {
            var pattern=song.Channels[c].GetOrCreatePattern(pat);
            pattern.Notes[0]=new Note(46){Instrument=pat==0?plain:active,Duration=8,FinePitch=0};
            pattern.Notes[8]=new Note(46){Instrument=active,Duration=8,FinePitch=(sbyte)(c==0?0:7)};
            pattern.Notes[16]=new Note(47){Instrument=active,Duration=8,FinePitch=(sbyte)(c==0?-7:0),HasAttack=false};
            pattern.Notes[24]=new Note(47){Instrument=active,Duration=8,FinePitch=0};
            if(scenario==2 && c<2) pattern.Notes[24].PhaseReset=1;
            if(scenario==6) { pattern.Notes[8].SlideNoteTarget=47; }
        }
        return p;
    }
    public static void Run(string output)
    {
        checks=0;Directory.CreateDirectory(output);
        foreach(bool pal in new[]{false,true})foreach(bool smooth in new[]{false,true})foreach(int scenario in Enumerable.Range(0,8))
        {
            Settings.SquareSmoothVibrato=smooth;var project=Fixture(scenario,pal);var song=project.Songs[0];
            var player=new Capture(pal);player.Render(song);
            string path=Path.Combine(output,$"apu-{scenario}-{pal}-{smooth}.nsf");
            string label=$"scenario {scenario}, PAL {pal}, smooth {smooth}";
            Check(new NsfFile().Save(project,FamiToneKernel.FamiStudio,path,new[]{song.Id},"APU flags","Test","",pal?MachineType.PAL:MachineType.NTSC,false),"exports "+label);
            var handle=NotSoFatso.NsfOpen(path);Check(handle!=IntPtr.Zero,"opens "+label);
            var pending=new List<(int Address,int Value)>();var registers=new Dictionary<int,int>();var errors=new List<string>();
            NotSoFatso.WriteRegisterDelegate callback=(a,v)=>{pending.Add((a,v));registers[a]=v;};
            try
            {
                NotSoFatso.NsfSetApuWriteCallback(handle,callback);NotSoFatso.NsfSetTrack(handle,0);NotSoFatso.NsfRunFrame(handle);pending.Clear();
                for(int frame=0;frame<player.Periods.Count;frame++)
                {
                    NotSoFatso.NsfRunFrame(handle);
                    for(int c=0;c<3;c++)
                    {
                        int period=NotSoFatso.NsfGetState(handle,c,NotSoFatso.STATE_PERIOD,0);
                        int volume=c==2?((registers.GetValueOrDefault(0x4008)&127)!=0?15:0):registers.GetValueOrDefault(0x4000+c*4)&15;
                        if(volume!=player.Volumes[frame][c])errors.Add($"frame {frame} channel {c} volume {volume}/{player.Volumes[frame][c]}");
                        if(volume!=0 && period!=player.Periods[frame][c])errors.Add($"frame {frame} channel {c} period {period}/{player.Periods[frame][c]}");
                        if(c<2 && smooth)
                        {
                            int address=0x4003+c*4;
                            // Embedded NSF kernels enable smooth vibrato. Only compare write
                            // cadence with the matching editor setting.
                            // Literal manual PhaseReset keeps its legacy extra write; the
                            // instrument flag must share the normal high-byte write instead.
                            var actual=pending.Where(w=>w.Address==address).Select(w=>w.Value&7);
                            var expected=player.Writes[frame].Where(w=>w.Address==address).Select(w=>w.Value&7);
                            if(!actual.SequenceEqual(expected))errors.Add($"frame {frame} channel {c} high writes [{string.Join(',',actual)}]/[{string.Join(',',expected)}]");
                        }
                    }
                    pending.Clear();
                }
            }
            finally { NotSoFatso.NsfClose(handle);GC.KeepAlive(callback); }
            File.WriteAllLines(path+".comparison.txt",errors.Count==0?new[]{$"PASS {player.Periods.Count} actual player/CPU frames"}:errors);
            Check(errors.Count==0,"exact periods, volume and phase-write sequence: "+label);
        }
        Settings.SquareSmoothVibrato=false;
        foreach(bool clamp in new[]{false,true})
        {
            var p=new Project();var i=p.CreateInstrument(0,"Only new option");
            i.PitchClampToTimerByte=clamp;i.ResetPhaseOnAttack=!clamp;
            var s=p.CreateSong();s.ChangeFamiStudioTempoGroove(new[]{1},false);s.SetLength(1);s.SetDefaultPatternLength(8);
            s.Channels[0].GetOrCreatePattern(0).Notes[0]=new Note(46){Instrument=i,Duration=8};
            string path=Path.Combine(output,$"unsupported-{clamp}.asm");
            Check(!new FamitoneMusicFile(FamiToneKernel.FamiTone2,false).Save(p,new[]{s.Id},AssemblyFormat.ASM6,0x4000,false,path,Path.ChangeExtension(path,".dmc"),DpcmExportMode.All,false,null,MachineType.NTSC),"FamiTone2 rejects the isolated new option: clamp="+clamp);
            Check(!File.Exists(path),"unsupported legacy export leaves no misleading music file: clamp="+clamp);
        }
        Console.WriteLine($"All {checks} APU instrument-export checks passed.");
    }
}
