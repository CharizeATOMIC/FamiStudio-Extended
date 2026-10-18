using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FamiStudio;

static class PatternDelayRegression
{
    sealed class Player : BasePlayer
    {
        readonly Dictionary<int,int> registers = new();
        public readonly List<(int Period, int Volume)> Frames = new();
        public Player(bool pal) : base(NesApu.APU_WAV_EXPORT, pal, false) { Loop = LoopMode.Song; maxLoopCount = 3; }
        public override void NotifyRegisterWrite(int apu, int register, int value, int metadata = 0) => registers[register] = value;
        protected override short[] EndFrame()
        {
            var result = base.EndFrame();
            Frames.Add((registers.GetValueOrDefault(0x4002) | ((registers.GetValueOrDefault(0x4003)&7)<<8), registers.GetValueOrDefault(0x4000)&15));
            return result;
        }
        public void Render(Song song) { try { BeginPlaySong(song); while (PlaySongFrame()) { } } finally { Shutdown(); } }
    }
    static int checks;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS pattern delay: " + message); }
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        Settings.SquareSmoothVibrato = false;
        foreach (bool pal in new[]{false,true})
        {
            var project = new Project { PalMode = pal };
            var instrument = project.CreateInstrument(ExpansionType.None, "Pulse");
            var song = project.CreateSong("True loop with delayed echo");
            song.ChangeFamiStudioTempoGroove(new[]{1},false);
            song.SetDefaultPatternLength(16); song.SetBeatLength(4); song.SetLength(3); song.SetLoopPoint(1);
            var channel = song.Channels[0];
            var intro = channel.GetOrCreatePattern(0); intro.PlaybackDelay = 2;
            intro.Notes[0] = new Note(37) { Instrument=instrument, Duration=3 };
            intro.Notes[14] = new Note(40) { Instrument=instrument, Duration=4 };
            var body = channel.GetOrCreatePattern(1); body.PlaybackDelay = 7;
            body.Notes[0] = new Note(44) { Instrument=instrument, Duration=3 };
            body.Notes[4] = new Note(46) { Instrument=instrument, Duration=2 };
            var tail = channel.GetOrCreatePattern(2); tail.PlaybackDelay = 7;
            tail.Notes[14] = new Note(49) { Instrument=instrument, Duration=4 };

            var player = new Player(pal); player.Render(song);
            Check(player.Frames.Count==112,"three passes retain the 16 + (32) song timeline");
            var table = NesApu.GetNoteTableForChannelType(ChannelType.Square1,pal,1,440);
            var events = new SortedDictionary<int,(int note,int duration)> { [2]=(37,3),[16]=(40,4) };
            for(int pass=0;pass<3;pass++) {
                int start=16+32*pass;
                events[start+7]=(44,3); events[start+11]=(46,2); events[start+37]=(49,4);
            }
            int currentNote=0, stop=0;
            for(int frame=0;frame<player.Frames.Count;frame++) {
                if(events.TryGetValue(frame,out var e)) { currentNote=e.note; stop=frame+e.duration; }
                if(frame>=stop) currentNote=0;
                var actual=player.Frames[frame];
                Check(currentNote==0 ? actual.Volume==0 : actual.Volume==15 && actual.Period==table[currentNote],$"{(pal?"PAL":"NTSC")} scheduled notes and stops at frame {frame}");
            }
            Check(player.Frames[16].Period!=player.Frames[48].Period || player.Frames[16].Volume!=player.Frames[48].Volume,"first loop entrance can differ from a later entrance without extending the intro");
            Check(tail.ShallowClone().PlaybackDelay==7,"pattern duplication preserves delay");
            var binary=Path.Combine(output,pal?"delay-pal.fms":"delay-ntsc.fms");
            var text=Path.ChangeExtension(binary,".txt");
            Check(new ProjectFile().Save(project,binary),"binary save");
            Check(new FamistudioTextFile().Save(project,text,new[]{song.Id},false),"text save");
            foreach(var restored in new[]{project.DeepClone(),new ProjectFile().Load(binary),new FamistudioTextFile().Load(text)}) {
                Check(restored.Songs[0].Channels[0].PatternInstances[1].PlaybackDelay==7,"clone/binary/text delay round trip");
                var roundtrip=new Player(pal); roundtrip.Render(restored.Songs[0]);
                Check(roundtrip.Frames.SequenceEqual(player.Frames),"clone/binary/text retains cross-loop playback");
            }
        }
        Console.WriteLine($"PASS all {checks} pattern-delay checks");
    }
}
