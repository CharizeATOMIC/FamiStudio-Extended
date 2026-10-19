using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FamiStudio;

static class PulseSweepRegression
{
    sealed class Version1005Writer : ProjectSaveBuffer, ProjectBuffer
    {
        public Version1005Writer(Project project) : base(project) { }
        int ProjectBuffer.Version => 1005;
    }
    sealed class Capture : BasePlayer
    {
        readonly List<(int Register,int Value)> pending = new();
        readonly int reload;
        public readonly List<(int Register,int Value)[]> Writes = new();
        public readonly List<int[]> Timers = new();
        public readonly List<short> Audio = new();
        public Capture(bool pal,int repeatSweep=-1) : base(NesApu.APU_WAV_EXPORT,pal,false)
        { Loop=LoopMode.None;reload=repeatSweep; }
        public override void NotifyRegisterWrite(int apu,int register,int value,int metadata=0)
        { pending.Add((register,value)); }
        protected override short[] EndFrame()
        {
            if(reload>=0) foreach(int ch in new[]{0,1})NesApu.WriteRegister(NesApu.APU_WAV_EXPORT,0x4001+ch*4,reload);
            var samples=base.EndFrame();Audio.AddRange(samples);
            var registers=new NesApu.NesRegisterValues();registers.ReadRegisterValues(NesApu.APU_WAV_EXPORT);
            Timers.Add(new[]{registers.GetMergedRegisterValue(0,0x4002,0x4003,7),registers.GetMergedRegisterValue(0,0x4006,0x4007,7)});
            Writes.Add(pending.ToArray());pending.Clear();return samples;
        }
        public void Render(Song song) { try { BeginPlaySong(song);while(PlaySongFrame()){} } finally { Shutdown(); } }
        public int Count(int frame,int channel,int offset) => Writes[frame].Count(w=>w.Register==0x4000+channel*4+offset);
    }
    static int checks;
    internal sealed class NativeReplay : IDisposable
    {
        readonly int apu;
        readonly NesApu.DmcReadDelegate callback = (_,__)=>0;
        public NativeReplay(bool pal)
        {
            apu=NesApu.Create();
            if(NesApu.Init(apu,44100,24,pal?1:0,0,0,callback)!=0)throw new Exception("Native replay initialization failed.");
            NesApu.Reset(apu);
            foreach(var write in new[]{(0x4015,3),(0x4000,0x30),(0x4004,0x30),(0x4001,8),(0x4005,8)})NesApu.WriteRegister(apu,write.Item1,write.Item2);
        }
        public int[] Step(IEnumerable<(int Register,int Value)> writes)
        {
            foreach(var write in writes)if(write.Register>=0x4000&&write.Register<=0x4007||write.Register==0x4017)NesApu.WriteRegister(apu,write.Register,write.Value);
            NesApu.EndFrame(apu);var regs=new NesApu.NesRegisterValues();regs.ReadRegisterValues(apu);
            NesApu.RemoveSamples(apu,NesApu.SamplesAvailable(apu));
            return new[]{regs.GetMergedRegisterValue(0,0x4002,0x4003,7),regs.GetMergedRegisterValue(0,0x4006,0x4007,7)};
        }
        public void Dispose(){NesApu.Destroy(apu);GC.KeepAlive(callback);}
    }
    // Unlike frame-boundary replay, this reference preserves the real CPU
    // timestamp of every register write, including the NSF initialization call.
    internal sealed class TimedNativeReplay : IDisposable
    {
        readonly int apu;
        readonly NesApu.DmcReadDelegate callback = (_,__)=>0;
        long sourceStart, physicalStart, current;
        int physicalLength;
        public TimedNativeReplay(bool pal)
        {
            apu=NesApu.Create();
            if(NesApu.Init(apu,44100,24,pal?1:0,0,0,callback)!=0)throw new Exception("Timed replay initialization failed.");
            NesApu.Reset(apu); physicalLength=(pal?33247:29780)^1;
            NesApu.WriteRegister(apu,0x4015,15); current=4;
        }
        void Advance(long target)
        {
            while(target>physicalStart+physicalLength)
            {
                NesApu.EndFrame(apu);NesApu.RemoveSamples(apu,NesApu.SamplesAvailable(apu));
                physicalStart+=physicalLength;physicalLength^=1;current=physicalStart;
            }
            if(target<current)throw new Exception("CPU timestamps are not monotonic.");
            if(target>current)NesApu.SkipCycles(apu,(int)(target-current));current=target;
        }
        public int[] Step(IEnumerable<(int Register,int Value,int Cycle)> writes,int cycles)
        {
            foreach(var w in writes)
            {
                if(!(w.Register>=0x4000&&w.Register<=0x4007||w.Register==0x4015||w.Register==0x4017))continue;
                long target=sourceStart+w.Cycle;
                Advance(target-4);NesApu.WriteRegister(apu,w.Register,w.Value);current=target;
            }
            sourceStart+=cycles;Advance(sourceStart);
            var regs=new NesApu.NesRegisterValues();regs.ReadRegisterValues(apu);
            return new[]{regs.GetMergedRegisterValue(0,0x4002,0x4003,7),regs.GetMergedRegisterValue(0,0x4006,0x4007,7)};
        }
        public void Dispose(){NesApu.Destroy(apu);GC.KeepAlive(callback);}
    }
    static void Check(bool condition,string label)
    { if(!condition)throw new Exception(label);checks++;Console.WriteLine("PASS pulse sweep: "+label); }
    static Project Make(bool pal,byte sweep,int length=100,int note=46)
    {
        var project=new Project { PalMode=pal };var instrument=project.CreateInstrument(0,"Sweep");instrument.PulseSweepRegister=sweep;
        var song=project.CreateSong("Hardware sweep");song.ChangeFamiStudioTempoGroove(new[]{1},false);
        song.SetLength(1);song.SetLoopPoint(-1);song.SetDefaultPatternLength(length);song.SetBeatLength(4);
        foreach(int ch in new[]{0,1})song.Channels[ch].GetOrCreatePattern(0).Notes[0]=new Note(note){Instrument=instrument,Duration=length};
        return project;
    }
    static Capture Render(Project project,int reload=-1)
    { var capture=new Capture(project.PalMode,reload);capture.Render(project.Songs[0]);return capture; }
    static void Storage(string output)
    {
        var project=Make(false,8);var instrument=project.Instruments[0];var song=project.Songs[0];var crcs=new HashSet<uint>();
        Check(Project.PulseSweepFormatVersion==1006,"format 1006 identifies sweep control");
        foreach(byte control in new byte[]{0,8,0x80,0x86,0x8e,0xf6,0xfe,0xff})
        {
            instrument.PulseSweepRegister=control;crcs.Add(instrument.ComputeCRC());
            var path=Path.Combine(output,$"sweep-{control:x2}.fms");var text=Path.ChangeExtension(path,".txt");
            Check(new ProjectFile().Save(project,path),"binary save "+control);
            Check(new FamistudioTextFile().Save(project,text,new[]{song.Id},false),"text save "+control);
            foreach(var restored in new[]{new ProjectFile().Load(path),new FamistudioTextFile().Load(text),project.DeepClone()})
                Check(restored.Instruments[0].PulseSweepRegister==control,"binary/text/clone preserve all sweep bits "+control);
            var buffer=new ProjectSaveBuffer(project,ProjectBufferFlags.UndoRedo);instrument.Serialize(buffer);instrument.PulseSweepRegister=8;
            instrument.Serialize(new ProjectLoadBuffer(project,buffer.GetBuffer(),Project.Version,ProjectBufferFlags.UndoRedo));
            Check(instrument.PulseSweepRegister==control,"undo preserves sweep "+control);
        }
        Check(crcs.Count==8,"instrument deduplication distinguishes sweep behavior");
        // Write the real older layout. Trimming one trailing field cannot
        // remove newer clock bytes embedded in every serialized envelope.
        var oldBuffer=new Version1005Writer(project);instrument.Serialize(oldBuffer);
        instrument.Serialize(new ProjectLoadBuffer(project,oldBuffer.GetBuffer(),1005));
        Check(instrument.PulseSweepRegister==8,"format 1005 instrument loads with legacy sweep disabled");
        var oldProject=new ProjectFile().Load(Path.Combine("Tests","TuningRegression","Fixtures","format1004-dpcm.fms"));
        Check(oldProject.Instruments.All(i=>i.PulseSweepRegister==8),"existing full project loads without enabling hardware sweep");
        instrument.PulseSweepRegister=0x86;
        Check(song.UsesExtendedPlayback(true) && !song.UsesExtendedPlayback(false),"legacy notation exports detect sweep while register exports remain supported");
        string ft=Path.Combine(output,"unsupported-ft.txt"),midi=Path.Combine(output,"unsupported.mid");
        if(File.Exists(ft))File.Delete(ft);if(File.Exists(midi))File.Delete(midi);
        Check(!new FamitrackerTextFile().Save(project,ft,new[]{song.Id}) && !File.Exists(ft),"FamiTracker rejects sweep without producing a misleading file");
        Check(!new MidiFileWriter().Save(project,midi,song.Id,MidiExportInstrumentMode.Channel,new int[song.Channels.Length],true,false,2) && !File.Exists(midi),"MIDI rejects sweep without producing a misleading file");
        string vgm=Path.Combine(output,"sweep.vgm");
        Check(VgmFile.Save(song,vgm,"Sweep","Test","NES","","","","",false),"VGM supports captured hardware sweep writes");
        var bytes=File.ReadAllBytes(vgm);int offset=0x34+BitConverter.ToInt32(bytes,0x34);var vgmWrites=new List<(int Register,int Value)>();
        while(offset<bytes.Length)
        {
            int command=bytes[offset++];if(command==0x66)break;
            if(command==0xb4){vgmWrites.Add((0x4000+bytes[offset],bytes[offset+1]));offset+=2;}
            else if(command==0x61)offset+=2;
            else if(command==0x62||command==0x63||command>=0x70&&command<=0x7f){}
            else if(command==0x67){int size=BitConverter.ToInt32(bytes,offset+2);offset+=6+size;}
            else throw new Exception($"Unexpected VGM command {command:x2}");
        }
        foreach(int channel in new[]{0,1})
        {
            Check(vgmWrites.Any(w=>w.Register==0x4001+channel*4&&w.Value==0x86),"VGM contains the hardware sweep control");
            Check(vgmWrites.Count(w=>w.Register==0x4002+channel*4)<=2,"VGM preserves held-sweep timer suppression");
        }
    }
    static void Hardware(bool pal)
    {
        foreach(byte control in new byte[]{0x86,0x8e,0xf6,0xfe})
        {
            var project=Make(pal,control);var capture=Render(project);
            foreach(int channel in new[]{0,1})
            {
                Check(Enumerable.Range(1,99).All(f=>capture.Count(f,channel,2)==0 && capture.Count(f,channel,3)==0),$"held sweep does not overwrite hardware timer: PAL={pal}, control={control:x2}, channel={channel}");
                Check(Enumerable.Range(1,99).All(f=>capture.Count(f,channel,1)==0),"held sweep does not continually reload its divider");
                int first=capture.Timers[0][channel],last=capture.Timers[^1][channel];
                Check((control&8)!=0?last<first:last>first,"native APU independently changes timer in the requested direction");
                Check(capture.Timers.Select(t=>t[channel]).Distinct().Count()>8,"native timer evolves over many sweep steps");
            }
            Check(capture.Audio.Any(v=>v!=0),"sweep renders audible native PCM");
        }
        var fast=Render(Make(pal,0x86));var slow=Render(Make(pal,0xf6));
        Check(slow.Timers[30][0]<fast.Timers[30][0],"period seven runs more slowly than period zero");
        var identity=Make(pal,0x86,50,63);var once=Render(identity);var every=Render(identity,0x86);
        Check(once.Timers.Zip(every.Timers).All(pair=>pair.First.SequenceEqual(pair.Second)),"Gimmick $86 once-per-attack produces the same native timers as its every-frame reload");
    }
    static void Articulation(bool pal,bool smooth)
    {
        Settings.SquareSmoothVibrato=smooth;
        var project=Make(pal,0x86,100);var song=project.Songs[0];var sweep=project.Instruments[0];var plain=project.CreateInstrument(0,"Normal");
        foreach(int channel in new[]{0,1})
        {
            var pattern=song.Channels[channel].PatternInstances[0];
            pattern.Notes[12]=new Note(46){Instrument=sweep,Duration=88,HasAttack=false};
            pattern.Notes[20]=new Note{Volume=7};
            pattern.Notes[40]=new Note{FinePitch=1};
            pattern.Notes[56]=new Note(46){Instrument=sweep,Duration=44,FinePitch=0};
            pattern.Notes[64]=new Note(46){Instrument=plain,Duration=36,HasAttack=false};
            pattern.Notes[72]=new Note(Note.NoteStop);
            pattern.Notes[80]=new Note(46){Instrument=sweep,Duration=20};
        }
        var capture=Render(project);
        foreach(int channel in new[]{0,1})
        {
            Check(capture.Count(12,channel,2)==0&&capture.Count(12,channel,3)==0&&capture.Count(12,channel,1)==0,$"legato preserves hardware sweep: PAL={pal}, smooth={smooth}, channel={channel}");
            Check(capture.Count(20,channel,2)==0&&capture.Count(20,channel,3)==0,"volume-only effects do not retrigger sweep");
            Check(capture.Timers[39][channel]>511,"fixture has swept the hardware high byte across a boundary");
            Check(capture.Count(40,channel,2)==1&&capture.Count(40,channel,3)==1&&capture.Timers[40][channel]<300,"software low-byte change restores both hardware bytes after a sweep");
            foreach(int frame in new[]{56,80})Check(capture.Count(frame,channel,2)==1&&capture.Count(frame,channel,3)==1&&capture.Count(frame,channel,1)==1,"new attack restores base timer and restarts sweep exactly once");
            Check(capture.Count(64,channel,2)==1&&capture.Count(64,channel,3)==1&&capture.Writes[64].Any(w=>w.Register==0x4001+channel*4&&w.Value==8),"leaving sweep disables hardware modulation and restores full normal timer");
            Check(capture.Count(72,channel,2)==0&&capture.Count(72,channel,3)==0,"stop silences without a phase retrigger");
            Check(!capture.Writes.SelectMany(w=>w).Any(w=>w.Register==0x4017),"hardware sweep never invokes the smooth-vibrato frame-counter trick");
        }
    }
    static void OtherPulse(bool pal,int sweepChannel)
    {
        Settings.SquareSmoothVibrato=true;
        var project=Make(pal,0x86,40);var song=project.Songs[0];int plainChannel=1-sweepChannel;
        var plain=project.CreateInstrument(0,"Crossing pitch");var table=NesApu.GetNoteTableForChannelType(ChannelType.Square1,pal,1);
        var pattern=song.Channels[plainChannel].PatternInstances[0];pattern.Notes.Clear();
        foreach(int frame in new[]{0,10,20,30})
        {
            int period=(frame/10)%2==0?255:256;
            pattern.Notes[frame]=new Note(46){Instrument=plain,Duration=10,FinePitch=(sbyte)(period-table[46])};
        }
        var capture=Render(project);
        Check(!capture.Writes.SelectMany(w=>w).Any(w=>w.Register==0x4017),$"other pulse never resets shared frame counter during a sweep: PAL={pal}, sweep channel={sweepChannel}");
        Check(capture.Timers[30][sweepChannel]>capture.Timers[10][sweepChannel],"neighbor high-byte crossings leave hardware sweep progressing");
    }
    public static void Run(string output)
    {
        checks=0;Directory.CreateDirectory(output);bool oldSmooth=Settings.SquareSmoothVibrato;
        try
        {
            Storage(output);
            foreach(bool pal in new[]{false,true}) { Hardware(pal);foreach(bool smooth in new[]{false,true})Articulation(pal,smooth);OtherPulse(pal,0);OtherPulse(pal,1); }
        }
        finally { Settings.SquareSmoothVibrato=oldSmooth; }
        File.WriteAllText(Path.Combine(output,"result.txt"),$"PASS all {checks} pulse-sweep checks\n");
        Console.WriteLine($"PASS all {checks} pulse-sweep checks");
    }
    public static void RunNsf(string output)
    {
        checks=0;Directory.CreateDirectory(output);bool oldSmooth=Settings.SquareSmoothVibrato;var results=new List<object>();int allErrors=0;
        try
        {
            Settings.SquareSmoothVibrato=true;
            foreach(bool pal in new[]{false,true})foreach(byte sweep in new byte[]{0x86,0x8e,0xf6,0xfe})
            {
                var project=Make(pal,sweep,100);var song=project.Songs[0];var instrument=project.Instruments[0];var plain=project.CreateInstrument(0,"Normal");
                foreach(int channel in new[]{0,1})
                {
                    var pattern=song.Channels[channel].PatternInstances[0];
                    pattern.Notes[12]=new Note(46){Instrument=instrument,Duration=88,HasAttack=false};
                    pattern.Notes[20]=new Note{Volume=7};pattern.Notes[40]=new Note{FinePitch=1};
                    pattern.Notes[56]=new Note(46){Instrument=instrument,Duration=44,FinePitch=0};
                    pattern.Notes[64]=new Note(46){Instrument=plain,Duration=36,HasAttack=false};
                    pattern.Notes[72]=new Note(Note.NoteStop);
                    pattern.Notes[80]=new Note(46){Instrument=instrument,Duration=20};
                }
                var editor=Render(project);string path=Path.Combine(output,$"sweep-{sweep:x2}-{(pal?"pal":"ntsc")}.nsf");
                Check(new NsfFile().Save(project,FamiToneKernel.FamiStudio,path,new[]{song.Id},"Sweep CPU regression","Test","",pal?MachineType.PAL:MachineType.NTSC,false),"NSF export "+Path.GetFileName(path));
                var handle=NotSoFatso.NsfOpen(path);Check(handle!=IntPtr.Zero,"NSF opens "+Path.GetFileName(path));
                var pending=new List<(int Register,int Value)>();var registers=new Dictionary<int,int>();var errors=new List<string>();
                int replayChecks=0,coreChecks=0,writeChecks=0,editorPhaseDifferences=0;int[] expectedSweep={8,8};
                var timedWrites=new List<(int Register,int Value,int Cycle)>();
                NotSoFatso.WriteRegisterDelegate callback=(r,v)=>{pending.Add((r,v));registers[r]=v;timedWrites.Add((r,v,NotSoFatso.NsfGetState(handle,-1,NotSoFatso.STATE_CPUCYCLE,0)));};
                using var replay=new NativeReplay(pal);
                using var timedReplay=new TimedNativeReplay(pal);
                try
                {
                    NotSoFatso.NsfSetTrack(handle,0);NotSoFatso.NsfSetApuWriteCallback(handle,callback);NotSoFatso.NsfRunFrame(handle);
                    timedReplay.Step(timedWrites,NotSoFatso.NsfGetState(handle,-1,NotSoFatso.STATE_FRAMECYCLES,0));
                    pending.Clear();timedWrites.Clear();
                    for(int frame=0;frame<editor.Timers.Count;frame++)
                    {
                        NotSoFatso.NsfRunFrame(handle);var hardware=replay.Step(pending);
                        var cpuTimedHardware=timedReplay.Step(timedWrites,NotSoFatso.NsfGetState(handle,-1,NotSoFatso.STATE_FRAMECYCLES,0));
                        foreach(var write in editor.Writes[frame])if(write.Register==0x4001||write.Register==0x4005)expectedSweep[(write.Register-0x4001)/4]=write.Value;
                        for(int channel=0;channel<2;channel++)
                        {
                            int reg=0x4000+channel*4;int volume=editor.Writes[frame].LastOrDefault(w=>w.Register==reg).Value&15;
                            if(volume==0)continue;
                            replayChecks++;coreChecks++;
                            if(hardware[channel]!=editor.Timers[frame][channel])errors.Add($"native replay frame {frame} ch {channel}: {hardware[channel]} != {editor.Timers[frame][channel]}");
                            int actual=NotSoFatso.NsfGetState(handle,channel,NotSoFatso.STATE_PERIOD,0);
                            if(actual!=cpuTimedHardware[channel])errors.Add($"NSF emulator vs cycle-accurate replay frame {frame} ch {channel}: {actual} != {cpuTimedHardware[channel]}");
                            if(actual!=editor.Timers[frame][channel])editorPhaseDifferences++;
                            writeChecks++;
                            if(registers.GetValueOrDefault(reg+1)!=expectedSweep[channel])errors.Add($"sweep control frame {frame} ch {channel}");
                            foreach(int offset in new[]{2,3})
                            {
                                writeChecks++;
                                var expected=editor.Writes[frame].Where(w=>w.Register==reg+offset).Select(w=>w.Value&(offset==3?7:255));
                                var emitted=pending.Where(w=>w.Register==reg+offset).Select(w=>w.Value&(offset==3?7:255));
                                if(!expected.SequenceEqual(emitted))errors.Add($"timer write cadence frame {frame} ch {channel} offset {offset}: [{string.Join(',',emitted)}] != [{string.Join(',',expected)}]");
                            }
                        }
                        pending.Clear();timedWrites.Clear();
                    }
                }
                finally{NotSoFatso.NsfClose(handle);GC.KeepAlive(callback);}
                allErrors+=errors.Count;checks+=replayChecks+coreChecks+writeChecks;
                results.Add(new{pal,sweep,replayChecks,coreChecks,writeChecks,editorPhaseDifferences,errors=errors.Count,examples=errors.Take(20).ToArray()});
                File.WriteAllLines(path+".comparison.txt",errors.Count==0?new[]{"PASS actual NSF CPU writes, accurate native replay and NSF emulator timers"}:errors);
                Console.WriteLine($"{(errors.Count==0?"PASS":"FAIL")} pulse NSF {sweep:x2} PAL={pal}: {replayChecks+coreChecks+writeChecks} checks, {errors.Count} errors, {editorPhaseDifferences} CPU/frame-boundary phase differences");
                foreach(string error in errors.Take(3))Console.WriteLine(error);
            }
        }
        finally{Settings.SquareSmoothVibrato=oldSmooth;}
        File.WriteAllText(Path.Combine(output,"nsf-result.json"),JsonSerializer.Serialize(new{checks,errors=allErrors,cases=results},new JsonSerializerOptions{WriteIndented=true}));
        if(allErrors!=0)throw new Exception($"Pulse sweep NSF comparison failed: {allErrors} errors.");
        Console.WriteLine($"PASS all {checks} pulse-sweep NSF checks");
    }
}
