using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FamiStudio;

static class IndependentPcmModelRegression
{
    static readonly List<string> checks = new();
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks.Add(message);
        Console.WriteLine("PASS " + message);
    }
    static void Reject(Action action, string message)
    {
        try { action(); } catch (ArgumentException) { Check(true, message); return; }
        throw new Exception(message);
    }

    public static Project MakeProject()
    {
        var p = new Project();
        p.Name = "Independent sample libraries";
        p.Author = "FamiStudio Extended";
        p.Copyright = "Original synthetic tones";
        p.SetExpansionAudioMask(ExpansionType.Mmc5Mask | ExpansionType.Vrc6Mask, pcmEnabled: true);
        var wave = Enumerable.Range(0, 2205).Select(i => (short)(22000 * Math.Sin(i * 2 * Math.PI * 330 / 22050))).ToArray();
        var dpcm = p.CreateDPCMSampleFromWavData("Tone", wave, 22050);
        var pcm = p.CreatePcmSampleFromWavData("Tone", wave, 22050);
        p.CreateFolder(FolderType.Sample, "Drums");
        p.CreateFolder(FolderType.PcmSample, "Drums");
        dpcm.FolderName = pcm.FolderName = "Drums";
        p.AutoSortPcmSamples = false;
        var apu = p.CreateInstrument(ExpansionType.None, "2A03 keys");
        apu.MapDPCMSample(37, dpcm, 14, true).DmcInitialValue = 65;
        apu.MapPcmSample(38, pcm, -12, true);
        var mmc5 = p.CreateInstrument(ExpansionType.Mmc5, "MMC5 keys");
        mmc5.MapPcmSample(37, pcm, 12);
        var vrc6 = p.CreateInstrument(ExpansionType.Vrc6, "VRC6 keys");
        vrc6.MapPcmSample(37, pcm);
        var song = p.CreateSong("Mixed keyboard mappings");
        song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        song.SetDefaultPatternLength(64);
        song.SetBeatLength(16);
        song.SetLength(1);
        song.SetLoopPoint(-1);
        var apuPattern = song.GetChannelByType(ChannelType.Dpcm).GetOrCreatePattern(0);
        apuPattern.Notes[0] = new Note(37) { Instrument = apu, Duration = 16 };
        apuPattern.Notes[16] = new Note(38) { Instrument = apu, Duration = 16 };
        song.GetChannelByType(ChannelType.Mmc5Pcm).GetOrCreatePattern(0).Notes[32] =
            new Note(37) { Instrument = mmc5, Duration = 16 };
        song.GetChannelByType(ChannelType.Vrc6Square1).GetOrCreatePattern(0).Notes[48] =
            new Note(37) { Instrument = vrc6, Duration = 16 };
        return p;
    }

    static void CheckLibraries(Project p, string context)
    {
        Check(p.Samples.Count == 1 && p.PcmSamples.Count == 1, context + ": separate collection sizes");
        var d = p.GetSample("Tone"); var c = p.GetPcmSample("Tone");
        Check(d != null && c != null && d.Id != c.Id && !d.IsPcm && c.IsPcm, context + ": same names remain separate assets");
        Check(!ReferenceEquals(d.SourceData, c.SourceData), context + ": independent source objects");
        Check(d.Folder.Type == FolderType.Sample && c.Folder.Type == FolderType.PcmSample, context + ": independent folders");
        var apu = p.GetInstrument("2A03 keys");
        Check(apu.GetDPCMMapping(37)?.Sample == d && apu.GetPcmMapping(38)?.Sample == c, context + ": mixed 2A03 keys");
        Check(apu.GetDPCMMapping(38) == null && apu.GetPcmMapping(37) == null, context + ": hardware mapping lookups cannot cross sample kinds");
        Check(p.GetInstrument("MMC5 keys").GetPcmMapping(37)?.Sample == c &&
            p.GetInstrument("VRC6 keys").GetPcmMapping(37)?.Sample == c, context + ": expansion PCM references");
    }

    public static void Run(string output, string legacyFile = null)
    {
        Directory.CreateDirectory(output);
        var p = MakeProject();
        CheckLibraries(p, "created");
        var dpcm = p.Samples[0]; var pcm = p.PcmSamples[0];
        var originalDpcm = (byte[])dpcm.ProcessedData.Clone();
        var originalPcm = (byte[])pcm.GetPcmData(false).Clone();
        Check(!ReferenceEquals(dpcm.SourceWavData.Samples, pcm.SourceWavData.Samples), "both libraries own separate source arrays");
        Check(pcm.ProcessedData.SequenceEqual(originalPcm), "PCM processing directly stores PCM bytes");
        Check(p.GetTotalSampleSize() == Math.Min(Project.MaxMappedSampleSize, dpcm.ProcessedData.Length), "DPCM memory accounting excludes PCM");
        Check(pcm.Bank == 0, "PCM has no DPCM bank assignment");
        p.AutoAssignSamplesBanks(Project.MaxMappedSampleSize, out _);
        Check(pcm.Bank == 0, "DPCM bank packing cannot change PCM");
        pcm.VolumeAdjust = 20; pcm.Process();
        Check(dpcm.ProcessedData.SequenceEqual(originalDpcm), "editing PCM leaves DPCM unchanged");
        Check(!pcm.GetPcmData(false).SequenceEqual(originalPcm), "PCM processing applies to its own data");
        pcm.VolumeAdjust = 100; pcm.Process();
        dpcm.VolumeAdjust = 35; dpcm.Process();
        Check(pcm.GetPcmData(false).SequenceEqual(originalPcm), "editing DPCM leaves PCM unchanged");
        dpcm.VolumeAdjust = 100; dpcm.Process();
        Reject(() => p.GetInstrument("MMC5 keys").MapDPCMSample(40, dpcm), "MMC5 rejects DPCM keys");
        Reject(() => p.GetInstrument("VRC6 keys").MapDPCMSample(40, dpcm), "VRC6 rejects DPCM keys");
        Reject(() => p.GetInstrument("2A03 keys").MapDPCMSample(40, pcm), "DPCM mapping rejects PCM asset");
        Reject(() => p.GetInstrument("2A03 keys").MapPcmSample(40, dpcm), "PCM mapping rejects DPCM asset");
        Reject(() => p.ReplaceSampleInAllMappings(dpcm, pcm), "global replacement cannot cross independent libraries");
        var converted = p.CreateInstrument(ExpansionType.Mmc5, "Converted PCM keys");
        InstrumentConverter.Convert(p.GetInstrument("2A03 keys"), converted);
        Check(converted.GetPcmMapping(38)?.Sample == pcm && !converted.HasAnyDpcmMappings,
            "instrument conversion retains compatible PCM keys without adding DPCM to MMC5");
        p.DeleteInstrument(converted);
        Check(!p.Songs[0].GetChannelByType(ChannelType.Mmc5Pcm).SupportsInstrument(p.GetInstrument("2A03 keys")), "MMC5 PCM accepts its own instrument type only");
        CheckLibraries(p.DeepClone(), "clone");
        var undo = new ProjectSaveBuffer(p, ProjectBufferFlags.UndoRedo);
        p.Serialize(undo);
        p.DeleteSample(pcm);
        Check(p.PcmSamples.Count == 0 && p.Samples.Count == 1 && p.GetInstrument("2A03 keys").GetDPCMMapping(37) != null, "PCM deletion preserves DPCM asset and mapping");
        p.Serialize(new ProjectLoadBuffer(p, undo.GetBuffer(), Project.Version, ProjectBufferFlags.UndoRedo));
        CheckLibraries(p, "undo");
        Check(!p.AutoSortPcmSamples, "independent PCM sort preference survives undo");
        var binary = Path.Combine(output, "Independent-PCM.fms");
        Check(new ProjectFile().Save(p, binary), "binary save");
        CheckLibraries(new ProjectFile().Load(binary), "binary");
        var text = Path.Combine(output, "Independent-PCM.txt");
        Check(new FamistudioTextFile().Save(p, text, new[] { p.Songs[0].Id }, false), "text save");
        var loaded = new FamistudioTextFile().Load(text);
        CheckLibraries(loaded, "text");
        Check(loaded.PcmSamples[0].GetPcmData(false).SequenceEqual(originalPcm), "text PCM bytes are exact");
        Check(loaded.Samples[0].ProcessedData.SequenceEqual(originalDpcm), "text DPCM bytes are exact");
        var invalidText = Path.Combine(output, "invalid-mmc5-dpcm.txt");
        var content = File.ReadAllText(text);
        var marker = content.IndexOf("Instrument Name=\"MMC5 keys\"");
        Check(marker >= 0, "text contains MMC5 instrument");
        var mappingStart = content.IndexOf("PCMMapping", marker);
        content = content.Remove(mappingStart, 10).Insert(mappingStart, "DPCMMapping");
        File.WriteAllText(invalidText, content);
        Check(new FamistudioTextFile().Load(invalidText) == null, "text loader rejects DPCM mappings on MMC5 instead of silently dropping them");
        var clean = p.DeepClone();
        clean.UnmapUnusedSamples(); clean.DeleteUnmappedSamples();
        CheckLibraries(clean, "cleanup");
        clean.TransposeDPCMMapping(37, 40, clean.GetInstrument("VRC6 keys"));
        Check(clean.Songs[0].GetChannelByType(ChannelType.Vrc6Square1).PatternInstances[0].Notes[48].Value == 40,
            "VRC6 PCM key relocation updates score references");
        var duplicate = p.DuplicateDPCMSample(p.PcmSamples[0]);
        Check(duplicate.IsPcm && p.PcmSamples.Contains(duplicate) && !p.Samples.Contains(duplicate), "duplicate remains in PCM library");
        Check(!ReferenceEquals(duplicate.SourceWavData.Samples, p.GetPcmSample("Tone").SourceWavData.Samples), "duplicate has independent waveform storage");
        p.DeleteSample(duplicate);

        // Exercise actual clipboard serialization without touching the user's OS clipboard.
        var clip = new ProjectSaveBuffer(p, ProjectBufferFlags.Clipboard);
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        typeof(ClipboardUtils).GetMethod("SaveSampleList", flags).Invoke(null,
            new object[] { clip, new HashSet<Instrument>(p.Instruments) });
        var pasted = new Project();
        var read = new ProjectLoadBuffer(pasted, clip.GetBuffer(), Project.Version, ProjectBufferFlags.Clipboard);
        typeof(ClipboardUtils).GetMethod("LoadAndMergeSampleList", flags).Invoke(null,
            new object[] { read, false, ClipboardImportFlags.CreateMissing });
        Check(pasted.Samples.Count == 1 && pasted.PcmSamples.Count == 1, "clipboard keeps same-name PCM and DPCM assets distinct");
        Check(pasted.PcmSamples[0].SourceWavData.Samples.SequenceEqual(p.PcmSamples[0].SourceWavData.Samples), "clipboard retains PCM source samples");

        var mixedChips = p.DeepClone();
        mixedChips.SetExpansionAudioMask(mixedChips.ExpansionAudioMask | ExpansionType.FdsMask, pcmEnabled: true);
        var fds = mixedChips.CreateInstrument(ExpansionType.Fds, "Wave instrument");
        var mixedClipboard = new ProjectSaveBuffer(mixedChips, ProjectBufferFlags.Clipboard);
        typeof(ClipboardUtils).GetMethod("SaveInstrumentList", flags).Invoke(null,
            new object[] { mixedClipboard, new[] { mixedChips.GetInstrument("2A03 keys"), fds } });
        var mixedRead = new ProjectLoadBuffer(mixedChips, mixedClipboard.GetBuffer(), Project.Version, ProjectBufferFlags.Clipboard);
        typeof(ClipboardUtils).GetMethod("LoadAndMergeInstrumentList", flags).Invoke(null,
            new object[] { mixedRead, true, ClipboardImportFlags.CreateMissing });
        Check(fds.SamplesMapping == null && mixedChips.GetInstrument("2A03 keys").HasAnyDpcmMappings,
            "clipboard preflight across APU and FDS cannot inherit temporary instrument mappings");

        var mergeTarget = new Project();
        mergeTarget.SetExpansionAudioMask(ExpansionType.Mmc5Mask | ExpansionType.Vrc6Mask, pcmEnabled: true);
        mergeTarget.CreateDPCMSampleFromDmcData("Tone", new byte[] { 0x55, 0xaa });
        Check(mergeTarget.MergeProject(p.DeepClone()), "merge of independent sample project");
        Check(mergeTarget.Samples.Count == 1 && mergeTarget.PcmSamples.Count == 1 &&
            mergeTarget.PcmSamples[0].SourceWavData.Samples.SequenceEqual(p.PcmSamples[0].SourceWavData.Samples),
            "merge cannot match a PCM source to a same-name DPCM source");
        if (legacyFile != null)
        {
            var legacy = new ProjectFile().Load(legacyFile);
            Check(legacy != null && legacy.Mmc5PcmEnabled && legacy.PcmSamples.Count > 0, "R11 shared library automatically migrates");
            foreach (var channel in legacy.Songs.SelectMany(s => s.Channels).Where(c => c.IsMmc5PcmChannel))
            foreach (var note in channel.Patterns.SelectMany(pat => pat.Notes.Values).Where(n => n.IsMusical))
                Check(note.Instrument?.IsMmc5 == true && note.Instrument.GetPcmMapping(note.Value) != null, "R11 MMC5 notes retain mapped PCM instruments");
            Check(legacy.PcmSamples[0].GetPcmData(false).SequenceEqual(legacy.Samples[0].GetPcmData(false)), "R11 migration preserves exact source-derived PCM bytes");
            Check(legacy.PcmSamples[0].GetPcmData(true).SequenceEqual(legacy.Samples[0].GetPcmData(true)), "R11 migration preserves PAL PCM bytes");
            var savedLegacy = Path.Combine(output, "Migrated-R11.fms");
            Check(new ProjectFile().Save(legacy, savedLegacy) && new ProjectFile().Load(savedLegacy).PcmSamples.Count == legacy.PcmSamples.Count,
                "migration is stable after saving and reopening");
            var legacyText = Path.ChangeExtension(legacyFile, ".txt");
            if (File.Exists(legacyText))
            {
                var importedLegacyText = new FamistudioTextFile().Load(legacyText);
                Check(importedLegacyText != null && importedLegacyText.PcmSamples.Count == legacy.PcmSamples.Count &&
                    importedLegacyText.PcmSamples[0].GetPcmData(false).SequenceEqual(legacy.PcmSamples[0].GetPcmData(false)),
                    "R11 shared text projects migrate without losing MMC5 notes or sample data");
            }
        }
        File.WriteAllText(Path.Combine(output, "independent-pcm-model.json"),
            JsonSerializer.Serialize(new { passed = true, checks = checks.Count, results = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS independent PCM model: {checks.Count} checks");
    }
}
