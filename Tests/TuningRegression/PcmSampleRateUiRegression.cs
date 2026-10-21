using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FamiStudio;

static class PcmSampleRateUiRegression
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Field(object target, string name) => target.GetType().GetField(name, Private).GetValue(target);
    static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private).Invoke(target, args);
    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        Console.WriteLine("PASS PCM rate UI: " + message);
    }
    static Control PropertyControl(PropertyPage page, int index)
    {
        var entries = (IList)Field(page, "properties");
        return (Control)entries[index].GetType().GetField("control").GetValue(entries[index]);
    }
    static void Select(PropertyPage page, int index, int selected)
    {
        page.SetDropDownListIndex(index, selected);
        Invoke(page, "DropDown_SelectedIndexChanged", PropertyControl(page, index), selected);
    }
    static void ScrollBottom(MultiPropertyDialog dialog, int index)
    {
        var tabs = (IList)Field(dialog, "tabs");
        var container = (Container)tabs[index].GetType().GetField("container").GetValue(tabs[index]);
        if (container is ScrollContainer scroll) scroll.ScrollY = Math.Max(0, scroll.VirtualSizeY - scroll.Height);
    }

    public static void Run(FamiStudioWindow window, string language, string output, Action<FamiStudioWindow, string> capture)
    {
        var app = window.FamiStudio;
        _ = Mmc5PcmStrings.SampleRateLabel;
        foreach (var f in typeof(Mmc5PcmStrings).GetFields(BindingFlags.Public | BindingFlags.Static))
            Check(!string.IsNullOrWhiteSpace(f.GetValue(null)?.ToString()) && !f.GetValue(null).ToString().Contains("### MISSING ###"), "localized " + f.Name);
        Check(app.Project.PcmRateMultiplier == 1 && app.Project.UsesPcm, "isolated 4 kHz PCM fixture");
        var sampleId = app.Project.PcmSamples[0].Id;
        var original = app.Project.GetSample(sampleId).ProcessedData.ToArray();
        var source = app.Project.GetSample(sampleId).SourceWavData.Samples.ToArray();
        int originalMusicRate = app.Project.UpdateRateMultiplier;

        Invoke(window.ProjectExplorer, "EditProjectProperties");
        var dialog = (MultiPropertyDialog)window.TopDialog;
        var page = dialog.GetPropertyPage(0);
        Check(((string[])Field(PropertyControl(page, 8), "items")).SequenceEqual(new[] { "4 kHz", "8 kHz", "16 kHz", "32 kHz" }), "four PCM rates in existing project Information page");
        Select(page, 8, 2);
        Check(((Label)PropertyControl(page, 9)).Text.Contains(Mmc5PcmStrings.SampleRateHighHint.Value), "higher rate shows performance and hardware export notice");
        ScrollBottom(dialog, 0);
        capture(window, Path.Combine(output, language + "-pcm-rate-project.ppm"));
        dialog.Close(DialogResult.Cancel);
        Check(app.Project.PcmRateMultiplier == 1 && app.Project.GetSample(sampleId).ProcessedData.SequenceEqual(original), "cancel preserves sample data and project clock");

        Invoke(window.ProjectExplorer, "EditProjectProperties");
        dialog = (MultiPropertyDialog)window.TopDialog;
        Select(dialog.GetPropertyPage(0), 8, 2);
        dialog.Close(DialogResult.OK);
        Check(app.Project.PcmRateMultiplier == 4 && app.Project.GetSample(sampleId).ProcessedData.Length >= original.Length * 4 - 4, "accept resamples original WAV to 16 kHz");
        Check(app.Project.UpdateRateMultiplier == originalMusicRate && app.Project.GetSample(sampleId).SourceWavData.Samples.SequenceEqual(source), "music grid and source WAV remain unchanged");
        app.UndoRedoManager.Undo();
        Check(app.Project.PcmRateMultiplier == 1 && app.Project.GetSample(sampleId).ProcessedData.SequenceEqual(original), "undo restores PCM rate and processed data");
        app.UndoRedoManager.Redo();
        Check(app.Project.PcmRateMultiplier == 4, "redo restores higher rate");

        var export = new ExportDialog(window);
        export.ShowDialogAsync();
        dialog = (MultiPropertyDialog)window.TopDialog;
        var indices = (Dictionary<int, int>)Field(export, "pcmRateProperties");
        var formatType = typeof(ExportDialog).GetNestedType("ExportFormat", BindingFlags.NonPublic);
        foreach (var kv in indices)
        {
            page = dialog.GetPropertyPage(kv.Key);
            var options = (string[])Field(PropertyControl(page, kv.Value), "items");
            Check(options.SequenceEqual(new[] { Mmc5PcmStrings.ExportPcmRateSelect.Value, Mmc5PcmStrings.ExportPcmRate4k.Value }), "hardware export offers only explicit compatible 4 kHz: " + kv.Key);
            object[] args = { Enum.ToObject(formatType, kv.Key), null };
            Check(!(bool)Invoke(export, "TryGetPcmExportRate", args), "high rate has no implicit hardware fallback: " + kv.Key);
            Select(page, kv.Value, 1);
            Check((bool)Invoke(export, "TryGetPcmExportRate", args) && (int?)args[1] == 4000, "explicit 4 kHz selection accepted: " + kv.Key);
            Invoke(dialog, "SetSelectedTab", kv.Key);
            ScrollBottom(dialog, kv.Key);
            capture(window, Path.Combine(output, language + "-pcm-rate-export-" + kv.Key + ".ppm"));
            Invoke(export, "ResetPcmRateSelection", Enum.ToObject(formatType, kv.Key));
            Check(page.GetSelectedIndex(kv.Value) == 0, "reset clears explicit resampling choice");
        }
        Check(indices.Count == 3, "NSF, NES and sound-engine export covered");
        dialog.Close(DialogResult.Cancel);
        Check(app.Project.PcmRateMultiplier == 4 && app.Project.GetSample(sampleId).SourceWavData.Samples.SequenceEqual(source), "export UI preserves high-rate project and original sample");
    }
}
