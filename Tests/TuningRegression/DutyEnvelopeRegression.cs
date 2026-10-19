using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FamiStudio;

static class DutyEnvelopeRegression
{
    sealed class Capture : BasePlayer
    {
        readonly Dictionary<int,int> registers=new();
        public readonly List<(int Pulse,int Noise)> Frames=new();
        public Capture(bool pal):base(NesApu.APU_WAV_EXPORT,pal,false){Loop=LoopMode.None;}
        public override void NotifyRegisterWrite(int apu,int register,int value,int metadata=0)=>registers[register]=value;
        protected override short[] EndFrame(){var audio=base.EndFrame();Frames.Add((registers.GetValueOrDefault(0x4000)>>6,registers.GetValueOrDefault(0x400e)>>7));return audio;}
        public void Render(Song song){try{BeginPlaySong(song);while(PlaySongFrame()){} }finally{Shutdown();}}
    }
    static int checks;
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;Console.WriteLine("PASS duty envelope: "+message);}
    static Project Make(bool latched)
    {
        var p=new Project();var song=p.CreateSong("Duty macro switch");song.ChangeFamiStudioTempoGroove(new[]{1},false);song.SetLength(1);song.SetLoopPoint(-1);song.SetDefaultPatternLength(48);song.SetBeatLength(8);
        var one=p.CreateInstrument(0,"One");one.Envelopes[EnvelopeType.DutyCycle].Values[0]=1;
        var zero=p.CreateInstrument(0,"Zero");var ze=zero.Envelopes[EnvelopeType.DutyCycle];ze.Length=2;ze.Loop=0;
        var empty=p.CreateInstrument(0,"Disabled");empty.Envelopes[EnvelopeType.DutyCycle].Length=0;
        var motion=p.CreateInstrument(0,"Motion");var me=motion.Envelopes[EnvelopeType.DutyCycle];me.Length=3;me.Values[0]=0;me.Values[1]=1;me.Values[2]=0;
        foreach(int ch in new[]{ChannelType.Square1,ChannelType.Noise})
        {
            var pattern=song.GetChannelByType(ch).GetOrCreatePattern(0);
            var order=new[]{one,zero,one,empty,motion,zero};
            for(int i=0;i<order.Length;i++)pattern.Notes[i*8]=new Note(ch==ChannelType.Noise?9:46){Instrument=order[i],Duration=8};
            if(latched)pattern.Notes[0].DutyCycle=1;
            pattern.Notes[40].DutyCycle=0;
        }
        return p;
    }
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        foreach(bool pal in new[]{false,true})foreach(bool latched in new[]{false,true})
        {
            var original=Make(latched);original.PalMode=pal;
            var first=new Capture(pal);first.Render(original.Songs[0]);
            int inherited=latched?1:0;
            Check(first.Frames[0]==(1,1)&&first.Frames[8]==(inherited,inherited)&&first.Frames[16]==(1,1)&&first.Frames[24]==(inherited,inherited),$"Pulse/noise 1→zero/disabled macro retains established effect semantics; PAL={pal}, latched={latched}");
            Check(first.Frames[32]==(0,0)&&first.Frames[33]==(1,1)&&first.Frames[34]==(0,0)&&first.Frames[40]==(0,0),"nonconstant 0/1 macro and explicit zero effect both control actual pulse/noise registers");
            string binary=Path.Combine(output,$"duty-{pal}-{latched}.fms"),text=Path.ChangeExtension(binary,".txt");
            Check(new ProjectFile().Save(original,binary)&&new FamistudioTextFile().Save(original,text,new[]{original.Songs[0].Id},false),"binary/text save");
            foreach(var restored in new[]{new ProjectFile().Load(binary),new FamistudioTextFile().Load(text)})
            {
                var zero=restored.GetInstrument("Zero").Envelopes[EnvelopeType.DutyCycle];var empty=restored.GetInstrument("Disabled").Envelopes[EnvelopeType.DutyCycle];
                var capture=new Capture(pal);capture.Render(restored.Songs[0]);
                Check(capture.Frames.SequenceEqual(first.Frames),"all pulse/noise frames remain identical after binary/text reload");
                Check(zero.Length==2&&zero.Loop==0&&zero.Values.Take(2).All(v=>v==0),"explicit constant-zero macro length and loop survive");
                Check(empty.Length==0,"disabled duty macro remains zero-length rather than default one-step");
            }
        }
        Console.WriteLine($"PASS all {checks} duty envelope checks");
    }
}
