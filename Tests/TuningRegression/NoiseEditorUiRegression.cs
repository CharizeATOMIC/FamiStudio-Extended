using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FamiStudio;

static class NoiseEditorUiRegression
{
    static object Field(object target, string name) => InstrumentLayoutUiRegression.Field(target, name);
    static object Invoke(object target, string name, params object[] args) => InstrumentLayoutUiRegression.Invoke(target, name, args);
    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        Console.WriteLine("PASS noise editor UI: " + message);
    }
    static Button MacroButton(ProjectExplorer explorer, Instrument instrument, int type) =>
        (Button)Invoke(explorer, "FindInstrumentEnvelopeButton", instrument, type);
    static bool Edits(FamiStudioWindow window, Instrument instrument, int type) => window.PianoRoll.IsEditingInstrument &&
        ReferenceEquals(Field(window.PianoRoll, "editInstrument"), instrument) && (int)Field(window.PianoRoll, "editEnvelope") == type;
    static void DrawValue(PianoRoll roll, int value)
    {
        int left = (int)Invoke(roll, "GetPixelXForAbsoluteNoteIndex", 0, true);
        int right = (int)Invoke(roll, "GetPixelXForAbsoluteNoteIndex", 1, true);
        int x = (int)Field(roll, "pianoSizeX") + (left + right) / 2;
        int topValue = 64 / (int)(float)Field(roll, "envelopeValueZoom") - 1;
        int y = (int)Field(roll, "headerAndEffectSizeY") + 1 +
            (int)((topValue - value + 0.5f) * (float)Field(roll, "envelopeValueSizeY")) - (int)Field(roll, "scrollY");
        roll.SendPointerDown(new PointerEventArgs(PointerEventArgs.ButtonLeft, x, y));
        roll.SendPointerUp(new PointerEventArgs(PointerEventArgs.ButtonLeft, x, y));
    }

    public static void Run(FamiStudioWindow window, string language, string output, Action<FamiStudioWindow, string> capture)
    {
        Localization.LocalizeStatic(typeof(NoiseEditorStrings));
        Localization.LocalizeStatic(typeof(EnvelopeType));
        foreach (var field in typeof(NoiseEditorStrings).GetFields(BindingFlags.Public | BindingFlags.Static))
            Check(!string.IsNullOrWhiteSpace(field.GetValue(null)?.ToString()), language + " translated " + field.Name);
        var app = window.PianoRoll.App;
        app.StopSong();
        InstrumentLayoutUiRegression.Run(window, language, output, capture);
        var inst = app.Project.CreateInstrument(ExpansionType.None, "Noise snare");
        inst.NoisePeriodEnvelope.Length = 4;
        inst.NoisePeriodEnvelope.Loop = 2;
        inst.NoisePeriodFollowsNote = true;
        inst.NoiseControlEnvelope.Length = 4;
        inst.NoiseRestartEnvelope.Length = 4;
        Array.Copy(new sbyte[] { 10, 7, 4, 2 }, inst.NoisePeriodEnvelope.Values, 4);
        Array.Copy(new sbyte[] { 63, 58, 52, 48 }, inst.NoiseControlEnvelope.Values, 4);
        Array.Copy(new sbyte[] { 2, 0, 0, 0 }, inst.NoiseRestartEnvelope.Values, 4);
        // A real placed C4 note makes the migration validate channel use too.
        var song = app.Project.CreateSong("Noise editor UI");
        var channel = song.Channels.Single(c => c.Type == ChannelType.Noise);
        channel.GetOrCreatePattern(0).Notes[0] = new Note(49) { Instrument = inst, Duration = 8 };
        app.SelectedSong = song;
        app.SelectedInstrument = inst;
        app.SelectedChannelIndex = Array.IndexOf(song.Channels, channel);
        app.SelectedArpeggio = null;
        var explorer = window.ProjectExplorer;
        explorer.RefreshButtons();
        InstrumentLayoutUiRegression.CheckCommonRow(explorer, inst);
        var row = InstrumentLayoutUiRegression.Panel(explorer, inst);
        row.SendPointerUp(new PointerEventArgs(PointerEventArgs.ButtonRight, row.Width / 2, row.Height / 2));
        Check(window.IsContextMenuActive && !InstrumentLayoutUiRegression.Options(window).Any(o => o.Text == NoiseEditorStrings.NoiseSettingsLabel.ToString()),
            "instrument context menu has no duplicate noise submenu entry");
        app.HideContextMenu();
        InstrumentLayoutUiRegression.ExpandHardware(explorer, inst);

        // An unselected row must edit its own instrument, not the currently
        // selected one. Test mouse and touch activation of the two text items.
        foreach (int type in new[] { EnvelopeType.NoiseEnvelope, EnvelopeType.NoiseEnvelopeRestart })
        {
            app.SelectedInstrument = app.Project.Instruments.First(i => i != inst);
            var button = InstrumentLayoutUiRegression.HardwareMacro(explorer, type);
            var conversion = InstrumentLayoutUiRegression.Tagged(explorer, "ConvertLegacyNoisePitch");
            Check(button != null && button.ImageName == null && InstrumentLayoutUiRegression.VisibleText(button) == NoiseEditorStrings.MacroStepsLabel.Format(EnvelopeType.LocalizedNames[type], 4),
                "populated macro appears directly in hardware settings as a text button " + type);
            Check(conversion != null && conversion.ImageName == null && InstrumentLayoutUiRegression.VisibleText(conversion) == NoiseEditorStrings.ConvertLegacyPitchLabel.ToString(),
                "legacy pitch has a direct conversion button alongside the two macros");
            InstrumentLayoutUiRegression.Click(button, type == EnvelopeType.NoiseEnvelopeRestart);
            Check(!window.IsContextMenuActive && Edits(window, inst, type) && ReferenceEquals(app.SelectedInstrument, inst) && ReferenceEquals(Field(explorer, "expandedInstrument"), inst),
                "one direct click selects and edits the originating instrument macro without a submenu " + type);
            Check(InstrumentLayoutUiRegression.HardwareMacro(explorer, EnvelopeType.NoiseEnvelope) != null &&
                InstrumentLayoutUiRegression.HardwareMacro(explorer, EnvelopeType.NoiseEnvelopeRestart) != null,
                "direct editing keeps both hardware controls visible");
            capture(window, Path.Combine(output, language + "-noise-macro-" + type + ".ppm"));
        }

        int normalWidth = explorer.Width;
        try
        {
            explorer.Resize(DpiScaling.ScaleForWindow(240), explorer.Height);
            InstrumentLayoutUiRegression.ExpandHardware(explorer, inst);
            var direct = new[] { "HardwareNoiseEnvelope", "HardwareNoiseRestart", "ConvertLegacyNoisePitch" }
                .Select(tag => InstrumentLayoutUiRegression.Tagged(explorer, tag)).ToArray();
            Check(direct.All(button => button != null && button.Width > 0 && button.Left >= 0 && button.Right <= button.ParentContainer.Width && button.Ellipsis) &&
                direct.Select(button => button.Top).Distinct().Count() == 3,
                language + " populated macros and legacy conversion each have a separate accessible row at 240px");
            InstrumentLayoutUiRegression.CheckCommonRow(explorer, inst);
        }
        finally { explorer.Resize(normalWidth, explorer.Height); }

        var roll = window.PianoRoll;
        app.StartEditInstrument(inst, EnvelopeType.NoiseEnvelope);
        DrawValue(roll, 40);
        Check(inst.NoiseControlEnvelope.Values[0] == 40, "mouse drawing changes the sounding hardware-control value");
        app.UndoRedoManager.Undo();
        Check(inst.NoiseControlEnvelope.Values[0] == 63, "hardware macro drawing supports undo");
        app.UndoRedoManager.Redo();
        Check(inst.NoiseControlEnvelope.Values[0] == 40, "hardware macro drawing supports redo");
        app.UndoRedoManager.Undo();

        InstrumentLayoutUiRegression.Click(MacroButton(explorer, inst, EnvelopeType.Arpeggio));
        Check(Edits(window, inst, EnvelopeType.Arpeggio) && NoiseEditorStrings.GetHint(inst, EnvelopeType.Arpeggio, true) == NoiseEditorStrings.LegacyPitchHint.ToString(),
            "ordinary arpeggio editor explains the remaining legacy pitch program");
        Check(MacroButton(explorer, inst, EnvelopeType.Arpeggio).ToolTip.Contains(NoiseEditorStrings.LegacyPitchHint.ToString()), "ordinary arpeggio button carries the legacy hint");
        app.SelectedChannelIndex = 0;
        Check(NoiseEditorStrings.GetHint(inst, EnvelopeType.Arpeggio, false) == NoiseEditorStrings.LegacyPitchHint.ToString(),
            "legacy arpeggio warning remains visible while a pulse channel is selected");
        roll.SendPointerUp(new PointerEventArgs(PointerEventArgs.ButtonRight,
            (int)Field(roll, "pianoSizeX") + 10, (int)Field(roll, "headerAndEffectSizeY") + 30));
        Check(window.IsContextMenuActive && InstrumentLayoutUiRegression.Options(window).Any(o => o.Text == NoiseEditorStrings.ConvertLegacyPitchLabel.ToString()),
            "legacy conversion remains reachable from the actual arpeggio context menu on a pulse channel");
        app.HideContextMenu();
        app.SelectedChannelIndex = Array.IndexOf(song.Channels, channel);
        app.StartEditInstrument(inst, EnvelopeType.NoisePeriod);
        Check(Edits(window, inst, EnvelopeType.Arpeggio), "an old workspace envelope selection opens the native arpeggio editor");
        uint beforeMigration = inst.ComputeCRC();
        InstrumentLayoutUiRegression.ExpandHardware(explorer, inst);
        InstrumentLayoutUiRegression.Click(InstrumentLayoutUiRegression.Tagged(explorer, "ConvertLegacyNoisePitch"), true);
        Check(inst.NoisePeriodEnvelope.Length == 0 && !inst.NoisePeriodFollowsNote && inst.ArpeggioEnvelope.Length == 4 && inst.ArpeggioEnvelope.Loop == 2 &&
            inst.ArpeggioEnvelope.Values.Take(4).SequenceEqual(new sbyte[] { 4, 7, 10, 12 }), "migration stores the native 14-minus-period arpeggio and preserves its loop");
        Check(!window.IsContextMenuActive && Edits(window, inst, EnvelopeType.Arpeggio) && ReferenceEquals(app.SelectedInstrument, inst), "direct migration opens the same instrument's native arpeggio without a submenu");
        Check(InstrumentLayoutUiRegression.Tagged(explorer, "ConvertLegacyNoisePitch") == null, "successful conversion removes its obsolete direct action");
        Check(NoiseEditorStrings.GetHint(inst, EnvelopeType.Arpeggio, true) == null &&
            !MacroButton(explorer, inst, EnvelopeType.Arpeggio).ToolTip.Contains(NoiseEditorStrings.LegacyPitchHint.ToString()), "migration immediately removes the obsolete legacy hint");
        app.UndoRedoManager.Undo();
        Check(inst.ComputeCRC() == beforeMigration && inst.NoisePeriodFollowsNote, "migration undo restores every original macro and mode");
        Check(InstrumentLayoutUiRegression.Tagged(explorer, "ConvertLegacyNoisePitch") != null, "migration undo restores the reachable conversion button");
        app.UndoRedoManager.Redo();
        Check(inst.NoisePeriodEnvelope.Length == 0 && inst.ArpeggioEnvelope.Values[0] == 4, "migration redo restores editable native pitch");
        explorer.RefreshButtons();
        InstrumentLayoutUiRegression.ExpandHardware(explorer, inst);
        ((Container)Field(explorer, "mainContainer")).ScrollY = Math.Max(0, InstrumentLayoutUiRegression.Panel(explorer, inst).Top - DpiScaling.ScaleForWindow(48));
        app.StartEditInstrument(inst, EnvelopeType.Arpeggio);
        capture(window, Path.Combine(output, language + "-noise-native-arpeggio.ppm"));
        Check(InstrumentLayoutUiRegression.Tagged(explorer, "ConvertLegacyNoisePitch") == null &&
            InstrumentLayoutUiRegression.HardwareMacro(explorer, EnvelopeType.NoiseEnvelope) != null &&
            InstrumentLayoutUiRegression.HardwareMacro(explorer, EnvelopeType.NoiseEnvelopeRestart) != null,
            "converted noise keeps both direct hardware controls and no intermediate submenu");
        capture(window, Path.Combine(output, language + "-noise-direct-controls.ppm"));
        InstrumentLayoutUiRegression.Click(InstrumentLayoutUiRegression.HardwareMacro(explorer, EnvelopeType.NoiseEnvelope));

        app.StartEditInstrument(inst, EnvelopeType.Volume);
        Check(NoiseEditorStrings.GetHint(inst, EnvelopeType.Volume, true) == NoiseEditorStrings.VolumeOverriddenHint.ToString(), "normal volume editor identifies its hardware override");
        Check(NoiseEditorStrings.GetHint(inst, EnvelopeType.Volume, false) == null, "pulse-channel volume is not incorrectly marked overridden");
        var originalControl = inst.NoiseControlEnvelope.Values.Take(4).ToArray();
        int menuX = (int)Field(roll, "pianoSizeX") + 10, menuY = (int)Field(roll, "headerAndEffectSizeY") + 30;
        roll.SendPointerUp(new PointerEventArgs(PointerEventArgs.ButtonRight, menuX, menuY));
        Check(window.IsContextMenuActive, "right-click opens the actual volume editor menu");
        InstrumentLayoutUiRegression.Choose(window, o => o.Text == NoiseEditorStrings.UseSoftwareVolumeLabel.ToString());
        Check(inst.NoiseControlEnvelope.Length == 0 && Edits(window, inst, EnvelopeType.Volume), "software-volume action disables the hardware override and opens the normal editor");
        Check(InstrumentLayoutUiRegression.VisibleText(InstrumentLayoutUiRegression.HardwareMacro(explorer, EnvelopeType.NoiseEnvelope)) ==
            NoiseEditorStrings.UnusedMacroLabel.Format(EnvelopeType.LocalizedNames[EnvelopeType.NoiseEnvelope]),
            "direct hardware button immediately describes the newly emptied macro");
        Check(NoiseEditorStrings.GetHint(inst, EnvelopeType.Volume, true) == null &&
            !MacroButton(explorer, inst, EnvelopeType.Volume).ToolTip.Contains(NoiseEditorStrings.VolumeOverriddenHint.ToString()), "software volume immediately updates the editor and project hints");
        app.UndoRedoManager.Undo();
        Check(inst.NoiseControlEnvelope.Length == 4 && inst.NoiseControlEnvelope.Values.Take(4).SequenceEqual(originalControl), "software-volume undo restores every original hardware value");
        Check(InstrumentLayoutUiRegression.VisibleText(InstrumentLayoutUiRegression.HardwareMacro(explorer, EnvelopeType.NoiseEnvelope)) ==
            NoiseEditorStrings.MacroStepsLabel.Format(EnvelopeType.LocalizedNames[EnvelopeType.NoiseEnvelope], 4),
            "undo immediately restores the direct button's macro length");
        explorer.RefreshButtons();

        string path = Path.Combine(output, language + "-editable-noise.fms");
        Check(new ProjectFile().Save(app.Project, path), "edited noise project saves");
        var restored = new ProjectFile().Load(path).GetInstrument(inst.Name);
        Check(restored.NoisePeriodEnvelope.Length == 0 && restored.ArpeggioEnvelope.Values.Take(4).SequenceEqual(new sbyte[] { 4, 7, 10, 12 }) &&
            restored.ArpeggioEnvelope.Loop == 2 && restored.NoiseControlEnvelope.Values.Take(4).SequenceEqual(originalControl) && restored.NoiseRestartEnvelope.Values[0] == 2,
            "native pitch and both actual hardware macros survive save and reopen");
    }
}
