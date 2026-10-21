using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using FamiStudio;

static class PcmLibrariesUiRegression
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Field(object obj, string name) => obj.GetType().GetField(name, Private).GetValue(obj);
    static object Invoke(object obj, string name, params object[] args) =>
        obj.GetType().GetMethod(name, Private).Invoke(obj, args);
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS independent PCM UI: " + message);
    }
    static Control PropertyControl(PropertyPage page, int index)
    {
        var properties = (IList)Field(page, "properties");
        return (Control)properties[index].GetType().GetField("control").GetValue(properties[index]);
    }
    static void ChangeSelection(PropertyPage page, int index, int selected)
    {
        page.SetDropDownListIndex(index, selected);
        Invoke(page, "DropDown_SelectedIndexChanged", PropertyControl(page, index), selected);
    }

    public static void Run(FamiStudioWindow window, string language, string output, Action<FamiStudioWindow, string> capture)
    {
        var app = window.FamiStudio;
        var explorer = window.ProjectExplorer;
        var project = app.Project;
        var headers = InstrumentLayoutUiRegression.Descendants(explorer).OfType<PanelContainer>().ToArray();
        Check(headers.Count(p => Equals(p.UserData, "DPCMSamples")) == 1 &&
            headers.Count(p => Equals(p.UserData, "PCMSamples")) == 1, "two distinct sample-library sections");
        Check(InstrumentLayoutUiRegression.Tagged(explorer, "ImportDpcmSamples") != null &&
            InstrumentLayoutUiRegression.Tagged(explorer, "ImportPcmSamples") != null, "each library has its own import control");

        // Exercise the same WAV import handler reached from those controls.
        string wav = Path.Combine(output, "Library sound.wav");
        var source = Enumerable.Range(0, 22050 * 3).Select(i => (short)(Math.Sin(i * 0.11) * 16000)).ToArray();
        WaveFile.Save(source, wav, 22050, 1);
        int dpcmCount = project.Samples.Count, pcmCount = project.PcmSamples.Count;
        Invoke(explorer, "ImportSamples", new[] { wav }, true);
        var pcm = project.PcmSamples.Single(s => s.Name == "Library sound");
        Check(project.Samples.Count == dpcmCount && project.PcmSamples.Count == pcmCount + 1 &&
            pcm.SourceWavData.Samples.SequenceEqual(source), "PCM WAV import stays separate and retains more than two seconds");
        Invoke(explorer, "ImportSamples", new[] { wav }, false);
        Check(window.TopDialog is PropertyDialog importLog && importLog.Title == "Log",
            "the original DPCM duration limit still reports its truncation warning");
        window.TopDialog.Close(DialogResult.OK);
        Check(window.TopDialog == null, "the import warning closes before further UI interactions");
        var dpcm = project.Samples.Single(s => s.Name == pcm.Name);
        Check(pcm.IsPcm && !dpcm.IsPcm && pcm.Id != dpcm.Id &&
            dpcm.SourceWavData.Samples.Length == 22050 * 2, "independent libraries permit equal names and preserve existing DPCM limits");
        int pcmId = pcm.Id, dpcmId = dpcm.Id;
        app.UndoRedoManager.Undo();
        Check(project.GetSample(dpcmId) == null && project.GetSample(pcmId)?.IsPcm == true,
            "undoing DPCM import does not remove the PCM sample");
        app.UndoRedoManager.Redo();
        pcm = project.GetSample(pcmId);
        dpcm = project.GetSample(dpcmId);
        Check(dpcm != null && !dpcm.IsPcm, "redo restores the DPCM sample in its own library");

        // Rename through the real property dialog. An equal name in the other
        // library is not a collision and the undo must retain the library flag.
        Invoke(explorer, "EditDPCMSampleProperties", pcm, false);
        var properties = (PropertyDialog)window.TopDialog;
        ((TextBox)PropertyControl(properties.Properties, 0)).Text = "PCM renamed";
        properties.Close(DialogResult.OK);
        Check(project.GetPcmSample("PCM renamed")?.Id == pcmId &&
            project.GetSample("Library sound")?.Id == dpcmId, "PCM rename cannot rename the identically named DPCM sample");
        app.UndoRedoManager.Undo();
        Check(project.GetPcmSample("Library sound")?.Id == pcmId, "PCM rename undo restores its original library and name");
        pcm = project.GetSample(pcmId);

        var pcmParams = DPCMSampleParamProvider.GetParams(pcm);
        var dpcmParams = DPCMSampleParamProvider.GetParams(dpcm);
        Check(pcmParams.Length == 3 && dpcmParams.Length == 10,
            "PCM processing shows volume, fine tuning and trim without DPCM encoding/bank controls");

        app.UndoRedoManager.BeginTransaction(TransactionScope.Project, TransactionFlags.StopAudio | TransactionFlags.RecreatePlayers);
        project.SetExpansionAudioMask(project.ExpansionAudioMask | ExpansionType.Vrc6Mask, pcmEnabled: true);
        var apu = project.CreateInstrument(ExpansionType.None, "APU sample keys");
        var mmc5 = project.CreateInstrument(ExpansionType.Mmc5, "MMC5 sample keys");
        var vrc6 = project.CreateInstrument(ExpansionType.Vrc6, "VRC6 sample keys");
        apu.MapDPCMSample(37, dpcm);
        var pcmMapping = apu.MapPcmSample(49, pcm, 7, true);
        mmc5.MapPcmSample(49, pcm, -5, true);
        vrc6.MapPcmSample(49, pcm, 12, false);
        app.UndoRedoManager.EndTransaction();
        Invoke(explorer, "RecreateAllControls");
        foreach (var instrument in new[] { apu, mmc5, vrc6 })
        {
            var panel = InstrumentLayoutUiRegression.Panel(explorer, instrument);
            var keyButton = panel.Controls.OfType<Button>().Single(b => Equals(b.UserData, "DPCM"));
            InstrumentLayoutUiRegression.Click(keyButton, true);
            Check(app.IsEditingDPCMSampleMapping && window.PianoRoll.EditInstrument == instrument,
                instrument.Expansion + " has one directly reachable sample-key editor");
        }

        // APU's single key editor contains both kinds and can assign either.
        app.StartEditDPCMMapping(apu);
        Invoke(window.PianoRoll, "MapDPCMSample", (byte)61);
        var assign = (PropertyDialog)window.TopDialog;
        var available = project.AllSamples.Where(s => s.IsPcm ? apu.SupportsPcmMapping : apu.SupportsDpcmMapping).ToList();
        ChangeSelection(assign.Properties, 0, available.FindIndex(s => s.Id == pcmId));
        Check(!InstrumentLayoutUiRegression.Descendants(assign).Contains(PropertyControl(assign.Properties, 1)) &&
            InstrumentLayoutUiRegression.Descendants(assign).Contains(PropertyControl(assign.Properties, 3)),
            "choosing PCM shows only its transpose instead of DPCM rate");
        assign.Properties.SetPropertyValue(3, -9);
        assign.Close(DialogResult.OK);
        Check(apu.GetPcmMapping(61)?.PcmPitch == -9 && apu.GetDPCMMapping(61) == null,
            "one APU key receives one PCM mapping");
        app.UndoRedoManager.Undo();
        Check(project.GetInstrument(apu.Id).GetSampleMapping(61) == null, "key assignment supports undo");
        app.UndoRedoManager.Redo();
        apu = project.GetInstrument(apu.Id);
        Check(apu.GetPcmMapping(61)?.PcmPitch == -9, "key assignment supports redo");

        app.StartEditDPCMMapping(apu);
        Invoke(window.PianoRoll, "EditDPCMSampleMappingProperties", new Point(160, 150), apu.GetPcmMapping(49));
        var mappingDialog = (PropertyDialog)window.TopDialog;
        Check(mappingDialog.Properties.GetPropertyValue<int>(4) == 7 &&
            !InstrumentLayoutUiRegression.Descendants(mappingDialog).Contains(PropertyControl(mappingDialog.Properties, 0)) &&
            !InstrumentLayoutUiRegression.Descendants(mappingDialog).Contains(PropertyControl(mappingDialog.Properties, 3)),
            "PCM key properties follow the mapped asset rather than the selected APU channel");
        capture(window, Path.Combine(output, language + "-independent-pcm-key.ppm"));
        mappingDialog.Close(DialogResult.Cancel);
        capture(window, Path.Combine(output, language + "-independent-pcm-libraries.ppm"));

        // Copying mappings preserves parameters; no silent reset of PCM pitch
        // or the older full seven-bit DPCM initial DAC is permitted.
        var sourceMapping = apu.GetDPCMMapping(37);
        sourceMapping.Sample.Bank = 3;
        sourceMapping.DmcInitialValue = 117;
        sourceMapping.OverrideDmcInitialValue = true;
        typeof(PianoRoll).GetMethod("CopySampleMapping", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { apu, 38, sourceMapping });
        Check(apu.GetDPCMMapping(38).DmcInitialValue == 117 && apu.GetDPCMMapping(38).OverrideDmcInitialValue && apu.GetDPCMMapping(38).Sample.Bank == 3,
            "mapping drag/copy preserves the full DPCM DAC override and sample bank");
        typeof(ProjectExplorer).GetMethod("CopySampleMapping", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { project.GetInstrument(vrc6.Id), 50, apu.GetPcmMapping(49) });
        vrc6 = project.GetInstrument(vrc6.Id);
        Check(vrc6.GetPcmMapping(50).PcmPitch == 7 && vrc6.GetPcmMapping(50).Loop,
            "mapping copy preserves PCM transpose and loop across supported instruments");
    }
}
