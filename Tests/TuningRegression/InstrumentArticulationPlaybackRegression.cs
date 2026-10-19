using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FamiStudio;

// These tests exercise the actual player and APU writes, independently of the
// export-engine tests and the instrument-model/serialization tests.
static class InstrumentArticulationPlaybackRegression
{
    sealed class Capture : BasePlayer
    {
        readonly Dictionary<int,int> registers = new();
        readonly List<(int Register,int Value)> pending = new();
        public readonly List<Dictionary<int,int>> Frames = new();
        public readonly List<(int Register,int Value)[]> Writes = new();
        public Capture(bool pal) : base(NesApu.APU_WAV_EXPORT,pal,false) { Loop=LoopMode.None; }
        public override void NotifyRegisterWrite(int apu,int register,int value,int metadata=0)
        { registers[register]=value;pending.Add((register,value)); }
        protected override short[] EndFrame()
        { var audio=base.EndFrame();Frames.Add(new(registers));Writes.Add(pending.ToArray());pending.Clear();return audio; }
        public void Render(Song song) { try { BeginPlaySong(song);while(PlaySongFrame()){} } finally { Shutdown(); } }
        public int Timer(int frame,int channel) => Frames[frame].GetValueOrDefault(0x4002+channel*4) | ((Frames[frame].GetValueOrDefault(0x4003+channel*4)&7)<<8);
        public int HighWrites(int frame,int channel) => Writes[frame].Count(w=>w.Register==0x4003+channel*4);
    }
    static int checks;
    static void Check(bool condition,string message)
    { if(!condition)throw new Exception(message);checks++;Console.WriteLine("PASS articulation playback: "+message); }
    static Project Make(bool pal,int frames,int mask=0)
    {
        var p=new Project { PalMode=pal };p.SetExpansionAudioMask(mask);
        var s=p.CreateSong("Articulation playback");s.ChangeFamiStudioTempoGroove(new[]{1},false);
        s.SetLength(1);s.SetLoopPoint(-1);s.SetDefaultPatternLength(frames);s.SetBeatLength(4);return p;
    }
    static Capture Render(Project p) { var c=new Capture(p.PalMode);c.Render(p.Songs[0]);return c; }
    static void Pitch(Instrument i,int[] values,bool relative=false)
    {
        var e=i.Envelopes[EnvelopeType.Pitch];e.Length=values.Length;e.Relative=relative;
        for(int n=0;n<values.Length;n++)e.Values[n]=(sbyte)values[n];
    }
    static void Phase(bool pal,bool enabled,bool smooth)
    {
        Settings.SquareSmoothVibrato=smooth;
        var p=Make(pal,40);var s=p.Songs[0];var i=p.CreateInstrument(0,"Phase");i.ResetPhaseOnAttack=enabled;
        var different=p.CreateInstrument(0,"Different envelope");different.ResetPhaseOnAttack=enabled;
        different.Envelopes[EnvelopeType.Volume].Values[0]=14;
        int[] table=NesApu.GetNoteTableForChannelType(ChannelType.Square1,pal,1).Select(v=>(int)v).ToArray();
        foreach(int ch in new[]{ChannelType.Square1,ChannelType.Square2})
        {
            var pattern=s.GetChannelByType(ch).GetOrCreatePattern(0);
            foreach(int f in new[]{0,4,8,24,28})
                pattern.Notes[f]=new Note(46){Instrument=i,Duration=40,FinePitch=(sbyte)(255-table[46]),HasAttack=f!=8&&f!=28};
            pattern.Notes[12]=new Note(45){Instrument=i,Duration=40,FinePitch=(sbyte)(256-table[45])};
            pattern.Notes[16]=new Note(Note.NoteRelease);
            pattern.Notes[20]=new Note(Note.NoteStop);
            pattern.Notes[32]=new Note(46){Instrument=different,Duration=40,HasAttack=false};
            pattern.Notes[36]=new Note(46){Instrument=different,Duration=40,HasAttack=false,PhaseReset=1};
        }
        var capture=Render(p);
        foreach(int ch in new[]{ChannelType.Square1,ChannelType.Square2})
        {
            Check(capture.HighWrites(4,ch)==(enabled?1:0),$"same-period attack resets exactly once iff enabled; PAL={pal}, smooth={smooth}, enabled={enabled}, channel={ch}");
            Check(new[]{8,16,20,28}.All(f=>capture.HighWrites(f,ch)==0),"legato, release, stop and later legato do not reset phase");
            Check(new[]{12,24}.All(f=>capture.HighWrites(f,ch)==(enabled||!smooth?1:0)),"high-byte crossing uses one attack reset or the existing smooth-vibrato path");
            Check(capture.HighWrites(32,ch)==(enabled?1:0),"incompatible-envelope instrument switch uses its required attack");
            Check(capture.HighWrites(36,ch)==1,"explicit phase effect remains available on a legato note");
        }
    }
    static void Boundaries(bool pal,bool enabled,bool clampPeriods)
    {
        Settings.SquareSmoothVibrato=false;Settings.ClampPeriods=clampPeriods;
        var p=Make(pal,48);var s=p.Songs[0];var i=p.CreateInstrument(0,"Low-byte boundaries");i.PitchClampToTimerByte=enabled;
        int[] pitch={0,1,63,-63,-1,0};Pitch(i,pitch);
        int[] bases={255,256,511,512,0,2047};
        var table=NesApu.GetNoteTableForChannelType(ChannelType.Square1,pal,1);
        foreach(int ch in new[]{ChannelType.Square1,ChannelType.Square2,ChannelType.Triangle})
        {
            var pattern=s.GetChannelByType(ch).GetOrCreatePattern(0);
            for(int n=0;n<bases.Length;n++)
            {
                int note=Enumerable.Range(1,96).OrderBy(k=>Math.Abs(table[k]-bases[n])).First();
                int fine=bases[n]-table[note];
                if(fine < -128 || fine >127)throw new Exception("Boundary test cannot express fine pitch.");
                pattern.Notes[n*8]=new Note(note){Instrument=i,Duration=8,FinePitch=(sbyte)fine};
            }
        }
        var capture=Render(p);
        foreach(int ch in new[]{ChannelType.Square1,ChannelType.Square2,ChannelType.Triangle})
        {
            var expected=new List<int>();
            foreach(int basis in bases)for(int f=0;f<8;f++)
            {
                int offset=pitch[Math.Min(f,pitch.Length-1)];
                int timer=enabled?(basis&~255)+Math.Clamp((basis&255)+offset,0,255):basis+offset;
                expected.Add(clampPeriods?Math.Clamp(timer,0,2047):timer&2047);
            }
            Check(Enumerable.Range(0,48).All(f=>capture.Timer(f,ch)==expected[f]),$"six signed-pitch boundaries and hardware bounds; PAL={pal}, enabled={enabled}, clampPeriods={clampPeriods}, channel={ch}");
        }
    }
    static void RelativeAndArpeggio(bool pal)
    {
        Settings.ClampPeriods=true;Settings.SquareSmoothVibrato=false;
        var p=Make(pal,16);var s=p.Songs[0];var i=p.CreateInstrument(0,"Relative with arpeggio");i.PitchClampToTimerByte=true;
        Pitch(i,new[]{20,20,-10,-60,10,0},true);
        var arp=i.Envelopes[EnvelopeType.Arpeggio];arp.Length=6;
        int[] arps={0,1,-1,0,1,-1};for(int j=0;j<arps.Length;j++)arp.Values[j]=(sbyte)arps[j];
        var table=NesApu.GetNoteTableForChannelType(ChannelType.Square1,pal,1);
        foreach(int ch in new[]{ChannelType.Square1,ChannelType.Square2,ChannelType.Triangle})
            s.GetChannelByType(ch).GetOrCreatePattern(0).Notes[0]=new Note(46){Instrument=i,Duration=16,FinePitch=3};
        var c=Render(p);int[] accumulated={20,40,30,-30,-20,-20};
        foreach(int ch in new[]{ChannelType.Square1,ChannelType.Square2,ChannelType.Triangle})
            Check(Enumerable.Range(0,6).All(f=> { int basis=table[46+arps[f]]+3;return c.Timer(f,ch)==(basis&~255)+Math.Clamp((basis&255)+accumulated[f],0,255); }),$"relative pitch accumulates before low-byte saturation while arpeggio and fine pitch select the base; PAL={pal}, channel={ch}");
    }
    static void Slides(bool pal)
    {
        Settings.ClampPeriods=true;Settings.SquareSmoothVibrato=false;
        var p=Make(pal,32);var s=p.Songs[0];var plain=p.CreateInstrument(0,"Sliding base");
        foreach(int ch in new[]{ChannelType.Square1,ChannelType.Square2,ChannelType.Triangle})
            s.GetChannelByType(ch).GetOrCreatePattern(0).Notes[0]=new Note(40){Instrument=plain,Duration=32,SlideNoteTarget=52,FinePitch=7};
        var basis=Render(p);plain.PitchClampToTimerByte=true;Pitch(plain,new[]{63});var clamped=Render(p);
        foreach(int ch in new[]{ChannelType.Square1,ChannelType.Square2,ChannelType.Triangle})
        {
            Check(Enumerable.Range(0,32).Select(f=>basis.Timer(f,ch)).Distinct().Count()>10,"slide fixture traverses actual timer values");
            Check(Enumerable.Range(0,32).All(f=> { int b=basis.Timer(f,ch);return clamped.Timer(f,ch)==(b&~255)+Math.Min((b&255)+63,255); }),$"sliding timer is part of the base, then only pitch envelope saturates; PAL={pal}, channel={ch}");
        }
    }
    static void ExpansionUnaffected(bool pal)
    {
        var p=Make(pal,16,ExpansionType.Vrc6Mask);var i=p.CreateInstrument(ExpansionType.Vrc6,"VRC6 unaffected");Pitch(i,new[]{63,-63,0});
        var pattern=p.Songs[0].GetChannelByType(ChannelType.Vrc6Square1).GetOrCreatePattern(0);
        pattern.Notes[0]=new Note(46){Instrument=i,Duration=16};pattern.Notes[8]=new Note(46){Instrument=i,Duration=8};
        var normal=Render(p);i.ResetPhaseOnAttack=true;i.PitchClampToTimerByte=true;var flags=Render(p);
        Check(normal.Writes.SelectMany(w=>w).SequenceEqual(flags.Writes.SelectMany(w=>w)), $"APU articulation flags have no effect on VRC6 register writes; PAL={pal}");
    }
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);checks=0;bool oldSmooth=Settings.SquareSmoothVibrato,oldClamp=Settings.ClampPeriods;
        try
        {
            foreach(bool pal in new[]{false,true})
            {
                foreach(bool enabled in new[]{false,true})foreach(bool smooth in new[]{false,true})Phase(pal,enabled,smooth);
                foreach(bool enabled in new[]{false,true})foreach(bool clamp in new[]{false,true})Boundaries(pal,enabled,clamp);
                RelativeAndArpeggio(pal);Slides(pal);ExpansionUnaffected(pal);
            }
        }
        finally { Settings.SquareSmoothVibrato=oldSmooth;Settings.ClampPeriods=oldClamp; }
        File.WriteAllText(Path.Combine(output,"result.txt"),$"PASS {checks} independent articulation playback checks.\n");
        Console.WriteLine($"PASS all {checks} independent articulation playback checks");
    }
}
