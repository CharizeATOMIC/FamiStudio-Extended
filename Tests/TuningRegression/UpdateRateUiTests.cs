using System;
using System.IO;
using System.Reflection;
using FamiStudio;

static class UpdateRateUiTests
{
    static void Invoke(object target,string name,params object[] values)=>target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(target,values);
    static void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS update clock UI: "+text);}
    public static void Run(FamiStudioWindow window,string language,string output,Action<FamiStudioWindow,string> capture)
    {
        var app=window.PianoRoll.App;app.StopSong();
        var instrument=app.Project.CreateInstrument(0,"Clock UI");int id=instrument.Id;
        instrument.VolumeEnvelope.Length=4;instrument.VolumeEnvelope.Values[0]=15;instrument.VolumeEnvelope.Values[1]=12;instrument.VolumeEnvelope.Values[2]=8;instrument.VolumeEnvelope.Values[3]=0;
        int originalSongRate=app.SelectedSong.LogicRateMultiplier;float bpm=app.SelectedSong.BPM;
        Invoke(window.ProjectExplorer,"EditProjectProperties");
        var project=(MultiPropertyDialog)window.TopDialog;var page=project.GetPropertyPage(0);
        page.SetDropDownListIndex(7,2);
        capture(window,Path.Combine(output,language+"-output-clock.ppm"));project.Close(DialogResult.OK);
        Check(app.Project.UpdateRateMultiplier==4&&app.SelectedSong.LogicRateMultiplier==originalSongRate&&app.SelectedSong.BPM==bpm,"project dropdown raises output without changing existing song tempo");
        app.UndoRedoManager.Undo();Check(app.Project.UpdateRateMultiplier==1&&app.SelectedSong.BPM==bpm,"project undo preserves original clock");
        app.UndoRedoManager.Redo();Check(app.Project.UpdateRateMultiplier==4&&app.SelectedSong.BPM==bpm,"project redo preserves original clock");
        Invoke(window.ProjectExplorer,"EditSongProperties",app.SelectedSong,false);
        capture(window,Path.Combine(output,language+"-song-data-clock.ppm"));window.TopDialog.Close(DialogResult.Cancel);
        PropertyDialog OpenEnvelope()
        {
            instrument=app.Project.GetInstrument(id);app.SelectedInstrument=instrument;
            window.PianoRoll.StartEditInstrument(instrument,EnvelopeType.Volume);
            Invoke(window.PianoRoll,"EditEnvelopeClockProperties");return (PropertyDialog)window.TopDialog;
        }
        var dialog=OpenEnvelope();Check(dialog.Properties.GetSelectedIndex(0)==0,"old macro explicitly displays its original rate");
        dialog.Properties.SetDropDownListIndex(0,2);
        capture(window,Path.Combine(output,language+"-envelope-clock.ppm"));dialog.Close(DialogResult.OK);
        Check(instrument.VolumeEnvelope.TickRateMultiplier==4&&instrument.VolumeEnvelope.Length==16,"macro dropdown resamples time without speeding the envelope");
        app.UndoRedoManager.Undo();instrument=app.Project.GetInstrument(id);
        Check(instrument.VolumeEnvelope.TickRateMultiplier==1&&instrument.VolumeEnvelope.Length==4,"macro undo restores clock and values");
        app.UndoRedoManager.Redo();instrument=app.Project.GetInstrument(id);
        Check(instrument.VolumeEnvelope.TickRateMultiplier==4&&instrument.VolumeEnvelope.Length==16,"macro redo restores converted steps");
        dialog=OpenEnvelope();dialog.Properties.SetDropDownListIndex(0,1);dialog.Close(DialogResult.Cancel);
        Check(instrument.VolumeEnvelope.TickRateMultiplier==4&&instrument.VolumeEnvelope.Length==16,"macro cancel preserves the resource");
        var lower=new ProjectPropertiesDialog(window,app.Project);lower.EditProjectPropertiesAsync(_=>{});lower.InfoPage.SetDropDownListIndex(7,0);
        Check(lower.UpdateRateMultiplier==4,"output dropdown refuses data it cannot represent");window.TopDialog.Close(DialogResult.Cancel);
        dialog=OpenEnvelope();dialog.Properties.SetDropDownListIndex(0,0);dialog.Close(DialogResult.OK);
        Check(instrument.VolumeEnvelope.TickRateMultiplier==1&&instrument.VolumeEnvelope.Length==4,"exact macro downgrade preserves its original duration");
        window.PianoRoll.StartEditInstrument(instrument,EnvelopeType.Volume);
        capture(window,Path.Combine(output,language+"-envelope-clock-visible.ppm"));
        app.StartEditChannel(0,0);
    }
}
