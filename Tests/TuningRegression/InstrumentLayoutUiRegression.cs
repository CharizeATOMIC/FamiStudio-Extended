using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FamiStudio;

// Exercises the actual compact instrument controls, including their touch path.
// The production model has no special test-only layout or edit entry points.
static class InstrumentLayoutUiRegression
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static object Field(object target, string name) => target.GetType().GetField(name, Private).GetValue(target);
    internal static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private).Invoke(target, args);
    internal static IEnumerable<Control> Descendants(Container container)
    {
        foreach (var control in container.Controls)
        {
            yield return control;
            if (control is Container nested)
                foreach (var descendant in Descendants(nested)) yield return descendant;
        }
    }
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS instrument layout UI: " + message);
    }
    internal static void Click(Control control, bool touch = false)
    {
        if (control == null) throw new Exception("Expected a reachable UI control.");
        int x = control.Width / 2, y = control.Height / 2;
        if (touch) control.SendTouchClick(new PointerEventArgs(x, y, 1.0f));
        else
        {
            control.SendPointerDown(new PointerEventArgs(PointerEventArgs.ButtonLeft, x, y));
            control.SendPointerUp(new PointerEventArgs(PointerEventArgs.ButtonLeft, x, y));
        }
    }
    internal static PanelContainer Panel(ProjectExplorer explorer, Instrument instrument) =>
        (PanelContainer)Invoke(explorer, "FindInstrumentPanel", instrument);
    internal static Button Tagged(ProjectExplorer explorer, string tag) =>
        Descendants(explorer).OfType<Button>().SingleOrDefault(b => Equals(b.UserData, tag));
    internal static Button HardwareMacro(ProjectExplorer explorer, int type) => Tagged(explorer,
        type == EnvelopeType.NoiseEnvelope ? "HardwareNoiseEnvelope" : "HardwareNoiseRestart");
    internal static string VisibleText(Button button) => (Field(button, "TextEvent") as Button.StringDelegate)?.Invoke(button) ?? button.Text;
    internal static void ExpandInstrument(ProjectExplorer explorer, Instrument instrument)
    {
        if (!ReferenceEquals(Field(explorer, "expandedInstrument"), instrument))
        {
            var expand = Panel(explorer, instrument).Controls.OfType<Button>()
                .Single(b => b.ImageName == "InstrumentExpand" || b.ImageName == "InstrumentExpanded");
            Click(expand, true);
        }
    }
    internal static void ExpandHardware(ProjectExplorer explorer, Instrument instrument)
    {
        ExpandInstrument(explorer, instrument);
        var expanded = (HashSet<int>)Field(explorer, "expandedHardwareInstruments");
        if (!expanded.Contains(instrument.Id)) Click(Tagged(explorer, "HardwareSettings"), true);
    }
    internal static ContextMenuOption[] Options(FamiStudioWindow window) =>
        (ContextMenuOption[])Field(window.ContextMenu, "menuOptions");
    internal static void Choose(FamiStudioWindow window, Func<ContextMenuOption, bool> predicate, bool touch = false)
    {
        var menu = window.ContextMenu;
        var options = Options(window);
        int index = Array.FindIndex(options, option => predicate(option));
        Check(index >= 0 && options.Count(predicate) == 1, "localized text menu option is unambiguous");
        int itemHeight = (int)Field(menu, "itemSizeY");
        int x = menu.Width / 2, y = index * itemHeight + itemHeight / 2;
        if (touch) menu.SendTouchClick(new PointerEventArgs(x, y, 1.0f));
        else
        {
            menu.SendPointerDown(new PointerEventArgs(PointerEventArgs.ButtonLeft, x, y));
            menu.SendPointerUp(new PointerEventArgs(PointerEventArgs.ButtonLeft, x, y));
        }
    }
    static string ParamLabel(string key) => typeof(InstrumentParamProvider).GetField(key, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null).ToString();
    static ParamInfo Info(ParamControl control) => (ParamInfo)typeof(ParamControl).GetField("param", Private).GetValue(control);
    static ParamControl Param(ProjectExplorer explorer, string key) => Descendants(explorer).OfType<ParamControl>().SingleOrDefault(c => Info(c).Name == ParamLabel(key));
    internal static void CheckCommonRow(ProjectExplorer explorer, Instrument instrument)
    {
        var panel = Panel(explorer, instrument);
        var buttons = panel.Controls.OfType<Button>().ToArray();
        string[] common = { "Volume", "Arpeggio", "Pitch", "DutyCycle" };
        Check(buttons.Count(b => b.UserData is string name && common.Contains(name)) == 4,
            "main row retains exactly the four ordinary envelope buttons");
        Check(buttons.Length == 7, "main row always has only the original instrument controls");
        Check(!buttons.Any(b => Equals(b.UserData, "HardwareNoise")), "hardware noise does not add another main-row icon");
        foreach (int type in new[] { EnvelopeType.NoisePeriod, EnvelopeType.NoiseEnvelope, EnvelopeType.NoiseEnvelopeRestart })
            Check(!buttons.Any(b => Equals(b.UserData, EnvelopeType.InternalNames[type])), "no duplicated hardware envelope icon " + type);
        var ordered = buttons.OrderBy(b => b.Left).ToArray();
        Check(ordered.All(b => b.Left >= 0 && b.Right <= panel.Width) &&
            ordered.Zip(ordered.Skip(1), (a, b) => a.Right <= b.Left).All(fits => fits), "instrument buttons fit their row without overlap");
    }

    public static void Run(FamiStudioWindow window, string language, string output, Action<FamiStudioWindow, string> capture)
    {
        var app = window.PianoRoll.App;
        var explorer = window.ProjectExplorer;
        var instrument = app.Project.CreateInstrument(ExpansionType.None, "Simple lead");
        app.SelectedInstrument = instrument;
        app.StartEditInstrument(instrument, EnvelopeType.Volume);
        explorer.RefreshButtons();
        Localization.LocalizeStatic(typeof(InstrumentParamProvider));
        uint initial = instrument.ComputeCRC();
        CheckCommonRow(explorer, instrument);
        ExpandInstrument(explorer, instrument);
        Check(Tagged(explorer, "HardwareSettings") != null && HardwareMacro(explorer, EnvelopeType.NoiseEnvelope) == null &&
            HardwareMacro(explorer, EnvelopeType.NoiseEnvelopeRestart) == null,
            language + " advanced controls start collapsed behind a visible header");
        Check(!Descendants(explorer).OfType<ParamControl>().Any(c => Info(c).Advanced), "ordinary instrument parameters are not crowded by advanced controls");
        Check(Tagged(explorer, "HardwareSettings").Text == NoiseEditorStrings.HardwareSettingsLabel.ToString(), "default instrument header has no active-setting badge");
        ((Container)Field(explorer, "mainContainer")).ScrollY = Math.Max(0, Panel(explorer, instrument).Top - DpiScaling.ScaleForWindow(48));
        capture(window, Path.Combine(output, language + "-instrument-common.ppm"));
        ExpandHardware(explorer, instrument);
        Check(instrument.ComputeCRC() == initial, "opening hardware settings does not alter instrument data");
        Check(Tagged(explorer, "NoiseSettings") == null, "hardware settings contain no intermediate noise submenu button");
        foreach (int type in new[] { EnvelopeType.NoiseEnvelope, EnvelopeType.NoiseEnvelopeRestart })
        {
            var button = HardwareMacro(explorer, type);
            Check(button != null && button.ImageName == null && VisibleText(button) == NoiseEditorStrings.UnusedMacroLabel.Format(EnvelopeType.LocalizedNames[type]),
                "empty macro has its own direct text button " + type);
        }
        Check(Tagged(explorer, "ConvertLegacyNoisePitch") == null, "ordinary instruments have no unused legacy conversion action");

        foreach (string key in new[] { "PulseSweepPeriodLabel", "PulseSweepUpLabel", "PulseSweepShiftLabel" })
            Check(Param(explorer, key) == null, "disabled sweep hides its dependent control " + key);
        capture(window, Path.Combine(output, language + "-instrument-hardware-compact.ppm"));
        Click(Param(explorer, "PulseSweepEnabledLabel"), true);
        Check((instrument.PulseSweepRegister & 0x80) != 0, "touch enables hardware sweep");
        foreach (string key in new[] { "PulseSweepPeriodLabel", "PulseSweepUpLabel", "PulseSweepShiftLabel" })
            Check(Param(explorer, key) != null, "enabling sweep reveals its dependent control " + key);
        Click(Tagged(explorer, "HardwareSettings"), true);
        Check(!Descendants(explorer).OfType<ParamControl>().Any(c => Info(c).Advanced) &&
            Tagged(explorer, "HardwareSettings").Text == NoiseEditorStrings.HardwareSettingsActiveLabel.Format(1),
            "collapsed header still reports the enabled setting");
        app.UndoRedoManager.Undo();
        Check(instrument.PulseSweepRegister == 0x08, "hardware sweep change can be undone after collapsing its controls");
        app.UndoRedoManager.Redo();
        Check((instrument.PulseSweepRegister & 0x80) != 0, "redo restores the actual hidden sweep setting");
        ExpandHardware(explorer, instrument);
        Check(Param(explorer, "PulseSweepShiftLabel") != null, "restored active sweep remains reachable");
        Click(Param(explorer, "PulseSweepEnabledLabel"));
        Check(instrument.PulseSweepRegister == 0x08 && Param(explorer, "PulseSweepShiftLabel") == null,
            "disabling sweep removes only its dependent controls");

        int originalWidth = explorer.Width, originalHeight = explorer.Height;
        try
        {
            foreach (int logicalWidth in new[] { 300, 240 })
            {
                explorer.Resize(DpiScaling.ScaleForWindow(logicalWidth), originalHeight);
                ExpandHardware(explorer, instrument);
                CheckCommonRow(explorer, instrument);
                foreach (string tag in new[] { "HardwareSettings", "HardwareNoiseEnvelope", "HardwareNoiseRestart" })
                {
                    var button = Tagged(explorer, tag);
                    Check(button != null && button.Width > 0 && button.Height >= DpiScaling.ScaleForWindow(20) &&
                        button.Left >= 0 && button.Right <= button.ParentContainer.Width, language + " " + tag + " fits a " + logicalWidth + "px panel");
                }
                foreach (var label in Descendants(explorer).OfType<Label>().Where(l => l.UserData is ParamControl))
                {
                    var control = (ParamControl)label.UserData;
                    Check(label.Width > 0 && label.Right <= control.Left && label.Ellipsis,
                        language + " parameter label stays outside its control at " + logicalWidth + "px: " + Info(control).Name);
                }
                foreach (int type in new[] { EnvelopeType.NoiseEnvelope, EnvelopeType.NoiseEnvelopeRestart })
                {
                    uint beforeOpen = instrument.ComputeCRC();
                    Click(HardwareMacro(explorer, type), true);
                    Check(!window.IsContextMenuActive && ReferenceEquals(Field(window.PianoRoll, "editInstrument"), instrument) &&
                        (int)Field(window.PianoRoll, "editEnvelope") == type && instrument.Envelopes[type].Length == 0 && instrument.ComputeCRC() == beforeOpen,
                        "one touch directly opens the correct empty macro without a menu or data changes: " + type);
                }
                CheckCommonRow(explorer, instrument);
                if (logicalWidth == 240) capture(window, Path.Combine(output, language + "-hardware-settings-narrow.ppm"));
            }
        }
        finally { explorer.Resize(originalWidth, originalHeight); }
        var saveAs = (Button)Field(window.ToolBar, "buttonSaveAs");
        Check(saveAs.ImageName == "SaveAs" && saveAs.Image != null && string.IsNullOrEmpty(saveAs.Text), language + " Save As has its own icon with a localized tooltip");
    }
}
