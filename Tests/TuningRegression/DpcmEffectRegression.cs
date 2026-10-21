using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FamiStudio;

static class DpcmEffectRegression
{
    sealed class Player : BasePlayer
    {
        readonly List<(int Address,int Value)> pending = new();
        public readonly List<(int Address,int Value)[]> Frames = new();
        public readonly List<short> Audio = new();
        public Player(bool pal) : base(NesApu.APU_WAV_EXPORT,pal,false) { Loop=LoopMode.Song;maxLoopCount=2; }
        public override void NotifyRegisterWrite(int apu,int address,int value,int metadata=0) { if(address>=0x4010 && address<=0x4015)pending.Add((address,value)); }
        protected override short[] EndFrame() { var data=base.EndFrame();Audio.AddRange(data);Frames.Add(pending.ToArray());pending.Clear();return data; }
        public void Render(Song song) { try { BeginPlaySong(song);pending.Clear();while(PlaySongFrame()){} } finally { Shutdown(); } }
    }
    static int checks;
    static void Check(bool ok,string label) { if(!ok)throw new Exception(label);checks++;Console.WriteLine("PASS DPCM mapping: "+label); }
    static void Throws<T>(Action action,string label) where T:Exception
    {
        bool threw=false;try {action();}catch(T){threw=true;}Check(threw,label);
    }
    static byte[] LegacyMappingBytes(DPCMSampleMapping mapping,bool fullDac)
    {
        // Frozen 1004/1005 wire layouts: later versions append PCM pitch, so
        // the final byte of a current mapping is no longer its DAC low bit.
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write(mapping.Sample.Id);writer.Write(mapping.Loop);writer.Write(mapping.Pitch);
        writer.Write(mapping.OverrideDmcInitialValue);writer.Write(mapping.DmcInitialValueDiv2);
        if(fullDac)writer.Write((byte)(mapping.DmcInitialValue&1));
        return stream.ToArray();
    }
    static Project Fixture(bool pal=false)
    {
        var p=new Project{PalMode=pal};
        var sample=p.CreateDPCMSampleFromDmcData("Original synthetic bits",Enumerable.Range(0,65).Select(i=>(byte)(i*37+19)).ToArray());
        sample.PaddingMode=DPCMPaddingType.Unpadded;sample.DmcInitialValueDiv2=32;sample.Process();
        var i=p.CreateInstrument(0,"Complete DAC mapping");
        foreach(var pair in new[]{(1,0),(2,68),(3,127)}) {
            var m=i.MapDPCMSample(pair.Item1,sample,15,pair.Item1==3);
            m.OverrideDmcInitialValue=true;m.DmcInitialValue=pair.Item2;
        }
        var song=p.CreateSong("Repeated DAC attacks");song.ChangeFamiStudioTempoGroove(new[]{1},false);
        song.SetLength(1);song.SetDefaultPatternLength(32);song.SetBeatLength(4);song.SetLoopPoint(0);
        var pattern=song.Channels[4].GetOrCreatePattern(0);
        pattern.Notes[0]=new Note(2){Instrument=i,Duration=4,DeltaCounter=68};
        pattern.Notes[4]=new Note(2){Instrument=i,Duration=4,DeltaCounter=68};
        pattern.Notes[8]=new Note(3){Instrument=i,Duration=4,DeltaCounter=127};
        pattern.Notes[12]=new Note(2){Instrument=i,Duration=4,DeltaCounter=68,HasAttack=false};
        pattern.Notes[16]=new Note(1){Instrument=i,Duration=4,DeltaCounter=0};
        pattern.Notes[20]=new Note{DeltaCounter=68};
        pattern.Notes[24]=new Note(2){Instrument=i,Duration=4,DeltaCounter=66};
        pattern.Notes[28]=new Note(Note.NoteStop){DeltaCounter=0};
        return p;
    }
    static Player Render(Project p,bool pal) { var player=new Player(pal);player.Render(p.Songs[0]);return player; }
    static int[] Controls(IEnumerable<(int Address,int Value)> writes,ref int previous)
    {
        var result=new List<int>();
        foreach(var write in writes.Where(w=>w.Address==0x4015)) {
            int value=write.Value&31;if(value==previous && (value&16)==0)continue;result.Add(value);previous=value;
        }
        return result.ToArray();
    }
    static void VerifyNsf(Project project,Player editor,bool pal,string path)
    {
        Check(new NsfFile().Save(project,FamiToneKernel.FamiStudio,path,new[]{project.Songs[0].Id},"DAC mapping","Test","",pal?MachineType.PAL:MachineType.NTSC,false),"NSF saves "+Path.GetFileName(path));
        var h=NotSoFatso.NsfOpen(path);Check(h!=IntPtr.Zero,"NSF opens "+Path.GetFileName(path));
        var pending=new List<(int Address,int Value)>();var errors=new List<string>();
        NotSoFatso.WriteRegisterDelegate callback=(a,v)=>{if(a>=0x4010 && a<=0x4015)pending.Add((a,v));};
        int previousEditor=15,previousEngine=15;
        try {
            NotSoFatso.NsfSetApuWriteCallback(h,callback);NotSoFatso.NsfSetTrack(h,0);NotSoFatso.NsfRunFrame(h);pending.Clear();
            for(int f=0;f<editor.Frames.Count;f++) {
                NotSoFatso.NsfRunFrame(h);var expected=editor.Frames[f];
                foreach(int address in new[]{0x4010,0x4011,0x4013}) {
                    var a=pending.Where(w=>w.Address==address).Select(w=>w.Value);var b=expected.Where(w=>w.Address==address).Select(w=>w.Value);
                    if(!a.SequenceEqual(b))errors.Add($"frame {f} register {address:X4}: [{string.Join(',',a)}] != [{string.Join(',',b)}]");
                }
                if(pending.Count(w=>w.Address==0x4012)!=expected.Count(w=>w.Address==0x4012))errors.Add($"frame {f}: sample trigger count");
                if(!Controls(pending,ref previousEngine).SequenceEqual(Controls(expected,ref previousEditor)))errors.Add($"frame {f}: effective stop/retrigger 4015 sequence");
                pending.Clear();
            }
        } finally {NotSoFatso.NsfClose(h);GC.KeepAlive(callback);}
        File.WriteAllLines(path+".comparison.txt",errors.Count==0?new[]{$"PASS {editor.Frames.Count} frames"}:errors);
        Check(errors.Count==0,"NSF CPU preserves DAC, rate/loop, sample length and stop/retrigger sequence "+Path.GetFileName(path));
    }
    static void LegacyBoundaries(string output)
    {
        var p=Fixture();var song=p.Songs[0];var channel=song.Channels[4];var pattern=channel.PatternInstances[0];
        bool Save(string name)
        {
            string path=Path.Combine(output,"legacy-"+name+".asm");
            return new FamitoneMusicFile(FamiToneKernel.FamiTone2,false).Save(p,new[]{song.Id},AssemblyFormat.ASM6,0x4000,false,path,Path.ChangeExtension(path,".dmc"),DpcmExportMode.All,false,null,MachineType.NTSC);
        }
        pattern.Notes[8].HasDeltaCounter=false;
        Check(song.UsesOddDpcmMappingDac() && song.UsesExtendedPlayback(true),"played odd mapping is recognized at an ordinary attack");
        Check(!Save("used-odd"),"FamiTone2 rejects an actually used odd mapping DAC");
        Check(!new FamitrackerTextFile().Save(p,Path.Combine(output,"unsupported-ft.txt"),new[]{song.Id}),"FamiTracker text rejects odd mapping loss");
        Check(!new MidiFileWriter().Save(p,Path.Combine(output,"unsupported.mid"),song.Id,MidiExportInstrumentMode.Channel,new int[song.Channels.Length],true,false,2),"MIDI rejects odd mapping loss");
        pattern.Notes[8].DeltaCounter=126;
        Check(!song.UsesOddDpcmMappingDac() && Save("explicit-dac"),"explicit note DAC overrides mapping and retains legacy export behavior");
        pattern.Notes[8].HasDeltaCounter=false;pattern.Notes[8].HasAttack=false;
        Check(!song.UsesOddDpcmMappingDac() && Save("no-attack"),"no-attack note does not apply the mapping DAC");
        pattern.Notes[8].HasAttack=true;pattern.Notes[8].Value=2;
        Check(!song.UsesOddDpcmMappingDac() && Save("unused-mapping"),"unused odd key in a used instrument does not block export");
        var unused=channel.CreatePattern("Unscheduled odd key");unused.Notes[0]=new Note(3){Instrument=p.Instruments[0],Duration=4};
        pattern.Notes[40]=new Note(3){Instrument=p.Instruments[0],Duration=4};
        Check(!song.UsesOddDpcmMappingDac() && Save("unused-positions"),"unscheduled patterns and out-of-span notes do not block export");
        var other=p.CreateSong("Other odd song");other.SetLength(1);other.SetDefaultPatternLength(8);other.ChangeFamiStudioTempoGroove(new[]{1},false);
        other.Channels[4].GetOrCreatePattern(0).Notes[0]=new Note(3){Instrument=p.Instruments[0],Duration=4};
        Check(other.UsesOddDpcmMappingDac() && Save("selected-song"),"unselected song does not block the compatible selection");
        pattern.Notes.Clear();song.Channels[0].GetOrCreatePattern(0).Notes[0]=new Note(49){Instrument=p.Instruments[0],Duration=32};
        Check(!song.UsesOddDpcmMappingDac() && Save("non-dpcm"),"regular voice using an instrument with odd DPCM mappings remains compatible");
        // Export serialization removes unscheduled patterns from the original
        // resource table. Create this reachable variant after those exports.
        var repeated=channel.CreatePattern("Repeated odd key");
        repeated.Notes[0]=new Note(3){Instrument=p.Instruments[0],Duration=4};
        pattern.LoopVariant=repeated;Check(song.UsesOddDpcmMappingDac(),"reachable repeated pattern includes its odd mapping");
        song.SetLoopPoint(-1);Check(!song.UsesOddDpcmMappingDac(),"unreachable variation in a nonlooping song does not count");
    }
    public static void Run(string output)
    {
        checks=0;Directory.CreateDirectory(output);
        Check(Project.Version>=1005 && Project.DpcmMappingDacFormatVersion==1005,"format 1005 gates full mapping DAC values");
        string oldPath=Path.Combine("Tests","TuningRegression","Fixtures","format1004-dpcm.fms");
        Check(BitConverter.ToInt32(File.ReadAllBytes(oldPath),4)==1004,"fixture was written by the actual format 1004 application");
        var old=new ProjectFile().Load(oldPath);
        Check(old!=null && Enumerable.Range(1,3).Select(n=>old.Instruments[0].GetDPCMMapping(n).DmcInitialValue).SequenceEqual(new[]{0,68,126}),"old binary half-values load as exact even DACs");
        var p=Fixture();var instrument=p.Instruments[0];var mapping=instrument.GetDPCMMapping(3);
        Check(mapping.DmcInitialValue==127 && mapping.DmcInitialValueDiv2==63,"raw 127 preserves its low bit while exposing the legacy half");
        mapping.DmcInitialValueDiv2=63;Check(mapping.DmcInitialValue==126,"legacy setter intentionally restores an even value");mapping.DmcInitialValue=127;
        Throws<ArgumentOutOfRangeException>(()=>mapping.DmcInitialValue=-1,"raw negative value rejected");
        Throws<ArgumentOutOfRangeException>(()=>mapping.DmcInitialValue=128,"raw value above seven bits rejected");
        Throws<ArgumentOutOfRangeException>(()=>mapping.DmcInitialValueDiv2=64,"legacy half value above 63 rejected");
        Check(p.Samples[0].DmcInitialValueDiv2==32,"source sample's half-value processing remains unchanged");
        var other=new DPCMSampleMapping{Sample=mapping.Sample,Pitch=mapping.Pitch,Loop=mapping.Loop,OverrideDmcInitialValue=true,DmcInitialValue=126};
        Check(!mapping.Equals(other) && mapping.GetHashCode()!=other.GetHashCode(),"mapping equality and hash distinguish adjacent odd/even DACs");
        uint crc=instrument.ComputeCRC();mapping.DmcInitialValue=126;Check(crc!=instrument.ComputeCRC(),"instrument CRC includes mapping low bit");mapping.DmcInitialValue=127;
        var bytes=new ProjectSaveBuffer(p);mapping.Serialize(bytes);
        var legacy1004=LegacyMappingBytes(mapping,false);var legacy1005=LegacyMappingBytes(mapping,true);
        Check(bytes.GetBuffer().Take(legacy1005.Length).SequenceEqual(legacy1005),"current mapping retains the frozen 1005 wire prefix");
        mapping.DmcInitialValue=1;mapping.Serialize(new ProjectLoadBuffer(p,bytes.GetBuffer(),Project.Version));
        Check(mapping.DmcInitialValue==127,"direct serialization preserves low bit");
        mapping.DmcInitialValue=1;mapping.Serialize(new ProjectLoadBuffer(p,legacy1005,1005));
        Check(mapping.DmcInitialValue==127,"frozen 1005 mapping preserves low bit");
        mapping.Serialize(new ProjectLoadBuffer(p,legacy1004,1004));
        Check(mapping.DmcInitialValue==126,"reading old data into an existing odd mapping clears stale low bit");mapping.DmcInitialValue=127;
        foreach(int version in new[]{1005,Project.Version})foreach(byte lowBit in new byte[]{2,255}) {
            var invalid=version==1005?(byte[])legacy1005.Clone():bytes.GetBuffer();invalid[legacy1004.Length]=lowBit;
            Throws<InvalidDataException>(()=>other.Serialize(new ProjectLoadBuffer(p,invalid,version)),"malformed binary low bit "+lowBit+" rejected in format "+version);
        }
        foreach(var flags in new[]{ProjectBufferFlags.UndoRedo,ProjectBufferFlags.Clipboard}) {
            var buffer=new ProjectSaveBuffer(p,flags);mapping.Serialize(buffer);mapping.DmcInitialValue=0;
            mapping.Serialize(new ProjectLoadBuffer(p,buffer.GetBuffer(),Project.Version,flags));
            Check(mapping.DmcInitialValue==127,flags+" mapping serialization preserves full DAC");
        }
        string binary=Path.Combine(output,"full-dac.fms"),text=Path.Combine(output,"full-dac.txt");
        Check(new ProjectFile().Save(p,binary),"binary project saves");
        Check(new FamistudioTextFile().Save(p,text,new[]{p.Songs[0].Id},false),"text project saves");
        foreach(var restored in new[]{new ProjectFile().Load(binary),new FamistudioTextFile().Load(text),p.DeepClone()})
            Check(restored!=null && Enumerable.Range(1,3).Select(n=>restored.Instruments[0].GetDPCMMapping(n).DmcInitialValue).SequenceEqual(new[]{0,68,127}),"binary/text/clone preserve full mapping values");
        string contents=File.ReadAllText(text);Check(contents.Contains("DmcInitialValueRaw=\"127\""),"text explicitly marks raw units");
        string legacyText=Path.Combine(output,"legacy-half.txt");File.WriteAllText(legacyText,contents.Replace("DmcInitialValueRaw=\"68\"","DmcInitialValue=\"34\""));
        Check(new FamistudioTextFile().Load(legacyText).Instruments[0].GetDPCMMapping(2).DmcInitialValue==68,"legacy text half-value remains readable");
        foreach(string value in new[]{"-1","128"}) {
            string bad=Path.Combine(output,"invalid-"+value+".txt");File.WriteAllText(bad,contents.Replace("DmcInitialValueRaw=\"127\"","DmcInitialValueRaw=\""+value+"\""));
            Check(new FamistudioTextFile().Load(bad)==null,"text rejects invalid raw value "+value);
        }
        foreach(bool pal in new[]{false,true}) {
            p=Fixture(pal);var before=Render(p,pal);var optimized=p.DeepClone();var pattern=optimized.Songs[0].Channels[4].PatternInstances[0];
            int removed=pattern.Notes.Values.Count(GimmickConversion.RemoveRedundantDpcmDelta);
            Check(removed==4,"remove exactly the mapping-equivalent even, zero and odd attack effects, PAL "+pal);
            Check(new[]{12,20,24,28}.All(row=>pattern.Notes[row].HasDeltaCounter),"preserve no-attack, standalone, differing and stop DAC effects, PAL "+pal);
            var after=Render(optimized,pal);
            Check(before.Frames.Count==after.Frames.Count && before.Frames.Zip(after.Frames).All(pair=>pair.First.SequenceEqual(pair.Second)),"actual editor register writes unchanged through repeated loops, PAL "+pal);
            Check(before.Audio.SequenceEqual(after.Audio),"actual PCM unchanged after removing redundant effects, PAL "+pal);
            VerifyNsf(p,before,pal,Path.Combine(output,$"original-{pal}.nsf"));
            VerifyNsf(optimized,after,pal,Path.Combine(output,$"optimized-{pal}.nsf"));
        }
        LegacyBoundaries(output);
        Console.WriteLine($"All {checks} DPCM mapping/effect checks passed.");
    }
}
