using System;
using System.IO;
using System.Reflection;
using FamiStudio;

static class LoopVariantUiTests
{
    static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS loop variant UI: " + message); }
    public static void InspectExisting(FamiStudioWindow window, string language, string output, Action<FamiStudioWindow,string> capture)
    {
        var app=window.PianoRoll.App; var initial=app.SelectedSong;
        foreach(var song in app.Project.Songs)
        for(int c=0;c<song.Channels.Length;c++)
        for(int p=0;p<song.Length;p++)
        {
            var owner=song.Channels[c].PatternInstances[p];
            if(owner?.LoopVariant==null) continue;
            app.SelectedSong=song;
            app.StartEditLoopVariant(c,p);
            Check(ReferenceEquals(Invoke(window.PianoRoll,"GetDisplayedPattern",song.Channels[c],p),owner.LoopVariant),"loaded Gimmick variation selects original editable data");
            capture(window,Path.Combine(output,language+"-gimmick-loop-variation.ppm"));
            app.SelectedSong=initial;
            app.StartEditChannel(0,0);
            return;
        }
    }
    public static void Run(FamiStudioWindow window, string language, string output, Action<FamiStudioWindow,string> capture)
    {
        var app = window.PianoRoll.App;
        var fixture = new Project();
        var song = fixture.CreateSong("First and repeated passes");
        song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        song.SetBeatLength(4);
        song.SetLength(2); song.SetDefaultPatternLength(16); song.SetLoopPoint(0);
        var instrument = fixture.CreateInstrument(ExpansionType.None, "Editable tone");
        var channel = song.Channels[0];
        var first = channel.GetOrCreatePattern(0);
        first.Notes[0] = new Note(41) { Instrument = instrument, Duration = 8 };
        var variant = channel.CreatePattern("Repeated phrase");
        variant.Notes[0] = new Note(49) { Instrument = instrument, Duration = 6 };
        variant.PlaybackDelay = 2; first.LoopVariant = variant;
        var shared = channel.GetOrCreatePattern(1);
        shared.Notes[0] = new Note(46) { Instrument = instrument, Duration = 4 };
        string path = Path.Combine(output, language + "-loop-variant-edit.fms");
        Check(new ProjectFile().Save(fixture, path), "create fixture");
        Invoke(app, "UnloadProject");
        Invoke(app, "OpenProjectInternal", path);
        window.RunEventLoop();
        var piano = window.PianoRoll;
        channel = app.SelectedSong.Channels[0];
        first = channel.PatternInstances[0]; variant = first.LoopVariant;
        app.StartEditLoopVariant(0, 0);
        Check(piano.EditingLoopVariants, "context-menu entry activates repeated-loop view");
        Check(ReferenceEquals(Invoke(piano, "GetDisplayedPattern", channel, 0), variant), "display selects editable variant object");
        Check(ReferenceEquals(Invoke(piano, "GetDisplayedPattern", channel, 1), channel.PatternInstances[1]), "unchanged phrase remains shared without creating a variation");
        var view = (Channel)Invoke(piano, "GetDisplayedChannel", channel);
        Check(view.GetNoteAt(new NoteLocation(0, 0)).Value == 49 && channel.GetNoteAt(new NoteLocation(0, 0)).Value == 41, "hit testing and iterators use variation while original timeline stays intact");
        var note = variant.Notes[0];
        Invoke(piano, "ToggleNoteAttack", new NoteLocation(0, 0), note);
        Check(!variant.Notes[0].HasAttack && first.Notes[0].HasAttack, "real note editing command changes variation only");
        app.UndoRedoManager.Undo();
        Check(piano.EditingLoopVariants && first.LoopVariant.Notes[0].HasAttack && first.Notes[0].HasAttack, "undo restores variation and editing view");
        app.UndoRedoManager.Redo();
        Check(piano.EditingLoopVariants && !first.LoopVariant.Notes[0].HasAttack && first.Notes[0].HasAttack, "redo restores variation edit only");
        capture(window, Path.Combine(output, language + "-loop-variant-view.ppm"));
        app.StartEditChannel(0, 0);
        Check(!piano.EditingLoopVariants && ReferenceEquals(Invoke(piano, "GetDisplayedPattern", channel, 0), first), "normal pattern opening restores first-pass view");
        Check(new ProjectFile().Save(app.Project, path), "save edited variation");
        Invoke(app, "UnloadProject");
        Invoke(app, "OpenProjectInternal", path);
        window.RunEventLoop();
        app.StartEditLoopVariant(0, 0);
        first = app.SelectedSong.Channels[0].PatternInstances[0];
        Check(piano.EditingLoopVariants && first.Notes[0].HasAttack && !first.LoopVariant.Notes[0].HasAttack && first.LoopVariant.PlaybackDelay == 2, "actual reopen preserves edited variation, delay and original notes");
        app.StartEditChannel(0, 0);
    }
}
