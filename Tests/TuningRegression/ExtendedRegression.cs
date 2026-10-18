using System;
using System.IO;
using System.Linq;
using FamiStudio;

static class ExtendedRegression
{
    public static void Run(string output, Action<bool,string> check)
    {
        // Search the real FM register space independently of the production quantizer.
        foreach (int type in new[] { ChannelType.Vrc7Fm1, ChannelType.EPSMFm1 })
        foreach (int offset in new[] { -1199, -51, 37, 1199 })
        {
            var cents = Enumerable.Repeat(offset,12).ToArray();
            var table = CustomTuningTables.Get(type,false,8,432,cents);
            for (int note=1;note<=96;note++)
            {
                double freq=432*Math.Pow(2,(note-46)/12.0+offset/1200.0);
                bool epsm=type==ChannelType.EPSMFm1;
                double ideal=epsm?144*freq*1048576/8000000:freq*262144/49715;
                int actual=table.Periods[note];
                if(epsm) {int oct=0,m=actual;while(m>=512){m>>=1;oct++;}actual=((m<<2)|table.Fraction[note])<<oct;}
                double error=Math.Abs(Math.Log(actual/ideal)),best=double.MaxValue;
                for(int block=0;block<8;block++)for(int fnum=1;fnum<(epsm?2048:512);fnum++)best=Math.Min(best,Math.Abs(Math.Log((fnum<<block)/ideal)));
                check(error<=best+1e-12,$"nearest hardware FM pitch type {type}, note {note}, cents {offset}");
            }
        }
        for(int expansion=1;expansion<ExpansionType.Count;expansion++)
        {
            var p=Program.MakeProject();p.SetExpansionAudioMask(ExpansionType.GetMaskFromValue(expansion),8);
            foreach(var c in p.Songs[0].Channels)foreach(var clearPattern in c.Patterns)clearPattern.Notes.Clear();
            var counts=Enumerable.Repeat(1,ExpansionType.Count).ToArray();counts[expansion]=2;p.SetExpansionInstances(counts);
            var s=p.Songs[0];var c0=s.Channels.First(c=>c.Expansion==expansion&&c.ChipInstance==0);var c1=s.GetChannelByType(c0.Type,1);
            var inst=p.CreateInstrument(expansion);
            c0.GetOrCreatePattern(0).Notes[0]=new Note(46){Instrument=inst,Duration=64};
            c1.GetOrCreatePattern(0).Notes[0]=new Note(49){Instrument=inst,Duration=64};
            c0.StereoEnabled=c1.StereoEnabled=true;c0.Pan=0;c1.Pan=100;
            var left=new CapturePlayer(false,true){ChannelMask=1L<<c0.Index};left.Render(s);
            var right=new CapturePlayer(false,true){ChannelMask=1L<<c1.Index};right.Render(s);
            check(left.Audio.Skip(12000).Where((v,i)=>(i&1)==0).Any(v=>v!=0),$"chip {expansion} first instance produces left audio");
            check(right.Audio.Skip(12000).Where((v,i)=>(i&1)==1).Any(v=>v!=0),$"chip {expansion} second instance produces right audio");
            check(left.Audio.Skip(12000).Where((v,i)=>(i&1)==1).All(v=>v==0),$"chip {expansion} first instance has no right leakage");
            check(right.Audio.Skip(12000).Where((v,i)=>(i&1)==0).All(v=>v==0),$"chip {expansion} second instance has no left leakage");
            var binary=Path.Combine(output,$"duplicate-{expansion}.fms");new ProjectFile().Save(p,binary);
            var loaded=new ProjectFile().Load(binary);
            check(loaded.Songs[0].GetChannelByType(c0.Type,1).Pan==100,$"chip {expansion} binary round trip");
            var text=Path.ChangeExtension(binary,"txt");new FamistudioTextFile().Save(p,text,new[]{s.Id},false);
            loaded=new FamistudioTextFile().Load(text);
            check(loaded.Songs[0].GetChannelByType(c0.Type,1).PatternInstances[0].Channel.ChipInstance==1,$"chip {expansion} text pattern ownership");
            int files=0;
            AudioExportUtils.Save(s,Path.Combine(output,$"duplicate-{expansion}.wav"),44100,1,-1,(1L<<c0.Index)|(1L<<c1.Index),true,false,true,null,0,false,false,(a,n,path)=> { files++;WaveFile.Save(a,path,44100,n); });
            check(files==2 && c0.ShortName!=c1.ShortName,$"chip {expansion} separate WAV files have unique names");
        }
        var effects=Program.MakeProject();var song=effects.Songs[0];var instrument=effects.Instruments[0];
        var tuning=song.TuningSettings.Clone();tuning.Cents=new[]{0,12,-31,44,-14,16,-8,20,30,-12,5,-19};song.TuningSettings=tuning;
        var pat=song.Channels[0].GetOrCreatePattern(0);pat.Notes.Clear();
        pat.Notes[0]=new Note(46){Instrument=instrument,Duration=16,SlideNoteTarget=53};
        pat.Notes[16]=new Note(49){Instrument=instrument,Duration=16,RawVibrato=0x34,FinePitch=3};
        song.Channels[0].GetOrCreatePattern(1).Notes[0]=new Note(46){Instrument=instrument,Duration=32,RawVibrato=0,FinePitch=0};
        var editor=new CapturePlayer(false,false);editor.Render(song);
        var pathNsf=Path.Combine(output,"effects.nsf");new NsfFile().Save(effects,FamiToneKernel.FamiStudio,pathNsf,new[]{song.Id},"Effects","Test","",MachineType.NTSC,false);
        var handle=NotSoFatso.NsfOpen(pathNsf);NotSoFatso.NsfSetTrack(handle,0);
        NotSoFatso.NsfRunFrame(handle); // NSF initialization frame precedes the first music update.
        for(int frame=0;frame<60;frame++)
        {
            NotSoFatso.NsfRunFrame(handle);
            int actual=NotSoFatso.NsfGetState(handle,0,NotSoFatso.STATE_PERIOD,0);
            int expected=editor.Frames[frame][0x4002]|((editor.Frames[frame][0x4003]&7)<<8);
            check(actual==expected,$"microtonal slide/vibrato/fine pitch frame {frame}: {actual} = {expected}");
        }
        NotSoFatso.NsfClose(handle);
        song.SetLoopPoint(0);
        pathNsf=Path.Combine(output,"loop.nsf");new NsfFile().Save(effects,FamiToneKernel.FamiStudio,pathNsf,new[]{song.Id},"Loop","Test","",MachineType.NTSC,false);
        handle=NotSoFatso.NsfOpen(pathNsf);NotSoFatso.NsfSetTrack(handle,0);
        int firstPeriod=0;
        for(int frame=0;frame<80;frame++) {NotSoFatso.NsfRunFrame(handle);if(frame==8)firstPeriod=NotSoFatso.NsfGetState(handle,0,NotSoFatso.STATE_PERIOD,0);if(frame==72)check(firstPeriod==NotSoFatso.NsfGetState(handle,0,NotSoFatso.STATE_PERIOD,0),"loop restores the earlier tuning table");}
        NotSoFatso.NsfClose(handle);
        song.ExtendForLooping(2,false);
        check(song.GetTuningHz(3)==450,"expanding a loop preserves tuning-only pattern settings");

        var maximum=Program.MakeProject(); maximum.SetExpansionAudioMask(127,8);
        var maxCounts=Enumerable.Repeat(1,ExpansionType.Count).ToArray();
        maxCounts[ExpansionType.N163]=3;maxCounts[ExpansionType.Mmc5]=3;maxCounts[ExpansionType.Fds]=2;
        maximum.SetExpansionInstances(maxCounts);
        check(maximum.GetActiveChannelCount()==64,"64-channel boundary configuration");
        var last=maximum.Songs[0].Channels[63];last.StereoEnabled=true;last.Pan=100;
        last.GetOrCreatePattern(0).Notes[0]=new Note(49){Instrument=maximum.CreateInstrument(last.Expansion),Duration=64};
        var timer=System.Diagnostics.Stopwatch.StartNew();
        var full=new CapturePlayer(false,true){ChannelMask=1L<<63};full.Render(maximum.Songs[0]);timer.Stop();
        check(full.Audio.Skip(12000).Any(v=>v!=0),"bit 63 channel mask plays the last channel");
        new ProjectFile().Save(maximum, Path.Combine(output, "64-channels.fms"));
        Console.WriteLine($"64 channels: {full.Audio.Count / 88200.0:F2} s of audio rendered in {timer.Elapsed.TotalSeconds:F2} s");
        maxCounts[ExpansionType.Fds]++;
        bool rejected=false;try{maximum.SetExpansionInstances(maxCounts);}catch(ArgumentException){rejected=true;}
        check(rejected && maximum.GetActiveChannelCount()==64,"oversized configuration rejects atomically");

        var imported=Program.MakeProject();imported.SetExpansionAudioMask(ExpansionType.Vrc6Mask);
        var importCounts=Enumerable.Repeat(1,ExpansionType.Count).ToArray();importCounts[ExpansionType.Vrc6]=2;imported.SetExpansionInstances(importCounts);
        imported.Songs[0].GetChannelByType(ChannelType.Vrc6Square1,1).GetOrCreatePattern(0).Notes[0]=new Note(50){Instrument=imported.CreateInstrument(ExpansionType.Vrc6),Duration=32};
        var destination=new Project();destination.SetExpansionAudioMask(ExpansionType.Vrc6Mask);destination.CreateSong("Destination");
        check(destination.MergeProject(imported),"merge project with extra chip instances");
        check(destination.GetExpansionInstances(ExpansionType.Vrc6)==2 && destination.Songs.Any(s=>s.GetChannelByType(ChannelType.Vrc6Square1,1).PatternInstances[0]?.Notes.ContainsKey(0)==true),"merge retains notes on the second chip");

        var mix=Program.MakeProject();var mixSong=mix.Songs[0];mixSong.Channels[0].StereoEnabled=true;mixSong.Channels[0].Pan=0;
        short[] mixed=null;
        AudioExportUtils.Save(mixSong,"unused.wav",44100,1,-1,1,false,false,true,new[]{0f,.5f,.5f,.5f,.5f},0,false,false,(a,n,file)=>mixed=a);
        check(mixed.Skip(12000).Where((v,i)=>(i&1)==1).All(v=>v==0),"audio export preserves the song's left pan once");
        AudioExportUtils.Save(mixSong,"unused.wav",44100,1,-1,1,false,false,true,Enumerable.Repeat(.5f,5).ToArray(),0,false,false,(a,n,file)=>mixed=a);
        check(mixed.Where((v,i)=>(i&1)==0).SequenceEqual(mixed.Where((v,i)=>(i&1)==1)),"export pan override centers a previously left-panned song");
    }
}
