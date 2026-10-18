using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FamiStudio;

static class LoopVariantRegression
{
    sealed class Player : BasePlayer
    {
        readonly Dictionary<int,int> registers = new();
        public readonly List<(int Period,int Volume)> Frames = new();
        public Player(bool pal, bool selection = false) : base(NesApu.APU_WAV_EXPORT,pal,false)
        {
            Loop = selection ? LoopMode.Pattern : LoopMode.Song;
            maxLoopCount = 3; minSelectedPattern = maxSelectedPattern = 1;
        }
        public override void NotifyRegisterWrite(int apu,int register,int value,int metadata=0) => registers[register]=value;
        protected override short[] EndFrame()
        {
            var result=base.EndFrame();
            Frames.Add((registers.GetValueOrDefault(0x4002)|((registers.GetValueOrDefault(0x4003)&7)<<8),registers.GetValueOrDefault(0x4000)&15));
            return result;
        }
        public void Render(Song song,int limit=200)
        {
            try { BeginPlaySong(song); while(Frames.Count<limit && PlaySongFrame()){} }
            finally { Shutdown(); }
        }
    }
    static int checks;
    static void Check(bool pass,string message)
    { if(!pass)throw new Exception(message);checks++;Console.WriteLine("PASS loop variant: "+message); }
    static void Reject(Action action,string message)
    { bool rejected=false;try { action(); }catch(ArgumentException){rejected=true;}Check(rejected,message); }
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        Settings.SquareSmoothVibrato=false;
        foreach(bool pal in new[]{false,true})
        {
            var project=new Project{PalMode=pal};var instrument=project.CreateInstrument(ExpansionType.None,"Pulse");
            var song=project.CreateSong("First pass and repeat");song.ChangeFamiStudioTempoGroove(new[]{1},false);
            song.SetDefaultPatternLength(16);song.SetBeatLength(4);song.SetLength(3);song.SetLoopPoint(1);
            var channel=song.Channels[0];
            var intro=channel.GetOrCreatePattern(0);intro.Notes[0]=new Note(37){Instrument=instrument,Duration=4};
            var body=channel.GetOrCreatePattern(1);body.Notes[0]=new Note(41){Instrument=instrument,Duration=6};
            var tail=channel.GetOrCreatePattern(2);tail.PlaybackDelay=3;tail.Notes[13]=new Note(46){Instrument=instrument,Duration=6};
            var variant=channel.CreatePattern("Later passes");variant.PlaybackDelay=2;
            variant.Notes[0]=new Note(49){Instrument=instrument,Duration=6};body.LoopVariant=variant;
            Check(body.GetPlaybackPattern(false)==body && body.GetPlaybackPattern(true)==variant,"explicit first/later selection");
            Check(variant.GetMaxInstanceLength()==16,"referenced variant inherits its owner's timeline length");
            channel.DeleteUnusedPatterns();Check(channel.Patterns.Contains(variant),"cleanup retains referenced variant");
            var copy=body.ShallowClone();Check(copy.LoopVariant!=variant && copy.LoopVariant.Notes[0].Value==variant.Notes[0].Value,"same-channel duplicate has independent variation");
            var other=body.ShallowClone(song.Channels[1]);Check(other.LoopVariant!=variant && other.LoopVariant.Channel==song.Channels[1],"cross-channel duplicate clones variation into destination");
            Reject(()=>body.LoopVariant=body,"self-reference rejected");
            Reject(()=>body.LoopVariant=other,"cross-channel reference rejected");
            Reject(()=>variant.LoopVariant=intro,"nested variation rejected");
            var table=NesApu.GetNoteTableForChannelType(ChannelType.Square1,pal,1,440);
            var player=new Player(pal);player.Render(song);
            Check(player.Frames.Count==112,"true loop stays 16 + 3*32 frames");
            Check(player.Frames[16].Period==table[41] && player.Frames[16].Volume==15,"first pass uses ordinary pattern");
            Check(player.Frames[48].Period==table[46] && player.Frames[48].Volume==15,"previous delayed tail survives loop jump");
            Check(player.Frames[50].Period==table[49] && player.Frames[50].Volume==15,"second pass uses variation and its delay");
            Check(player.Frames[82].Period==table[49] && player.Frames[82].Volume==15,"later passes keep variation");
            var selection=new Player(pal,true);selection.Render(song,80);
            Check(selection.Frames[32].Period==table[41] && selection.Frames[48].Period==table[41],"selected-pattern looping always uses visible original");
            string binary=Path.Combine(output,pal?"variant-pal.fms":"variant-ntsc.fms");string text=Path.ChangeExtension(binary,".txt");
            Check(new ProjectFile().Save(project,binary),"binary save");
            Check(new FamistudioTextFile().Save(project,text,new[]{song.Id},false),"text save with forward variant name");
            foreach(var restored in new[]{project.DeepClone(),new ProjectFile().Load(binary),new FamistudioTextFile().Load(text)})
            {
                var restoredBody=restored.Songs[0].Channels[0].PatternInstances[1];
                Check(restoredBody.LoopVariant?.Notes[0].Value==49,"clone/binary/text preserves variation reference and notes");
                var roundtrip=new Player(pal);roundtrip.Render(restored.Songs[0]);
                Check(roundtrip.Frames.SequenceEqual(player.Frames),"clone/binary/text preserves continuous playback");
            }
            // Exercise the actual clipboard's dependency order and ID remapping,
            // without relying on a desktop clipboard provider in this headless test.
            var buffer=new ProjectSaveBuffer(project,ProjectBufferFlags.Clipboard);
            var clipboard=typeof(ClipboardUtils);var flags=BindingFlags.Static|BindingFlags.NonPublic;
            clipboard.GetMethod("SavePatternList",flags).Invoke(null,new object[]{buffer,new HashSet<Pattern>{body,variant}});
            var destination=project.CreateSong("Paste target");
            var reader=new ProjectLoadBuffer(project,buffer.GetBuffer(),Project.Version,ProjectBufferFlags.Clipboard);
            clipboard.GetMethod("LoadAndMergePatternList",flags).Invoke(null,new object[]{reader,destination,false,ClipboardImportFlags.CreateMissing});
            Check(destination.Channels[0].GetPattern(body.Name).LoopVariant?.Name==variant.Name,"clipboard remaps variant dependency before owner");
            // Deleting the last visible owner should also remove its unreferenced variant.
            channel.PatternInstances[1]=null;channel.DeleteUnusedPatterns();
            Check(!channel.Patterns.Contains(body) && !channel.Patterns.Contains(variant),"deleting owner releases unused variation");
        }
        CheckResourceCleanupAndResize();
        Console.WriteLine($"PASS all {checks} loop-variant checks");
    }
    static void CheckResourceCleanupAndResize()
    {
        var project = new Project(); var song = project.CreateSong();
        song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        song.SetLength(2); song.SetDefaultPatternLength(16);
        var instrument = project.CreateInstrument(0, "Variation only");
        var arpeggio = project.CreateArpeggio("Variation arpeggio");
        var channel = song.Channels[0];
        var first = channel.GetOrCreatePattern(0);
        var second = channel.GetOrCreatePattern(1);
        var variant = channel.CreatePattern("Shared variation");
        variant.Notes[2] = new Note(49) { Instrument=instrument, Arpeggio=arpeggio, Duration=4 };
        first.LoopVariant=variant; second.LoopVariant=variant;
        var sample=project.CreateDPCMSampleFromDmcData("Variation sample",new byte[17]);
        instrument.MapDPCMSample(50,sample);
        var dpcm=song.Channels[ChannelType.Dpcm];
        var dpcmFirst=dpcm.GetOrCreatePattern(0);var dpcmVariant=dpcm.CreatePattern("Sample variation");
        dpcmVariant.Notes[0]=new Note(50){Instrument=instrument,Duration=1};dpcmFirst.LoopVariant=dpcmVariant;
        project.DeleteUnusedInstruments();project.DeleteUnusedArpeggios();project.UnmapUnusedSamples();
        Check(project.Instruments.Contains(instrument),"cleanup preserves variation-only instrument");
        Check(project.Arpeggios.Contains(arpeggio),"cleanup preserves variation-only arpeggio");
        Check(instrument.GetDPCMMapping(50)?.Sample==sample && song.UsesDpcm,"cleanup and DPCM detection include variation-only sample");
        Check(channel.RenamePattern(variant,"Renamed variation") && first.LoopVariant==variant,"renaming retains variation references");
        Check(song.DuplicatePatternsForNoteLengthChange(0,0,false),"tempo change duplicates variation shared outside selection");
        var selected=channel.PatternInstances[0].LoopVariant;
        Check(selected!=variant && channel.PatternInstances[1].LoopVariant==variant,"tempo change isolates shared variation");
        song.ResizePatternNotes(0,2);
        Check(selected.Notes.ContainsKey(4) && selected.Notes[4].Duration==8,"tempo resize scales variation note position and duration");
        Check(variant.Notes.ContainsKey(2) && variant.Notes[2].Duration==4,"tempo resize preserves variation outside selection");
        first=channel.PatternInstances[0]; second=channel.PatternInstances[1];
        first.LoopVariant=variant; second.LoopVariant=variant;
        song.SetPatternCustomSettings(1,8,4,new[]{1});
        channel.MakePatternsWithDifferentLengthsUnique();
        Check(first.LoopVariant!=second.LoopVariant,"different pattern lengths isolate shared variation");
        first.LoopVariant=variant; second.LoopVariant=variant;
        song.SetPatternCustomSettings(1,16,4,new[]{1,2});
        channel.MakePatternsWithDifferentGroovesUnique();
        Check(first.LoopVariant!=second.LoopVariant,"different grooves isolate shared variation");
    }

}
