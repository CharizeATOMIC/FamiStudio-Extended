using System;
using System.IO;
using System.Reflection;
using FamiStudio;

static class CurveUiTests
{
    static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
    static int Dimension(ChipTuningCurve curve, string name) => (int)typeof(ChipTuningCurve).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(curve);
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS curve UI: " + message); }
    static void Click(Control control, int x, int y)
    {
        control.SendPointerDown(new PointerEventArgs(1, x, y));
        control.SendPointerUp(new PointerEventArgs(1, x, y));
    }
    static void Key(Control control, Keys key, ModifierKeys modifiers = default) => control.SendKeyDown(new KeyEventArgs(key, modifiers, false, 0));
    public static void Run(FamiStudioWindow window, string language, string output, Action<FamiStudioWindow,string> capture)
    {
        if (language == "zho")
        {
            var defaults = new ChipTuningSettings();
            var flatEditor = new ChipTuningDialog(window, defaults);
            flatEditor.ShowDialogAsync(_ => {});
            float first = (float)Invoke(flatEditor.Curve, "NoteValueY", 1, (double)defaults.GetValue(0, 1));
            float middle = (float)Invoke(flatEditor.Curve, "NoteValueY", 46, (double)defaults.GetValue(0, 46));
            float last = (float)Invoke(flatEditor.Curve, "NoteValueY", 96, (double)defaults.GetValue(0, 96));
            Check(first == middle && middle == last, "all default hardware values appear on the same zero row");
            capture(window, Path.Combine(output, "zho-curve-default.ppm"));
            flatEditor.Curve.ZoomPercent = 400;
            capture(window, Path.Combine(output, "zho-curve-default-zoom400.ppm"));
            flatEditor.Dialog.Close(DialogResult.Cancel);
        }
        var original = new ChipTuningSettings();
        for (int note = 37; note <= 60; note++) original.SetValue(0, note, original.GetDefaultValue(0, note) + (int)(Math.Sin(note * .6) * 18));
        int apuDefault = original.GetDefaultValue(0, 46), s5bDefault = original.GetDefaultValue(ExpansionType.S5B, 46);
        original.SetValue(0, 46, apuDefault + 3);
        original.SetValue(ExpansionType.S5B, 46, s5bDefault - 28);
        var editor = new ChipTuningDialog(window, original);
        editor.ShowDialogAsync(_ => {});
        var curve = editor.Curve;
        var props = editor.Dialog.Properties;
        capture(window, Path.Combine(output, language + "-curve.ppm"));
        if (language == "zho")
        {
            Check(curve.VisibleFirstNote == 1 && curve.VisibleLastNote == 96, "fit view contains all 96 piano keys");
            var beforeZoom = editor.Settings.Clone();
            props.SetPropertyValue(1, 400);
            Check(curve.ZoomPercent == 400 && curve.VisibleLastNote - curve.VisibleFirstNote < 26 && editor.Settings.ContentEquals(beforeZoom), "zoom changes viewport without changing hardware data");
            capture(window, Path.Combine(output, "zho-curve-zoom400.ppm"));
            var bar = curve.FindControlOfType<ScrollBar>();
            var positions = new object[] { 0, 0 };
            typeof(ScrollBar).GetMethod("GetScrollBarParams", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(bar, positions);
            int thumbMiddle = ((int)positions[0] + (int)positions[1]) / 2;
            bar.SendPointerDown(new PointerEventArgs(1, thumbMiddle, 2));
            bar.SendPointerMove(new PointerEventArgs(1, bar.Width - 2, 2));
            bar.SendPointerUp(new PointerEventArgs(1, bar.Width - 2, 2));
            Check(curve.VisibleLastNote == 96 && editor.Settings.ContentEquals(beforeZoom), "native scrollbar dragging reaches highest keys without editing values");
            curve.SelectedNote = 1;
            Check(curve.VisibleFirstNote == 1, "selected low note scrolls full keyboard back to its beginning");
            curve.SelectedNote = 46;
            Check(editor.Settings.GetValue(0, 46) == apuDefault + 3, "opening displays absolute hardware value");
            int x = (int)(float)Invoke(curve, "NoteX", 37);
            Click(curve, x, Dimension(curve, "KeyBottom") - 3);
            Check(curve.SelectedNote == 37, "white piano key selects editor note");
            x = (int)(float)Invoke(curve, "NoteX", 38);
            Click(curve, x, Dimension(curve, "KeyTop") + 5);
            Check(curve.SelectedNote == 38, "black piano key selects sharp note");
            curve.SelectedNote = 46;
            Check(editor.Settings.GetValue(0, 46) == apuDefault + 3, "selection preserves exact timer");
            var number = (NumericUpDown)NumericUiTests.ControlAt(props, 5);
            number.GrabDialogFocus(); number.ClearDialogFocus();
            Check(editor.Settings.GetValue(0, 46) == apuDefault + 3, "focus without typing preserves exact timer");
            x = (int)(float)Invoke(curve, "NoteX", 46);
            int originalY = (int)(float)Invoke(curve, "ValueY", (double)(apuDefault + 3));
            curve.SendPointerDown(new PointerEventArgs(1, x, originalY));
            curve.SendPointerMove(new PointerEventArgs(1, x + 2, originalY));
            curve.SendPointerUp(new PointerEventArgs(1, x + 2, originalY));
            Check(editor.Settings.GetValue(0, 46) == apuDefault + 3, "horizontal graph drag preserves exact timer");
            Key(curve, Keys.F2); Key(window.TopDialog, Keys.Enter);
            Check(editor.Settings.GetValue(0, 46) == apuDefault + 3, "unchanged popup confirmation preserves exact timer");
            number.GrabDialogFocus(); number.SelectAll();
            foreach (char character in (apuDefault - 12).ToString()) number.SendChar(new CharEventArgs(character, default));
            number.ClearDialogFocus();
            Check(editor.Settings.GetValue(0, 46) == apuDefault - 12, "typing sets fixed integer timer rather than an offset");
            x = (int)(float)Invoke(curve, "NoteX", 46);
            int y = (int)(float)Invoke(curve, "ValueY", (double)(apuDefault - 12));
            curve.SendPointerDown(new PointerEventArgs(1, x, y));
            Check(editor.Settings.GetValue(0, 46) == apuDefault - 12, "selecting a graph point does not change its timer");
            curve.SendPointerMove(new PointerEventArgs(1, x, y - 20));
            curve.SendPointerUp(new PointerEventArgs(1, x, y - 20));
            Check(editor.Settings.GetValue(0, 46) > apuDefault - 12 && editor.Settings.GetValue(0, 46) <= apuDefault + 50, "dragging raises timer within default plus fifty");
            Key(curve, Keys.Delete);
            Check(editor.Settings.GetValue(0, 46) == apuDefault && !editor.Settings.HasOverride(0, 46), "Delete restores default timer and clears override");
            Key(curve, Keys.Up, ModifierKeys.Shift);
            Check(editor.Settings.GetValue(0, 46) == apuDefault + 10, "Shift Up changes timer by ten units");
            props.SetDropDownListIndex(0, ExpansionType.S5B);
            Check(curve.Chip == ExpansionType.S5B && editor.Settings.GetValue(ExpansionType.S5B, 46) == s5bDefault - 28, "switching chips preserves independent hardware curves");
            props.SetDropDownListIndex(4, 95);
            Check(curve.SelectedNote == 96 && curve.VisibleLastNote == 96, "highest editor note scrolls into view");
            props.SetDropDownListIndex(4, 0);
            Check(curve.SelectedNote == 1 && curve.VisibleFirstNote == 1, "lowest editor note is reachable");
            curve.SelectedNote = 46;
            Key(curve, Keys.F2);
            NumericUiTests.SubmitPopup(window, (s5bDefault + 22).ToString());
            Check(editor.Settings.GetValue(ExpansionType.S5B, 46) == s5bDefault + 22, "F2 value entry sets absolute S5B period");
            Key(curve, Keys.F2); NumericUiTests.SubmitPopup(window, "1000");
            Check(editor.Settings.GetValue(ExpansionType.S5B, 46) == 1000, "manual entry is unrestricted by default plus or minus fifty");
            Key(curve, Keys.Up);
            Check(editor.Settings.GetValue(ExpansionType.S5B, 46) == 1001, "keyboard increment preserves a manually entered value outside the drag band");
            Key(curve, Keys.F2); NumericUiTests.SubmitPopup(window, "999999");
            Check(editor.Settings.GetValue(ExpansionType.S5B, 46) == editor.Settings.GetHardwareMaximumValue(ExpansionType.S5B, 46), "manual entry only clamps at hardware maximum");
            Key(curve, Keys.F2); NumericUiTests.SubmitPopup(window, "0");
            Check(editor.Settings.GetValue(ExpansionType.S5B, 46) == editor.Settings.GetHardwareMinimumValue(ExpansionType.S5B, 46), "manual entry only clamps at hardware minimum");
            Check(editor.Settings.GetValue(0, 46) == apuDefault + 10 && original.GetValue(0, 46) == apuDefault + 3, "chip edits and working copy are isolated");
            for (int chip = 0; chip < ExpansionType.Count; chip++)
            {
                props.SetDropDownListIndex(0, chip);
                int target = Math.Min(editor.Settings.GetDefaultValue(chip, 46) + 7, editor.Settings.GetMaximumValue(chip, 46));
                number.GrabDialogFocus(); number.SelectAll();
                foreach (char character in target.ToString()) number.SendChar(new CharEventArgs(character, default));
                number.ClearDialogFocus();
                Check(editor.Settings.GetValue(chip, 46) == target && number.Minimum == editor.Settings.GetHardwareMinimumValue(chip, 46) && number.Maximum == editor.Settings.GetHardwareMaximumValue(chip, 46), $"chip {chip} direct integer input uses its own hardware range");
                if (chip == ExpansionType.Vrc7 || chip == ExpansionType.N163 || chip == ExpansionType.EPSM)
                    capture(window, Path.Combine(output, $"zho-curve-chip{chip}.ppm"));
            }
            props.SetDropDownListIndex(0, 0);
            curve.SelectedNote = 43;
            int paintY = Dimension(curve, "GraphTop") + 10;
            curve.SendPointerDown(new PointerEventArgs(1, (int)(float)Invoke(curve, "NoteX", 43), paintY));
            curve.SendPointerMove(new PointerEventArgs(1, (int)(float)Invoke(curve, "NoteX", 45), paintY));
            curve.SendPointerUp(new PointerEventArgs(1, (int)(float)Invoke(curve, "NoteX", 45), paintY));
            Check(editor.Settings.GetValue(0, 44) > editor.Settings.GetDefaultValue(0, 44) && editor.Settings.GetValue(0, 44) <= editor.Settings.GetMaximumValue(0, 44) && curve.SelectedNote == 45, "arpeggio-cell painting edits intermediate notes within the fifty-unit drag band");
        }
        editor.Dialog.Close(DialogResult.Cancel);
        Check(original.GetValue(0, 46) == apuDefault + 3 && original.GetValue(ExpansionType.S5B, 46) == s5bDefault - 28, "cancel preserves original project curves");
        if (language == "zho")
        {
            var project = window.FamiStudio.Project;
            var before = project.ChipTuning.Clone();
            Invoke(window.ProjectExplorer, "EditProjectProperties");
            var outer = (MultiPropertyDialog)window.TopDialog;
            var page = outer.GetPropertyPage(0);
            Click(NumericUiTests.ControlAt(page, 6), 20, 10);
            Check(window.TopDialog is PropertyDialog, "curve editor opens from original project info page");
            var child = (PropertyDialog)window.TopDialog;
            int changed = child.Properties.GetPropertyValue<int>(5) + 15;
            child.Properties.SetPropertyValue(5, changed);
            child.Close(DialogResult.OK);
            Check(project.ChipTuning.ContentEquals(before), "accepting child waits for parent confirmation");
            outer.Close(DialogResult.Cancel);
            Check(project.ChipTuning.ContentEquals(before), "parent Cancel discards child curve changes");
            Invoke(window.ProjectExplorer, "EditProjectProperties");
            outer = (MultiPropertyDialog)window.TopDialog;
            Click(NumericUiTests.ControlAt(outer.GetPropertyPage(0), 6), 20, 10);
            child = (PropertyDialog)window.TopDialog;
            child.Properties.SetPropertyValue(5, changed);
            child.Close(DialogResult.OK); outer.Close(DialogResult.OK);
            Check(project.ChipTuning.GetValue(0, 46) == changed, "accepting project properties commits fixed hardware values");
            project.ChipTuning = before;
        }
    }
}
