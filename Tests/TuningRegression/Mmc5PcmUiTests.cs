using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FamiStudio;

static class Mmc5PcmUiTests
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS PCM UI: " + message);
    }

    public static void Run(FamiStudioWindow window, string language, string output, Action<FamiStudioWindow, string> capture)
    {
        var app = window.FamiStudio;
        // Force localization before inspecting the static fields. Missing text
        // must fail the UI check even if all controls are still clickable.
        _ = Mmc5PcmStrings.EnableLabel;
        foreach (var field in typeof(Mmc5PcmStrings).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var label = field.GetValue(null)?.ToString();
            Check(!string.IsNullOrWhiteSpace(label) && !label.Contains("### MISSING ###"),
                "localized PCM string: " + field.Name);
        }
        Check(app.Project.Mmc5PcmEnabled, "isolated PCM fixture loaded");
        typeof(ProjectExplorer).GetMethod("EditProjectProperties", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(window.ProjectExplorer, null);
        var dialog = (MultiPropertyDialog)window.TopDialog;
        typeof(MultiPropertyDialog).GetMethod("SetSelectedTab", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(dialog, new object[] { 1 });
        var page = dialog.GetPropertyPage(1);
        Check(page.GetPropertyValue<bool>(2), "PCM option is in existing expansion settings");
        capture(window, Path.Combine(output, language + "-pcm-expansion.ppm"));
        page.SetPropertyValue(2, false);
        dialog.Close(DialogResult.OK);
        Check(!app.Project.Mmc5PcmEnabled && app.SelectedSong.Channels.Length == 7, "accept removes PCM and recreates playback layout");
        app.UndoRedoManager.Undo();
        Check(app.Project.Mmc5PcmEnabled && app.SelectedSong.Channels.Length == 8, "undo restores the PCM layout and notes");
        var instrument = app.Project.Instruments.First(i => i.IsMmc5 && i.HasAnyPcmMappings);
        app.SelectedInstrument = instrument;
        app.SelectedChannelIndex = Array.FindIndex(app.SelectedSong.Channels, c => c.Type == ChannelType.Mmc5Pcm);
        app.StartEditDPCMMapping(instrument);
        var mapping = instrument.GetPcmMapping(49);
        typeof(PianoRoll).GetMethod("EditDPCMSampleMappingProperties", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(window.PianoRoll, new object[] { new Point(160, 150), mapping });
        var mappingDialog = (PropertyDialog)window.TopDialog;
        Check(mappingDialog.Properties.GetPropertyValue<int>(4) == 12, "PCM transpose appears beside existing sample settings");
        capture(window, Path.Combine(output, language + "-pcm-mapping.ppm"));
        mappingDialog.Properties.SetPropertyValue(4, -7);
        mappingDialog.Close(DialogResult.OK);
        Check(mapping.PcmPitch == -7 && mapping.Pitch == 15, "PCM transpose is independent of DPCM pitch");
        app.PlayInstrumentNote(49, false, false);
        app.StopInstrument();
        app.UndoRedoManager.Undo();
        Check(app.Project.GetInstrument(instrument.Id).GetPcmMapping(49).PcmPitch == 12, "PCM mapping transpose supports undo");
        Mmc5PcmPreviewRegression.RunUi(window, language, output, capture);
        PcmLibrariesUiRegression.Run(window, language, output, capture);
    }
}
