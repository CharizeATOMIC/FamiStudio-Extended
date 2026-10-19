using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FamiStudio;

static class NoiseEditorUiRegression
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Field(object target, string name) => target.GetType().GetField(name, Private).GetValue(target);
    static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private).Invoke(target, args);
    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        Console.WriteLine("PASS noise editor UI: " + message);
    }

    public static void Run(FamiStudioWindow window, string language, string output, Action<FamiStudioWindow, string> capture)
    {
        Localization.LocalizeStatic(typeof(NoiseEditorStrings));
        Localization.LocalizeStatic(typeof(EnvelopeType));
        foreach (var field in typeof(NoiseEditorStrings).GetFields(BindingFlags.Public | BindingFlags.Static))
            Check(!string.IsNullOrWhiteSpace(field.GetValue(null)?.ToString()), language + " translated " + field.Name);
        var app = window.PianoRoll.App;
        app.StopSong();
        var inst = app.Project.CreateInstrument(ExpansionType.None, "Editable noise " + language);
        inst.NoisePeriodEnvelope.Length = 4;
        inst.NoiseControlEnvelope.Length = 4;
        inst.NoiseRestartEnvelope.Length = 4;
        Array.Copy(new sbyte[] { 10, 7, 4, 2 }, inst.NoisePeriodEnvelope.Values, 4);
        Array.Copy(new sbyte[] { 63, 58, 52, 48 }, inst.NoiseControlEnvelope.Values, 4);
        Array.Copy(new sbyte[] { 2, 0, 0, 0 }, inst.NoiseRestartEnvelope.Values, 4);
        app.SelectedInstrument = inst;
        app.SelectedChannelIndex = ChannelType.Noise;
        app.SelectedArpeggio = null;
        var explorer = window.ProjectExplorer;
        explorer.RefreshButtons();
        var panel = (PanelContainer)Invoke(explorer, "FindInstrumentPanel", inst);
        var positions = new System.Collections.Generic.HashSet<int>();
        foreach (int type in new[] { EnvelopeType.NoisePeriod, EnvelopeType.NoiseEnvelope, EnvelopeType.NoiseEnvelopeRestart })
        {
            var button = (Button)Invoke(explorer, "FindInstrumentEnvelopeButton", inst, type);
            Check(button != null && !button.Dimmed, language + " populated macro has a visible active button: " + type);
            Check(button.Left >= 0 && button.Right <= panel.Width && positions.Add(button.Left), "hardware macro buttons fit without overlapping");
            button.TriggerClick();
            Check(window.PianoRoll.IsEditingInstrument && (int)Field(window.PianoRoll, "editEnvelope") == type && ReferenceEquals(Field(window.PianoRoll, "editInstrument"), inst), "click edits the actual sounding macro " + type);
            capture(window, Path.Combine(output, language + "-noise-macro-" + type + ".ppm"));
        }

        var roll = window.PianoRoll;
        app.StartEditInstrument(inst, EnvelopeType.NoisePeriod);
        var original = inst.NoisePeriodEnvelope.Values[0];
        int left = (int)Invoke(roll, "GetPixelXForAbsoluteNoteIndex", 0, true);
        int right = (int)Invoke(roll, "GetPixelXForAbsoluteNoteIndex", 1, true);
        int x = (int)Field(roll, "pianoSizeX") + (left + right) / 2;
        int topValue = 64 / (int)(float)Field(roll, "envelopeValueZoom") - 1;
        int y = (int)Field(roll, "headerAndEffectSizeY") + 1 + (int)((topValue - 12 + 0.5f) * (float)Field(roll, "envelopeValueSizeY")) - (int)Field(roll, "scrollY");
        app.UndoRedoManager.BeginTransaction(TransactionScope.Instrument, inst.Id);
        Invoke(roll, "DrawEnvelope", x, y, true, true);
        Check(inst.NoisePeriodEnvelope.Values[0] == 12, "drawing changes the actual hardware-period value");
        app.UndoRedoManager.Undo();
        Check(inst.NoisePeriodEnvelope.Values[0] == original, "hardware-period drawing supports undo");
        app.UndoRedoManager.Redo();
        Check(inst.NoisePeriodEnvelope.Values[0] == 12, "hardware-period drawing supports redo");
        app.UndoRedoManager.Undo();

        app.StartEditInstrument(inst, EnvelopeType.Arpeggio);
        Check(NoiseEditorStrings.GetHint(inst, EnvelopeType.Arpeggio, true) == NoiseEditorStrings.ArpeggioOverriddenHint.ToString(), "fixed mode explains why ordinary arpeggio is overridden");
        Invoke(roll, "ToggleNoisePeriodFollowsNote");
        Check(inst.NoisePeriodFollowsNote && NoiseEditorStrings.GetHint(inst, EnvelopeType.Arpeggio, true) == null, "follow-keys action enables ordinary arpeggios and removes overridden hint");
        Check(((Button)Invoke(explorer, "FindInstrumentEnvelopeButton", inst, EnvelopeType.NoisePeriod)).ToolTip.Contains(NoiseEditorStrings.FollowingPeriodHint.ToString()), "project macro tooltip immediately follows the changed mode");
        Check(!((Button)Invoke(explorer, "FindInstrumentEnvelopeButton", inst, EnvelopeType.Arpeggio)).ToolTip.Contains(NoiseEditorStrings.ArpeggioOverriddenHint.ToString()), "project arpeggio tooltip immediately removes the old override");
        app.UndoRedoManager.Undo();
        Check(!inst.NoisePeriodFollowsNote, "follow-keys switch is undoable");
        app.UndoRedoManager.Redo();
        Check(inst.NoisePeriodFollowsNote, "follow-keys switch supports redo");

        app.StartEditInstrument(inst, EnvelopeType.Volume);
        Check(NoiseEditorStrings.GetHint(inst, EnvelopeType.Volume, true) == NoiseEditorStrings.VolumeOverriddenHint.ToString(), "normal volume editor identifies its hardware override");
        Check(NoiseEditorStrings.GetHint(inst, EnvelopeType.Volume, false) == null, "pulse-channel volume is not incorrectly marked overridden");
        capture(window, Path.Combine(output, language + "-noise-volume-help.ppm"));
        var originalControl = inst.NoiseControlEnvelope.Values.Take(4).ToArray();
        Invoke(roll, "UseSoftwareNoiseVolume");
        Check(inst.NoiseControlEnvelope.Length == 0 && (int)Field(roll, "editEnvelope") == EnvelopeType.Volume, "software-volume action disables the hardware override and opens the normal editor");
        Check(NoiseEditorStrings.GetHint(inst, EnvelopeType.Volume, true) == null, "normal volume becomes active after clearing hardware control");
        Check(!((Button)Invoke(explorer, "FindInstrumentEnvelopeButton", inst, EnvelopeType.Volume)).ToolTip.Contains(NoiseEditorStrings.VolumeOverriddenHint.ToString()), "project volume tooltip immediately reflects software volume");
        app.UndoRedoManager.Undo();
        Check(inst.NoiseControlEnvelope.Length == 4 && inst.NoiseControlEnvelope.Values.Take(4).SequenceEqual(originalControl), "switching to software volume can restore every original macro value with undo");
        explorer.RefreshButtons();
        app.StartEditInstrument(inst, EnvelopeType.NoisePeriod);
        capture(window, Path.Combine(output, language + "-noise-follow-keys.ppm"));

        string path = Path.Combine(output, language + "-editable-noise.fms");
        Check(new ProjectFile().Save(app.Project, path), "edited noise project saves");
        var restored = new ProjectFile().Load(path).GetInstrument(inst.Name);
        Check(restored.NoisePeriodFollowsNote && restored.NoisePeriodEnvelope.Values[0] == original && restored.NoiseControlEnvelope.Length == 4,
            "noise mode and actual editable macros survive save and reopen");
    }
}
