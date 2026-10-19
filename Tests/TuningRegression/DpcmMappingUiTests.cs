using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FamiStudio;

static class DpcmMappingUiTests
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS DPCM mapping UI: " + message);
    }
    static void Type(NumericUpDown field, string value)
    {
        field.GrabDialogFocus(); field.SelectAll();
        foreach (var c in value) field.SendChar(new CharEventArgs(c, default));
        field.ClearDialogFocus();
    }
    public static void Run(FamiStudioWindow window, string language, string output, Action<FamiStudioWindow,string> capture)
    {
        var app = window.PianoRoll.App;
        var sample = app.Project.CreateDPCMSampleFromDmcData("DPCM UI test", Enumerable.Repeat((byte)0xaa, 17).ToArray());
        var instrument = app.Project.CreateInstrument(ExpansionType.None, "DPCM UI test");
        var mapping = instrument.MapDPCMSample(37, sample, 15, false);
        mapping.OverrideDmcInitialValue = true; mapping.DmcInitialValue = 85;
        int instrumentId = instrument.Id;
        PropertyDialog Open()
        {
            instrument = app.Project.GetInstrument(instrumentId);
            mapping = instrument.GetDPCMMapping(37);
            app.SelectedInstrument = instrument;
            window.PianoRoll.StartEditDPCMMapping(instrument);
            typeof(PianoRoll).GetMethod("EditDPCMSampleMappingProperties", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window.PianoRoll, new object[] { new Point(160, 150), mapping });
            return (PropertyDialog)window.TopDialog;
        }
        var dialog = Open();
        var number = (NumericUpDown)NumericUiTests.ControlAt(dialog.Properties, 3);
        Check(number.Value == 85, "dialog displays the full odd hardware value");
        Type(number, "999"); Check(number.Value == 127, "manual entry reaches the hardware maximum 127");
        Type(number, "-1"); Check(number.Value == 0, "manual entry clamps at zero");
        Type(number, "117"); Check(number.Value == 117, "manual entry preserves odd values");
        capture(window, Path.Combine(output, language + "-dpcm-mapping.ppm"));
        dialog.Close(DialogResult.OK);
        Check(mapping.DmcInitialValue == 117, "accept commits the exact value");
        app.UndoRedoManager.Undo();
        Check(app.Project.GetInstrument(instrumentId).GetDPCMMapping(37).DmcInitialValue == 85, "undo restores the original odd value");
        app.UndoRedoManager.Redo();
        Check(app.Project.GetInstrument(instrumentId).GetDPCMMapping(37).DmcInitialValue == 117, "redo restores the edited odd value");
        dialog = Open();
        Type((NumericUpDown)NumericUiTests.ControlAt(dialog.Properties, 3), "42");
        dialog.Close(DialogResult.Cancel);
        Check(mapping.DmcInitialValue == 117, "cancel preserves the mapping");
        app.StartEditChannel(0, 0);
    }
}
