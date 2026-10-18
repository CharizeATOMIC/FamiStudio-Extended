using System;
using System.Collections;
using System.Reflection;
using FamiStudio;

static class NumericUiTests
{
    static object Field(object obj, string name) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
    public static Control ControlAt(PropertyPage page, int index)
    {
        var property = ((IList)Field(page, "properties"))[index];
        return (Control)property.GetType().GetField("control").GetValue(property);
    }
    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception("Numeric UI: " + message);
        Console.WriteLine("PASS numeric UI: " + message);
    }
    static void Click(Control control, int x, int y)
    {
        control.SendPointerDown(new PointerEventArgs(1, x, y));
        control.SendPointerUp(new PointerEventArgs(1, x, y));
    }
    static KeyEventArgs Key(Keys key, ModifierKeys modifiers = default) => new KeyEventArgs(key, modifiers, false, 0);
    static void Type(TextBox field, string text)
    {
        field.SelectAll();
        foreach (char c in text) field.SendChar(new CharEventArgs(c, default));
    }
    public static void SubmitPopup(FamiStudioWindow window, string text, bool cancel = false)
    {
        var popup = (PropertyDialog)window.TopDialog;
        var field = (NumericUpDown)ControlAt(popup.Properties, 0);
        Check(field.HasDialogFocus, "value popup starts with keyboard focus");
        Type(field, text);
        popup.SendKeyDown(Key(cancel ? Keys.Escape : Keys.Enter));
        Check(window.TopDialog != popup, cancel ? "one Escape cancels input" : "Enter commits input");
    }
    public static void Run(FamiStudioWindow window)
    {
        var dialog = new PropertyDialog(window, "Numeric interaction test", 500);
        var page = dialog.Properties;
        page.AddNumericUpDown("A4", 440, 300, 580, 1);
        page.AddGrid("Cents", new[] { new ColumnDesc("Note", .4f), new ColumnDesc("Cents", .6f, -1200, 1200) }, new object[,] { { "C", 0 }, { "C#", 0 } }, 3);
        page.Build(); dialog.ShowDialogAsync(_ => {});
        var number = (NumericUpDown)ControlAt(page, 0);
        int changed = 0;
        number.ValueChanged += (_, _) => changed++;
        number.GrabDialogFocus();
        Type(number, "432"); number.ClearDialogFocus();
        Check(number.Value == 432 && changed == 1, "typed reference commits and emits one change");
        number.GrabDialogFocus(); Type(number, "9999"); number.ClearDialogFocus();
        Check(number.Value == 580, "typed reference clamps to maximum");
        number.GrabDialogFocus(); Type(number, "100"); number.ClearDialogFocus();
        Check(number.Value == 300, "typed reference clamps to minimum");
        number.Value = 440;
        number.SendKeyDown(Key(Keys.Up, ModifierKeys.Shift));
        Check(number.Value == 450, "Shift Up increases by ten");
        Click(number, DpiScaling.ScaleForWindow(25), number.Height / 2);
        Check(number.Value == 449, "wide minus button accepts its outer area");
        Click(number, number.Width - DpiScaling.ScaleForWindow(25), number.Height / 2);
        Check(number.Value == 450, "wide plus button accepts its outer area");
        number.SendPointerDown(new PointerEventArgs(1, number.Width - 4, number.Height / 2));
        number.Tick(.51f); number.Tick(.06f);
        number.SendPointerUp(new PointerEventArgs(1, number.Width - 4, number.Height / 2));
        Check(number.Value == 453, "holding plus repeats after initial delay");
        number.SendPointerDown(new PointerEventArgs(2, number.Width - 4, number.Height / 2));
        Check(number.Value == 453, "right click does not increment");
        number.Enabled = false; number.SendKeyDown(Key(Keys.Up));
        Check(number.Value == 453, "disabled numeric field ignores arrow keys");
        number.Enabled = true;
        var grid = (Grid)ControlAt(page, 1);
        grid.Move(grid.Left + 17, grid.Top);
        var offsets = (int[])Field(grid, "columnOffsets");
        var widths = (int[])Field(grid, "columnWidths");
        int rowHeight = (int)Field(grid, "rowHeight"), y = rowHeight + rowHeight / 2;
        Check(rowHeight >= DpiScaling.ScaleForWindow(28), "numeric table has taller hit targets");
        Click(grid, offsets[1] + DpiScaling.ScaleForWindow(25), y);
        Check(page.GetPropertyValue<int>(1, 0, 1) == -1, "table minus uses local coordinates and wider target");
        Click(grid, offsets[1] + widths[1] - DpiScaling.ScaleForWindow(25), y);
        Check(page.GetPropertyValue<int>(1, 0, 1) == 0, "table plus uses wider target");
        Click(grid, offsets[1] + widths[1] / 2, y);
        Check(window.TopDialog != dialog && page.GetPropertyValue<int>(1, 0, 1) == 0, "center opens input without changing value");
        SubmitPopup(window, "-37");
        Check(page.GetPropertyValue<int>(1, 0, 1) == -37, "negative cents input commits");
        Click(grid, offsets[1] + widths[1] / 2, y);
        SubmitPopup(window, "99", true);
        Check(page.GetPropertyValue<int>(1, 0, 1) == -37, "cancel preserves cents");
        grid.GrabDialogFocus(); grid.UpdateSelectedRow(0); grid.SendKeyDown(Key(Keys.F2));
        SubmitPopup(window, "9999");
        Check(page.GetPropertyValue<int>(1, 0, 1) == 1200, "F2 input clamps cents to maximum");
        grid.SetCellEnabled(0, 1, false);
        Click(grid, offsets[1] + widths[1] / 2, y);
        Click(grid, offsets[1] + 2, y);
        Check(window.TopDialog == dialog && page.GetPropertyValue<int>(1, 0, 1) == 1200, "disabled cell ignores clicks");
        dialog.Close(DialogResult.Cancel);
    }
    public static void Expansion(FamiStudioWindow window, Grid grid, PropertyPage page)
    {
        var parent = window.TopDialog;
        var offsets = (int[])Field(grid, "columnOffsets");
        var widths = (int[])Field(grid, "columnWidths");
        int rowHeight = (int)Field(grid, "rowHeight"), y = rowHeight * (int)Field(grid, "numHeaderRows") + rowHeight / 2;
        Click(grid, offsets[2] + widths[2] / 2, y);
        SubmitPopup(window, "3");
        Check(page.GetPropertyValue<int>(0, 0, 2) == 3, "expansion quantity accepts typing");
        Click(grid, widths[0] / 2, y);
        Click(grid, offsets[2] + widths[2] / 2, y);
        Check(window.TopDialog == parent && !page.GetPropertyValue<bool>(0, 0, 0), "unchecked chip quantity cannot be edited");
        Click(grid, widths[0] / 2, y);
        grid.UpdateData(0, 2, 2);
    }
}
