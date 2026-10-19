using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FamiStudio;

class CapturePlayer : BasePlayer
{
    public Dictionary<int,int> Registers = new();
    public List<Dictionary<int,int>> Frames = new();
    public List<short> Audio = new();
    public CapturePlayer(bool pal, bool stereo) : base(NesApu.APU_WAV_EXPORT,pal,stereo) { Loop=LoopMode.None; }
    public override void NotifyRegisterWrite(int apu,int reg,int value,int metadata=0) { Registers[reg]=value; }
    protected override short[] EndFrame() { var a=base.EndFrame();Audio.AddRange(a);Frames.Add(new(Registers));return a; }
    public void Render(Song song) { BeginPlaySong(song);while(PlaySongFrame());Shutdown(); }
}

static class Program
{
    static int checks;
    static void Check(bool ok,string message) { if(!ok)throw new Exception(message);checks++;Console.WriteLine("PASS "+message); }
    static int Pulse(Dictionary<int,int> registers) => registers[0x4002] | ((registers[0x4003]&7)<<8);
    public static Project MakeProject()
    {
        var p=new Project();var inst=p.CreateInstrument(ExpansionType.None,"Pulse");
        var song=p.CreateSong("432 to 450");song.ChangeFamiStudioTempoGroove(new[]{1},false);song.SetDefaultPatternLength(32);song.SetBeatLength(4);song.SetLength(2);song.SetLoopPoint(-1);
        song.TuningSettings=new TuningSettings{Override=true,ReferenceHz=432};
        song.GetPatternCustomSettings(1).Tuning=new TuningSettings{Override=true,ReferenceHz=450};
        song.Channels[0].GetOrCreatePattern(0).Notes[0]=new Note(46){Instrument=inst,Duration=64};
        return p;
    }
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--ui") { UiSmoke.Run(args); return; }
        Init.InitializeBaseSystems(true);
        if (args.Length > 0 && args[0] == "--engine-clock-demo") { EngineClockExportRegression.Demo(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--engine-clocks") { EngineClockExportRegression.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--project-compression") { ProjectCompressionRegression.Run(args[1], args.Length > 2 ? args[2] : null); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--envelope-clock") { EnvelopeClockRegression.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--update-rate") { UpdateRateRegression.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--pulse-sweep") { PulseSweepRegression.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--pulse-sweep-nsf") { PulseSweepRegression.RunNsf(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--dpcm-effects") { DpcmEffectRegression.Run(args[1]); GimmickEffectCleanup.Regression(); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--text-tempo") { TextTempoRegression.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--articulation-playback") { InstrumentArticulationPlaybackRegression.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--duty-envelope") { DutyEnvelopeRegression.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--apu-instrument-export") { ApuInstrumentExportRegression.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--instrument-articulation") { InstrumentArticulationRegression.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--gimmick-convert") { GimmickConversion.Run(args[1], args[2]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--gimmick-nsf") { GimmickNsfRegression.Run(args[1], args[2]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--noise-envelopes") { NoiseEnvelopeRegression.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--envelope-decay") { EnvelopeDecayRegression.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--pattern-delay") { PatternDelayRegression.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--pattern-export") { PatternExportRegression.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--loop-variant") { LoopVariantRegression.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--legacy-export") { LegacyExportRegression.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--chip-curves") { ChipCurveRegression.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--nsf-multi") { NsfMultiImportRegression.Run(args[1], Check); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--demo") { Demo.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--nes-demo") { NesDemo.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        if (args.Length > 0 && args[0] == "--nes-r4") { NesR4Verification.Run(args[1]); Init.ShutdownBaseSystems(); return; }
        Settings.SquareSmoothVibrato=false;
        string output=Path.GetFullPath(args.Length>0?args[0]:"test-output");Directory.CreateDirectory(output);
        var p=MakeProject();var song=p.Songs[0];
        Check(Math.Abs(song.TuningSettings.Frequency(46)-432)<1e-9,"A4 reference");
        var tuning=song.TuningSettings.Clone();tuning.Cents[0]=50;
        Check(Math.Abs(tuning.Frequency(49)/tuning.Frequency(37)-2)<1e-12,"temperament repeats every octave");
        Check(p.Tuning==440,"song override preserves project tuning");
        var clone=p.DeepClone();Check(clone.Songs[0].GetTuningHz()==432 && clone.Songs[0].GetTuningHz(1)==450,"clone and undo serialization preserve inheritance");
        string fms=Path.Combine(output,"tuning.fms"),txt=Path.Combine(output,"tuning.txt");
        Check(new ProjectFile().Save(p,fms),"binary save");
        Check(new ProjectFile().Load(fms).Songs[0].GetTuningHz(1)==450,"binary load");
        Check(new FamistudioTextFile().Save(p,txt,new[]{song.Id},false),"text export");
        Check(new FamistudioTextFile().Load(txt).Songs[0].GetTuningHz(1)==450,"text import");
        foreach(bool pal in new[]{false,true})
        {
            var player=new CapturePlayer(pal,false);player.Render(song);
            int first=NesApu.GetNoteTableForChannelType(ChannelType.Square1,pal,1,432,new int[12])[46];
            int second=NesApu.GetNoteTableForChannelType(ChannelType.Square1,pal,1,450,new int[12])[46];
            Check(Pulse(player.Frames[8])==first,$"editor {(pal?"PAL":"NTSC")} song tuning");
            Check(Pulse(player.Frames[45])==second,$"editor {(pal?"PAL":"NTSC")} pattern tuning on sustained note");
            var nsfPath=Path.Combine(output,pal?"tuning-pal.nsf":"tuning-ntsc.nsf");
            Check(new NsfFile().Save(p,FamiToneKernel.FamiStudio,nsfPath,new[]{song.Id},p.Name,p.Author,p.Copyright,pal?MachineType.PAL:MachineType.NTSC,false),"NSF export");
            var nsf=NotSoFatso.NsfOpen(nsfPath);Check(nsf!=IntPtr.Zero,"NSF opens in CPU emulator");
            var regs=new Dictionary<int,int>();NotSoFatso.WriteRegisterDelegate cb=(reg,value)=>regs[reg]=value;
            NotSoFatso.NsfSetApuWriteCallback(nsf,cb);NotSoFatso.NsfSetTrack(nsf,0);
            for(int i=0;i<48;i++) {NotSoFatso.NsfRunFrame(nsf);if(i==8)Check(NotSoFatso.NsfGetState(nsf,0,NotSoFatso.STATE_PERIOD,0)==first,"NSF song pitch matches editor");}
            Check(NotSoFatso.NsfGetState(nsf,0,NotSoFatso.STATE_PERIOD,0)==second,"NSF pattern pitch matches editor");
            NotSoFatso.NsfClose(nsf);GC.KeepAlive(cb);
        }
        song.Channels[0].StereoEnabled=true;song.Channels[0].Pan=0;
        var left=new CapturePlayer(false,true);left.Render(song);
        Check(left.Audio.Where((v,i)=>(i&1)==0).Any(v=>v!=0),"stereo left contains sound");
        Console.WriteLine("DEBUG right max="+left.Audio.Where((v,i)=>(i&1)!=0).Max(v=>Math.Abs((int)v))+" steady="+left.Audio.Skip(12000).Where((v,i)=>(i&1)!=0).Max(v=>Math.Abs((int)v)));
        Check(left.Audio.Skip(12000).Where((v,i)=>(i&1)!=0).All(v=>v==0),"hard left has silent right channel after DAC initialization");
        song.Channels[0].Pan=100;
        var right=new CapturePlayer(false,true);right.Render(song);
        Check(right.Audio.Skip(12000).Where((v,i)=>(i&1)==0).All(v=>v==0),"hard right has silent left channel after DAC initialization");
        Check(right.Audio.Where((v,i)=>(i&1)!=0).SequenceEqual(left.Audio.Where((v,i)=>(i&1)==0)),"panning preserves mono waveform");
        p.SetExpansionAudioMask(ExpansionType.Vrc6Mask);
        var counts=Enumerable.Repeat(1,ExpansionType.Count).ToArray();counts[ExpansionType.Vrc6]=2;p.SetExpansionInstances(counts);
        var vc=song.GetChannelByType(ChannelType.Vrc6Square1,1);
        var vi=p.CreateInstrument(ExpansionType.Vrc6,"Second VRC6");vc.GetOrCreatePattern(0).Notes[0]=new Note(49){Instrument=vi,Duration=60};
        Check(song.Channels.Length==11 && vc.Index==8,"duplicate VRC6 adds distinct editable channels");
        Check(vc.PatternInstances[0].Channel==vc,"pattern belongs to its chip instance");
        clone=p.DeepClone();Check(clone.Songs[0].GetChannelByType(ChannelType.Vrc6Square1,1).PatternInstances[0].Notes[0].Value==49,"duplicate chip round trip");
        var duplicated=new CapturePlayer(false,true);duplicated.Render(song);Check(duplicated.Audio.Any(v=>v!=0),"duplicate chip renders without clock drift");
        Check(!new NsfFile().Save(p,FamiToneKernel.FamiStudio,Path.Combine(output,"unsupported.nsf"),new[]{song.Id},p.Name,p.Author,p.Copyright,MachineType.NTSC,false),"NSF explicitly rejects duplicate chip instances");
        foreach (int mask in new[]{ExpansionType.Vrc6Mask,ExpansionType.Vrc7Mask,ExpansionType.FdsMask,ExpansionType.Mmc5Mask,ExpansionType.N163Mask,ExpansionType.S5BMask,ExpansionType.EPSMMask,127})
        foreach (int machine in new[]{MachineType.NTSC,MachineType.PAL,MachineType.Dual})
        {
            var chipProject=MakeProject();chipProject.SetExpansionAudioMask(mask,8);
            var chipSong=chipProject.Songs[0];
            var profile=chipSong.TuningSettings.Clone();profile.Cents[0]=37;chipSong.TuningSettings=profile;
            chipSong.GetPatternCustomSettings(1).Tuning.Cents[0]=-23;
            foreach (var c in chipSong.Channels.Where(c=>c.SupportsEffect(Note.EffectFinePitch)))
            {
                var instrument=chipProject.Instruments.FirstOrDefault(i=>i.Expansion==c.Expansion)??chipProject.CreateInstrument(c.Expansion);
                c.GetOrCreatePattern(0).Notes.Clear();c.GetOrCreatePattern(0).Notes[0]=new Note(49){Instrument=instrument,Duration=64};
            }
            string path=Path.Combine(output,$"chips-{mask}-{machine}.nsf");
            Check(new NsfFile().Save(chipProject,FamiToneKernel.FamiStudio,path,new[]{chipSong.Id},"Chips","Test","",machine,false),$"export chip mask {mask}, region {machine}");
            var handle=NotSoFatso.NsfOpen(path);Check(handle!=IntPtr.Zero,"open expansion NSF");NotSoFatso.NsfSetTrack(handle,0);
            var epsmRegisters = new int[512]; int epsmAddress0 = 0, epsmAddress1 = 0;
            NotSoFatso.WriteRegisterDelegate chipWrites = (reg, value) => {
                if (reg == NesApu.EPSM_ADDR0) epsmAddress0 = value;
                if (reg == NesApu.EPSM_ADDR1) epsmAddress1 = value;
                if (reg == NesApu.EPSM_DATA0) epsmRegisters[epsmAddress0] = value;
                if (reg == NesApu.EPSM_DATA1) epsmRegisters[256 + epsmAddress1] = value;
            };
            NotSoFatso.NsfSetApuWriteCallback(handle, chipWrites);
            bool pal=NotSoFatso.NsfIsPal(handle)==1;
            for (int frame=0;frame<48;frame++)
            {
                NotSoFatso.NsfRunFrame(handle);
                if(frame!=8 && frame!=45)continue;
                int pattern=frame==8?0:1;
                foreach(var c in chipSong.Channels.Where(c=>c.SupportsEffect(Note.EffectFinePitch)))
                {
                    int expected=NesApu.GetNoteTableForChannelType(c.Type,pal,8,chipSong.GetTuningHz(pattern),chipSong.GetTuning(pattern)?.Cents)[49];
                    int actual=NotSoFatso.NsfGetState(handle,c.Type,NotSoFatso.STATE_PERIOD,0);
                    var fractions = CustomTuningTables.Get(c.Type, pal, 8, chipSong.GetTuningHz(pattern), chipSong.GetTuning(pattern).Cents).Fraction;
                    if(c.IsN163Channel) expected = (expected << 2) | fractions[49];
                    if(c.IsVrc7Channel) actual <<= NotSoFatso.NsfGetState(handle,c.Type,NotSoFatso.STATE_FMOCTAVE,0);
                    if(c.IsEPSMFmChannel)
                    {
                        int idx = c.Type - ChannelType.EPSMFm1, bank = idx < 3 ? 0 : 256, reg = idx % 3;
                        int hi = epsmRegisters[bank + 0xa4 + reg], lo = epsmRegisters[bank + 0xa0 + reg];
                        actual = (((hi & 7) << 8) | lo) << ((hi >> 3) & 7);
                        int octave = 0, mantissa = expected; while (mantissa >= 512) { mantissa >>= 1; octave++; }
                        expected = ((mantissa << 2) | fractions[49]) << octave;
                    }
                    if(c.IsS5BChannel) actual--;
                    Check(actual==expected,$"mask {mask} region {machine} {c.Name} pattern {pattern}: {actual} = {expected}");
                }
            }
            NotSoFatso.NsfClose(handle);
            GC.KeepAlive(chipWrites);
        }
        ExtendedRegression.Run(output, Check);
        FinalRegression.Run(output, Check);
        Console.WriteLine($"All {checks} checks passed.");
        Init.ShutdownBaseSystems();
    }
}
