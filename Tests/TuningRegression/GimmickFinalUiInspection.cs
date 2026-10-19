using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FamiStudio;

// Optional local inspection of a user-supplied converted project. No game data
// is embedded here, and this does not change or save the source project.
static class GimmickFinalUiInspection
{
    static object Field(object target,string name) => target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target);
    static object Invoke(object target,string name,params object[] args) => target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(target,args);
    static void Check(bool condition,string message) { if(!condition)throw new Exception(message);Console.WriteLine("PASS final Gimmick UI: "+message); }
    public static void Run(FamiStudioWindow window,string output,Action<FamiStudioWindow,string> capture)
    {
        var app=window.PianoRoll.App;var project=app.Project;var initial=app.SelectedSong;
        void ShowInstrument(Instrument instrument)
        {
            foreach(var folder in project.GetFoldersForType(FolderType.Instrument))folder.Expanded=folder.Name==instrument.FolderName;
            app.SelectedInstrument=instrument;
            typeof(ProjectExplorer).GetField("expandedInstrument",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(window.ProjectExplorer,null);
            Invoke(window.ProjectExplorer,"ToggleExpandInstrument",instrument);
            var panel=(PanelContainer)Invoke(window.ProjectExplorer,"FindInstrumentPanel",instrument);
            ((Container)Field(window.ProjectExplorer,"mainContainer")).ScrollY=Math.Max(0,panel.Top-48);
        }
        void ShowChannel(Song selected,int type,int effect)
        {
            app.SelectedSong=selected;
            int index=Array.FindIndex(selected.Channels,c=>c.Type==type);
            Check(index>=0,"inspection channel exists: "+ChannelType.InternalNames[type]);
            app.StartEditChannel(index,0);
            // StartEditChannel may restore the previous channel's horizontal
            // scroll. Explicitly return to the opening bars for these captures.
            Invoke(window.PianoRoll,"CenterScroll",0);
            app.EffectPanelExpanded=true;window.PianoRoll.SelectedEffect=effect;
            var instrument=selected.Channels[index].PatternInstances.Take(selected.Length).Where(p=>p!=null)
                .SelectMany(p=>p.Notes.Values).First(n=>n.IsMusical&&n.Instrument!=null).Instrument;
            ShowInstrument(instrument);
        }
        try
        {
        Check(project.Songs.Count==20,"all twenty songs load");
        Check(project.Songs.SelectMany(s=>s.Channels).SelectMany(c=>c.Patterns).All(p=>p.PlaybackDelay==0),"all displayed note starts have zero hidden pattern delay");
        var noiseNotes=project.Songs.SelectMany(s=>s.Channels).Where(c=>c.Type==ChannelType.Noise).SelectMany(c=>c.Patterns).SelectMany(p=>p.Notes.Values).ToArray();
        Check(noiseNotes.All(n=>!n.HasDutyCycle&&!n.HasPhaseReset&&!n.HasFinePitch),"noise score contains no repeated zero duty, fine-pitch or phase effects");
        var dpcmNotes=project.Songs.SelectMany(s=>s.Channels).Where(c=>ChannelType.IsDpcmChannel(c.Type)).SelectMany(c=>c.Patterns).SelectMany(p=>p.Notes.Values).ToArray();
        Check(dpcmNotes.Any(n=>n.IsMusical)&&dpcmNotes.All(n=>!n.HasDeltaCounter),"all DPCM note and effect-only records use their mapping DAC without redundant DeltaCounter effects");
        var song=project.Songs.First(s=>s.Name=="Good Morning");app.SelectedSong=song;
        Check(song.NoteLength==9&&song.BeatLength==36&&song.PatternLength==144,"Good Morning defaults expose four steps per beat and sixteen per bar");
        int noiseChannel=Array.FindIndex(song.Channels,c=>c.Type==ChannelType.Noise);
        app.StartEditChannel(noiseChannel,2);app.EffectPanelExpanded=true;window.PianoRoll.SelectedEffect=Note.EffectDutyCycle;
        capture(window,Path.Combine(output,"zho-gimmick-final-noise.ppm"));

        var goodNight=project.Songs.First(s=>s.Name.StartsWith("Good Night",StringComparison.OrdinalIgnoreCase));
        ShowChannel(goodNight,ChannelType.S5BSquare3,Note.EffectVolume);
        capture(window,Path.Combine(output,"zho-gimmick-final-good-night-s5b3.ppm"));
        ShowChannel(goodNight,ChannelType.Dpcm,Note.EffectDeltaCounter);
        capture(window,Path.Combine(output,"zho-gimmick-final-dpcm-dac.ppm"));

        var pulse=project.Instruments.First(i=>i.IsRegular&&i.ResetPhaseOnAttack&&i.PitchClampToTimerByte);
        app.StartEditInstrument(pulse,EnvelopeType.Pitch);ShowInstrument(pulse);
        Check(pulse.ResetPhaseOnAttack&&pulse.PitchClampToTimerByte,"real pulse instrument exposes both active articulation parameters");
        capture(window,Path.Combine(output,"zho-gimmick-final-pulse.ppm"));
        }
        finally { app.SelectedSong=initial;app.StartEditChannel(0,0); }
    }
}
