using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FamiStudio;

static class PcmSampleRateModelRegression
{
    sealed class LegacyWriter : ProjectSaveBuffer, ProjectBuffer
    {
        public LegacyWriter(Project project, ProjectBufferFlags flags = ProjectBufferFlags.None) : base(project, flags) { }
        int ProjectBuffer.Version => 1010;
    }
    static readonly List<string> checks = new();
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        checks.Add(message);
        Console.WriteLine("PASS PCM rate model: " + message);
    }
    static byte[] Snapshot(Project project, bool includeSamples = true)
    {
        var buffer = new ProjectSaveBuffer(project, ProjectBufferFlags.UndoRedo);
        project.Serialize(buffer, includeSamples);
        return buffer.GetBuffer();
    }
    static void Restore(Project project, byte[] data, bool includeSamples = true) =>
        project.Serialize(new ProjectLoadBuffer(project, data, Project.Version, ProjectBufferFlags.UndoRedo), includeSamples);

    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        checks.Clear();
        foreach (bool pal in new[] { false, true })
        {
            var project = IndependentPcmModelRegression.MakeProject();
            project.PalMode = pal;
            var sample = project.PcmSamples[0];
            sample.PalProcessing = pal;
            sample.Process();
            var source = (short[])sample.SourceWavData.Samples.Clone();
            var dpcm = (byte[])project.Samples[0].ProcessedData.Clone();
            var original = (byte[])sample.ProcessedData.Clone();
            var originalDuration = sample.ProcessedDuration;
            var originalClock = project.UpdateRateMultiplier;
            var label = pal ? "PAL" : "NTSC";

            foreach (int rate in new[] { 1, 2, 4, 8 })
            {
                project.PcmRateMultiplier = rate;
                var name = label + "-" + rate;
                Check(project.UpdateRateMultiplier == originalClock && project.Songs[0].LogicRateMultiplier == 1,
                    name + " does not change music or editing clocks");
                Check(sample.ProcessedData.SequenceEqual(sample.GetPcmData(pal)), name + " processed waveform uses selected rate");
                Check(Math.Abs(sample.ProcessedData.Length - original.Length * rate) <= rate, name + " PCM sample count follows selected clock");
                Check(Math.Abs(sample.ProcessedDuration - originalDuration) <= 1, name + " duration is preserved");
                Check(sample.SourceWavData.Samples.SequenceEqual(source) && project.Samples[0].ProcessedData.SequenceEqual(dpcm),
                    name + " source WAV and DPCM bytes are unchanged");
                Check(Math.Abs(sample.GetPcmPreviewWave(pal).SampleRate - Mmc5PcmTiming.GetSampleRate(pal, rate)) < .001,
                    name + " preview has the correct sample clock");
                var clone = project.DeepClone();
                Check(clone.PcmRateMultiplier == rate && clone.PcmSamples[0].ProcessedData.SequenceEqual(sample.ProcessedData), name + " clone retains rate and PCM");
                var binary = Path.Combine(output, name + ".fms");
                Check(new ProjectFile().Save(project, binary), name + " binary save");
                var loaded = new ProjectFile().Load(binary);
                Check(loaded != null && loaded.PcmRateMultiplier == rate && loaded.PcmSamples[0].ProcessedData.SequenceEqual(sample.ProcessedData),
                    name + " binary load applies rate before processing samples");
                var text = Path.ChangeExtension(binary, ".txt");
                Check(new FamistudioTextFile().Save(project, text, new[] { project.Songs[0].Id }, false), name + " text save");
                loaded = new FamistudioTextFile().Load(text);
                Check(loaded != null && loaded.PcmRateMultiplier == rate && loaded.PcmSamples[0].GetPcmData(pal).SequenceEqual(sample.GetPcmData(pal)),
                    name + " text load retains rate and raw-source PCM");
            }

            project.PcmRateMultiplier = 1;
            Check(sample.ProcessedData.SequenceEqual(original), label + " 4→8→16→32→4 kHz returns exact original PCM");
            var before = Snapshot(project, false);
            project.PcmRateMultiplier = 8;
            var high = (byte[])sample.ProcessedData.Clone();
            var after = Snapshot(project, false);
            Restore(project, before, false);
            Check(project.PcmRateMultiplier == 1 && ReferenceEquals(sample, project.PcmSamples[0]) && sample.ProcessedData.SequenceEqual(original),
                label + " ProjectNoDPCMSamples undo preserves sample identity and rebuilds low-rate data");
            Check(Math.Abs(sample.GetPcmPreviewWave(pal).SampleRate - Mmc5PcmTiming.GetSampleRate(pal)) < .001,
                label + " undo refreshes preview cache");
            Restore(project, after, false);
            Check(project.PcmRateMultiplier == 8 && sample.ProcessedData.SequenceEqual(high), label + " ProjectNoDPCMSamples redo rebuilds high-rate data");
            before = Snapshot(project);
            project.PcmRateMultiplier = 2;
            Restore(project, before);
            Check(project.PcmRateMultiplier == 8 && project.PcmSamples[0].ProcessedData.SequenceEqual(high), label + " complete project undo restores selected PCM rate");

            project.PcmRateMultiplier = 1;
            var legacy = new LegacyWriter(project);
            project.Serialize(legacy);
            var old = new Project { PcmRateMultiplier = 8 };
            old.Serialize(new ProjectLoadBuffer(old, legacy.GetBuffer(), 1010));
            Check(old.PcmRateMultiplier == 1 && old.PcmSamples[0].GetPcmData(pal).SequenceEqual(original), label + " real format-1010 layout defaults to exact legacy clock");
            var unchanged = Snapshot(project);
            foreach (int invalid in new[] { 0, -1, 3, 16 })
            {
                bool rejected = false;
                try { project.PcmRateMultiplier = invalid; } catch (ArgumentOutOfRangeException) { rejected = true; }
                Check(rejected && Snapshot(project).SequenceEqual(unchanged), label + " invalid multiplier " + invalid + " leaves project unchanged");
            }
        }

        var incoming = IndependentPcmModelRegression.MakeProject();
        incoming.PcmRateMultiplier = 8;
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var clip = new ProjectSaveBuffer(incoming, ProjectBufferFlags.Clipboard);
        typeof(ClipboardUtils).GetMethod("SaveSampleList", flags).Invoke(null,
            new object[] { clip, new HashSet<Instrument>(incoming.Instruments) });
        var oldClip = new LegacyWriter(incoming, ProjectBufferFlags.Clipboard);
        typeof(ClipboardUtils).GetMethod("SaveSampleList", flags).Invoke(null,
            new object[] { oldClip, new HashSet<Instrument>(incoming.Instruments) });
        Check(clip.GetBuffer().SequenceEqual(oldClip.GetBuffer()), "project-only setting leaves the format-1010 sample clipboard layout unchanged");
        foreach (int rate in new[] { 1, 2, 4, 8 })
        {
            var pasted = new Project { PcmRateMultiplier = rate };
            var reader = new ProjectLoadBuffer(pasted, clip.GetBuffer(), Project.Version, ProjectBufferFlags.Clipboard);
            typeof(ClipboardUtils).GetMethod("LoadAndMergeSampleList", flags).Invoke(null,
                new object[] { reader, false, ClipboardImportFlags.CreateMissing });
            Check(pasted.PcmRateMultiplier == rate && pasted.PcmSamples[0].SourceWavData.Samples.SequenceEqual(incoming.PcmSamples[0].SourceWavData.Samples),
                "clipboard keeps destination rate " + rate + " and exact source WAV");
            Check(pasted.PcmSamples[0].ProcessedData.SequenceEqual(incoming.PcmSamples[0].GetPcmData(false, sampleRate: Mmc5PcmTiming.GetSampleRate(false, rate))),
                "clipboard processes samples at destination rate " + rate);
            var target = new Project { PcmRateMultiplier = rate };
            target.SetExpansionAudioMask(incoming.ExpansionAudioMask, pcmEnabled: true);
            Check(target.MergeProject(incoming.DeepClone()), "merge accepts source at different PCM rate " + rate);
            Check(target.PcmRateMultiplier == rate && target.PcmSamples[0].ProcessedData.SequenceEqual(pasted.PcmSamples[0].ProcessedData),
                "merge reprocesses incoming samples at destination rate " + rate);
        }
        var invalidText = Path.Combine(output, "invalid-rate.txt");
        var textContent = File.ReadAllText(Path.Combine(output, "NTSC-8.txt"));
        File.WriteAllText(invalidText, textContent.Replace("PcmRateMultiplier=\"8\"", "PcmRateMultiplier=\"3\""));
        Check(new FamistudioTextFile().Load(invalidText) == null, "text loader rejects unsupported PCM multiplier");

        var empty = new Project();
        var lowBytes = Snapshot(empty);
        empty.PcmRateMultiplier = 8;
        var highBytes = Snapshot(empty);
        var offsets = Enumerable.Range(0, lowBytes.Length).Where(i => lowBytes[i] != highBytes[i]).ToArray();
        Check(offsets.Length == 1 && lowBytes[offsets[0]] == 1 && highBytes[offsets[0]] == 8,
            "empty project serialization stores exactly one independent PCM rate field");
        highBytes[offsets[0]] = 3;
        bool invalidBinaryRejected = false;
        try { Restore(new Project(), highBytes); } catch (ArgumentOutOfRangeException) { invalidBinaryRejected = true; }
        Check(invalidBinaryRejected, "binary loader validates the PCM multiplier before loading samples");

        // A tiny low-source-rate WAV produces a long, valid 4 kHz sample,
        // but exceeds the existing 16 MiB processed-sample limit at 32 kHz.
        // Put a normal sample first to exercise rollback after partial work.
        var failure = new Project();
        failure.AutoSortPcmSamples = false;
        var first = failure.CreatePcmSampleFromWavData("First", new short[] { 2000, -2000, 5000, -5000 }, 8000);
        var longSample = failure.CreatePcmSampleFromWavData("Long source", Enumerable.Repeat((short)16000, 600).ToArray(), 1);
        var firstBefore = (byte[])first.ProcessedData.Clone();
        var longBefore = (byte[])longSample.ProcessedData.Clone();
        bool sizeRejected = false;
        try { failure.PcmRateMultiplier = 8; }
        catch (InvalidOperationException ex) { sizeRejected = ex.Message.Contains("16 MiB"); }
        Check(sizeRejected && failure.PcmRateMultiplier == 1, "oversized high-rate PCM restores the original project rate");
        Check(first.ProcessedData.SequenceEqual(firstBefore) && longSample.ProcessedData.SequenceEqual(longBefore),
            "failed rate change restores every sample after partial processing");
        Check(first.GetPcmPreviewWave(false).SampleRate == Mmc5PcmTiming.GetSampleRate(false), "failed rate change restores the preview clock");
        File.WriteAllText(Path.Combine(output, "pcm-rate-model.json"), JsonSerializer.Serialize(new { passed = true, checks = checks.Count, results = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS PCM sample rate model: {checks.Count} checks");
    }
}
