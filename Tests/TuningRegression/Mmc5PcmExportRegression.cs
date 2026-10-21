using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FamiStudio;

static class Mmc5PcmExportRegression
{
    static int checks;
    static void Check(bool condition,string message) { if(!condition)throw new Exception(message+"; "+Log.GetLastMessage(LogSeverity.Error));checks++;Console.WriteLine("PASS PCM export: "+message); }
    static Project Fixture(bool pal,int rate,int length,bool loop,bool dpcm,bool volume=false)
    {
        var p=new Project{PalMode=pal};p.SetExpansionAudioMask(ExpansionType.Mmc5Mask);p.Mmc5PcmEnabled=true;p.UpdateRateMultiplier=rate;
        var data=Enumerable.Range(0,length).Select(n=>(short)(volume?30000:((n*73+19)%251-125)*220)).ToArray();
        var sample=p.CreatePcmSampleFromWavData("Original synthetic PCM",data,(int)Math.Round(Mmc5PcmTiming.GetSampleRate(pal)));
        var instrument=p.CreateInstrument(ExpansionType.Mmc5,"PCM instrument");instrument.MapPcmSample(49,sample,0,loop);
        if(volume){var env=instrument.Envelopes[EnvelopeType.Volume];env.Length=4;for(int i=0;i<4;i++)env.Values[i]=(sbyte)new[]{15,7,0,15}[i];}
        var song=p.CreateSong("PCM IRQ synthetic");song.ChangeFamiStudioTempoGroove(new[]{1},false);song.SetDefaultPatternLength(length>4096?192:64);song.SetLength(length>4096?2:1);song.SetLoopPoint(-1);
        song.GetChannelByType(ChannelType.Mmc5Pcm).GetOrCreatePattern(0).Notes[0]=new Note(49){Instrument=instrument,Duration=length>4096?320:48};
        var pulse=p.CreateInstrument(ExpansionType.None,"Independent pulse");song.Channels[0].GetOrCreatePattern(0).Notes[0]=new Note(46){Instrument=pulse,Duration=length>4096?384:64};
        if(dpcm){var d=p.CreateDPCMSampleFromDmcData("Independent DPCM",Enumerable.Range(0,257).Select(i=>(byte)(i*29+51)).ToArray());var di=p.CreateInstrument(ExpansionType.None,"DPCM instrument");di.MapDPCMSample(49,d,15,true);song.Channels[4].GetOrCreatePattern(0).Notes[0]=new Note(49){Instrument=di,Duration=length>4096?312:40};}
        return p;
    }
    static object Verify(Project p,string path,bool nsfe,bool volume)
    {
        var song=p.Songs[0];var sample=p.GetInstrument("PCM instrument").GetPcmMapping(49);var expected=sample.Sample.GetPcmData(p.PalMode,sample.PcmPitch);
        var captured=Mmc5PcmExportData.Capture(p,new[]{song.Id},p.PalMode);
        File.WriteAllText(path+".events.json",JsonSerializer.Serialize(captured.Tracks[song.Id],new JsonSerializerOptions{IncludeFields=true,WriteIndented=true}));
        Check(new NsfFile().Save(p,FamiToneKernel.FamiStudio,path,new[]{song.Id},"PCM IRQ test","FamiStudio Extended tests","",p.PalMode?MachineType.PAL:MachineType.NTSC,nsfe),"save "+Path.GetFileName(path));
        if(!nsfe){var bytes=File.ReadAllBytes(path);Check(bytes[5]==2&&bytes[0x7c]==0x10,"NSF2 returning INIT/PLAY IRQ declaration");}
        var h=NotSoFatso.NsfOpen(path);Check(h!=IntPtr.Zero,"real NSF2 CPU opens");
        var writes=new List<(int Tick,int Cycle,int Value)>();int tick=-1;bool dpcmStart=false;int peak=0;
        NotSoFatso.WriteRegisterDelegate callback=(r,v)=>{if(r==0x5011)writes.Add((tick,NotSoFatso.NsfGetState(h,-1,NotSoFatso.STATE_CPUCYCLE,0),v));if(r==0x4015&&(v&16)!=0)dpcmStart=true;};
        try {
            NotSoFatso.NsfSetApuWriteCallback(h,callback);NotSoFatso.NsfSetTrack(h,0);
            int startup=0;while(NotSoFatso.NsfRunFrame(h)==0 && ++startup<32){}
            Check(startup<32,"returning INIT completes LUT setup");
            for(tick=0;tick<(expected.Length>4096?392:72);tick++)Check(NotSoFatso.NsfRunFrame(h)!=0,"PLAY progresses while PCM IRQ active "+tick);
            peak=NotSoFatso.NsfGetState(h,-1,1003,0);
            Check(peak>0,"CPU timing includes the completed PLAY routine and all interrupt/DMA time");
        } finally {NotSoFatso.NsfClose(h);GC.KeepAlive(callback);}
        var values=writes.Select(w=>w.Value).ToArray();int start=Array.FindIndex(values,v=>v!=128);Check(start>=0,"PCM produces non-centre 8-bit output");
        if(volume)
        {
            var runs=new List<int>();foreach(var v in values.Skip(start)){if(runs.Count==0||runs[^1]!=v)runs.Add(v);}
            var raw=expected[0];var v7=128+(raw-128)*7/15;
            Check(runs.SequenceEqual(new[]{(int)raw,v7,128,(int)raw,128}),"hardware volume follows independent envelope exactly");
        }
        else
        {
            int sourceStart=Array.FindIndex(expected,v=>v!=128);
            int activeEnd=captured.Tracks[song.Id].Events.First(e=>e.Command==2).Tick;
            int safeSampleCount=writes.Count(w=>w.Tick<activeEnd-4)-start;
            int compare=sample.Loop?Math.Min(expected.Length>4096?5000:2000,safeSampleCount):expected.Length-sourceStart;
            for(int i=0;i<compare;i++)if(values[start+i]!=expected[(sourceStart+i)%expected.Length])throw new Exception($"PCM sample order/value mismatch at {i}: {values[start+i]} != {expected[(sourceStart+i)%expected.Length]}");
            Check(compare>0,"exact PCM values/order including sample-loop or bank transition ("+compare+")");
            if(!sample.Loop)Check(values.Skip(start+compare).All(v=>v==128),"one-shot ends at centre without a stale held DAC");
        }
        if(song.Channels[4].Patterns.Any(pattern=>pattern.Notes.Values.Any(n=>n.IsMusical)))Check(dpcmStart,"DPCM trigger coexists with PCM IRQ");
        File.WriteAllText(path+".writes.json",JsonSerializer.Serialize(writes.Select(w=>new[]{w.Tick,w.Cycle,w.Value})));
        Check(writes.Where(w=>w.Tick>=(expected.Length>4096?324:52)).All(w=>w.Value==128),"note duration stops PCM");
        return new {file=path,pal=p.PalMode,rate=p.UpdateRateMultiplier,source_samples=expected.Length,pcm_writes=writes.Count,maximum_play_cycles=peak,passed=true};
    }
    sealed class CancelDuringValidation : ILogOutput
    {
        readonly string folder;
        public bool Requested;
        public CancelDuringValidation(string path){folder=path;}
        public bool AbortOperation => Requested |= Directory.EnumerateFiles(folder,".pcm-nsf-*.tmp").Any();
        public void LogMessage(string message){}
        public void ReportProgress(float progress){}
    }
    static List<string> MusicalStates(string path,int track,int frames)
    {
        var result=new List<string>();var state=new Dictionary<int,int>();int s5b=0;
        var changes=new SortedDictionary<int,List<(int Register,int Value)>>();
        var expansionStates=new Dictionary<int,string>();int previousEntry=0;
        var handle=NotSoFatso.NsfOpen(path);Check(handle!=IntPtr.Zero,"CPU opens loop/multi-chip comparison");
        NotSoFatso.WriteRegisterDelegate callback=(register,value)=>
        {
            if(register==0xc000)s5b=value&15;
            else if(register==0xe000)state[0x10000+s5b]=value;
            // $4012 is a relocated sample address, not an audible parameter:
            // enabling PCM changes bank packing while rate/length/DAC remain.
            else if(register!=0x4012&&(register>=0x4000&&register<=0x4013||register>=0x5000&&register<=0x5007))
            {
                var entry=NotSoFatso.NsfGetState(handle,-1,NotSoFatso.STATE_PLAYCALLS,0);
                if(entry!=previousEntry)
                {
                    // Expansion registers are written after the APU registers
                    // in this engine. At the first APU write of the next call
                    // they still hold the previous call's completed state.
                    expansionStates[previousEntry]=string.Join(",",new[]{ChannelType.Vrc6Square1,ChannelType.S5BSquare1}.SelectMany(c=>new[]{NotSoFatso.NsfGetState(handle,c,NotSoFatso.STATE_PERIOD,0),NotSoFatso.NsfGetState(handle,c,NotSoFatso.STATE_VOLUME,0)}));
                    previousEntry=entry;
                }
                if(!changes.TryGetValue(entry,out var list))changes.Add(entry,list=new());
                list.Add((register,value));
            }
        };
        try
        {
            NotSoFatso.NsfSetApuWriteCallback(handle,callback);NotSoFatso.NsfSetTrack(handle,track);
            int startup=0;while(NotSoFatso.NsfRunFrame(handle)==0&&++startup<32){}
            Check(startup<32,"loop/multi-chip INIT completes");
            for(int i=0;i<frames+3;i++)
            {
                Check(NotSoFatso.NsfRunFrame(handle)!=0,"loop/multi-chip PLAY completes "+i);
            }
            // Compare score ticks, not the arbitrary position of a reporting
            // boundary within INIT/PLAY. NSF2 IRQs continue during idle time.
            for(int entry=0;entry<=frames;entry++)
            {
                if(changes.TryGetValue(entry,out var list))foreach(var change in list)state[change.Register]=change.Value;
                if(entry>0)result.Add(string.Join(",",state.OrderBy(kv=>kv.Key).Select(kv=>$"{kv.Key:x}:{kv.Value:x}"))+"|"+expansionStates[entry]);
            }
        }
        finally{NotSoFatso.NsfClose(handle);GC.KeepAlive(callback);}
        return result;
    }
    static void RemovePcm(Project project)
    {
        project.Mmc5PcmEnabled=false;
        foreach(var i in project.Instruments)
            if(i.SamplesMapping != null)
                foreach(var key in i.SamplesMapping.Where(kv=>kv.Value.Sample.IsPcm).Select(kv=>kv.Key).ToArray())
                    i.UnmapDPCMSample(key);
    }
    static void AdditionalCases(string output)
    {
        var loop=Fixture(false,1,4097,true,true);
        var singleLoop=loop.DeepClone();singleLoop.Songs[0].SetLoopPoint(0);
        var singlePath=Path.Combine(output,"single-chip-loop.nsf");
        Check(new NsfFile().Save(singleLoop,FamiToneKernel.FamiStudio,singlePath,new[]{singleLoop.Songs[0].Id},"Single chip repeat","","",MachineType.NTSC,false),"MMC5 PCM repeated-song export");
        var singleBaseline=singleLoop.DeepClone();RemovePcm(singleBaseline);
        var singleBaselinePath=Path.Combine(output,"single-chip-loop-baseline.nsf");
        Check(new NsfFile().Save(singleBaseline,FamiToneKernel.FamiStudio,singleBaselinePath,new[]{singleBaseline.Songs[0].Id},"Single chip baseline","","",MachineType.NTSC,false),"ordinary MMC5 repeated-song baseline");
        Check(MusicalStates(singlePath,0,788).SequenceEqual(MusicalStates(singleBaselinePath,0,788)),"PCM preserves ordinary APU/DPCM/MMC5 register states through song repeats");
        loop.SetExpansionAudioMask(ExpansionType.Mmc5Mask|ExpansionType.S5BMask|ExpansionType.Vrc6Mask);
        var song=loop.Songs[0];song.SetLoopPoint(0);
        foreach(var chip in new[]{ExpansionType.S5B,ExpansionType.Vrc6})
        {
            var instrument=loop.CreateInstrument(chip,"Independent chip "+chip);
            var channel=song.Channels.First(c=>c.Expansion==chip);
            var pattern=channel.GetOrCreatePattern(0);
            pattern.Notes[0]=new Note(41){Instrument=instrument,Duration=56};
            pattern.Notes[64]=new Note(48){Instrument=instrument,Duration=80};
        }
        var captured=Mmc5PcmExportData.Capture(loop,new[]{song.Id},false);
        Check(captured.Tracks[song.Id].LoopTick>=0&&captured.Tracks[song.Id].EndTick>captured.Tracks[song.Id].LoopTick,"sparse PCM stream retains first pass and a repeat body");
        var pcmPath=Path.Combine(output,"multi-loop.nsf");
        Check(!new NsfFile().Save(loop,FamiToneKernel.FamiStudio,pcmPath,new[]{song.Id},"Loop multi-chip","","",MachineType.NTSC,false),"full repeat validation catches multi-chip loop-boundary overload even at 1x");
        song.SetLoopPoint(-1);
        Check(new NsfFile().Save(loop,FamiToneKernel.FamiStudio,pcmPath,new[]{song.Id},"Multi-chip","","",MachineType.NTSC,false),"PCM multi-chip nonlooping NSF2 export");
        var baseline=loop.DeepClone();RemovePcm(baseline);
        var oldPath=Path.Combine(output,"multi-loop-baseline.nsf");
        Check(new NsfFile().Save(baseline,FamiToneKernel.FamiStudio,oldPath,new[]{song.Id},"Loop multi-chip baseline","","",MachineType.NTSC,false),"ordinary multi-chip baseline export");
        Check(MusicalStates(pcmPath,0,404).SequenceEqual(MusicalStates(oldPath,0,404)),"PCM preserves ordinary APU/DPCM/MMC5/VRC6/S5B register states");

        var small=Fixture(false,1,17,true,false);
        for(int format=0;format<AssemblyFormat.Names.Length;format++)
        {
            var path=Path.Combine(output,"score-"+AssemblyFormat.Names[format]+".s");
            Check(new FamitoneMusicFile(FamiToneKernel.FamiStudio,false).Save(small,new[]{small.Songs[0].Id},format,8192,false,path,null,DpcmExportMode.Minimum,false,null,MachineType.NTSC),"score and sparse PCM commands export for "+AssemblyFormat.Names[format]);
            var bytes=File.ReadAllBytes(Path.ChangeExtension(path,".pcm"));
            Check(bytes.Take(17).SequenceEqual(small.GetInstrument("PCM instrument").GetPcmMapping(49).Sample.GetPcmData(false)),"assembler sidecar preserves unsigned PCM samples "+format);
            Check(File.ReadAllText(path).Contains("pcm_song_0")&&File.ReadAllText(path).Contains("famistudio_pcm_service"),"assembler includes the PCM stream and host IRQ contract "+format);
        }
        var cancelled=Path.Combine(output,"cancelled.nsf");var original=new byte[]{20,26,10,21};File.WriteAllBytes(cancelled,original);
        var oldLog=Log.LogOutput;var cancel=new CancelDuringValidation(output);
        try
        {
            Log.LogOutput=cancel;
            Check(!new NsfFile().Save(small,FamiToneKernel.FamiStudio,cancelled,new[]{small.Songs[0].Id},"Cancel","","",MachineType.NTSC,false),"CPU validation cancellation returns failure");
        }
        finally{Log.LogOutput=oldLog;}
        Check(cancel.Requested&&File.ReadAllBytes(cancelled).SequenceEqual(original),"cancel occurs after temporary write and preserves original export");
        Check(!Directory.EnumerateFiles(output,".pcm-nsf-*.tmp").Any(),"cancel removes temporary export");
    }
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);var results=new List<object>();
        foreach(bool pal in new[]{false,true})foreach(int rate in new[]{1,2})
        {
            foreach(int length in new[]{1,4,4097})
            {
                var p=Fixture(pal,rate,length,true,true);var name=$"pcm-{(pal?"pal":"ntsc")}-{rate}x-{length}.nsf";
                new ProjectFile().Save(p,Path.ChangeExtension(Path.Combine(output,name),".fms"));
                results.Add(Verify(p,Path.Combine(output,name),false,false));
            }
            results.Add(Verify(Fixture(pal,rate,17,false,false),Path.Combine(output,$"oneshot-{pal}-{rate}.nsfe"),true,false));
            results.Add(Verify(Fixture(pal,rate,1,true,false,true),Path.Combine(output,$"volume-{pal}-{rate}.nsf"),false,true));
        }
        foreach(bool pal in new[]{false,true})
        {
            // Keep a real source-read workload: one-byte loops are now cached.
            var heavy=Fixture(pal,4,2,true,true);
            var path=Path.Combine(output,$"budget-rejected-{pal}.nsf");
            var existing=new byte[]{4,8,15,16,23,42};
            File.WriteAllBytes(path,existing);
            Check(!new NsfFile().Save(heavy,FamiToneKernel.FamiStudio,path,new[]{heavy.Songs[0].Id},"Budget overload","","",pal?MachineType.PAL:MachineType.NTSC,false),"overloaded 4x project is rejected by actual CPU measurement");
            Check(File.ReadAllBytes(path).SequenceEqual(existing),"budget failure preserves existing destination bytes");
            Check(!Directory.EnumerateFiles(output,".pcm-nsf-*.tmp").Any(),"budget failure removes temporary export");

            // Four-times output can work when score and sample work fit each
            // deadline. Start a short sampler-only phrase after setup, without
            // simultaneous cold channel initialization or a looping DMC fetch.
            var light=Fixture(pal,4,4,true,false);
            light.Songs[0].Channels[0].GetOrCreatePattern(0).Notes.Clear();
            var notes=light.Songs[0].GetChannelByType(ChannelType.Mmc5Pcm).GetOrCreatePattern(0).Notes;
            var note=notes[0];notes.Clear();note.Duration=40;notes[8]=note;
            results.Add(Verify(light,Path.Combine(output,$"light-{pal}-4x.nsf"),false,false));
        }
        AdditionalCases(output);
        File.WriteAllText(Path.Combine(output,"pcm-nsf-validation.json"),JsonSerializer.Serialize(new{passed=true,checks,results},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS all {checks} MMC5 PCM export checks");
    }
}
