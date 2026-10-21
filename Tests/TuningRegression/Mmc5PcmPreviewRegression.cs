using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FamiStudio;

static class Mmc5PcmPreviewRegression
{
    static readonly List<string> checks = new();

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks.Add(message);
        Console.WriteLine("PASS PCM preview: " + message);
    }

    static void VerifyWave(DPCMSample sample, bool pal, string context)
    {
        var bytes = sample.GetPcmData(pal);
        var wave = sample.GetPcmPreviewWave(pal);
        Check(wave.SampleRate == Mmc5PcmTiming.GetSampleRate(pal), context + ": exact hardware sample rate");
        Check(wave.Samples.Length == bytes.Length &&
            wave.Samples.Select((v, i) => v == (short)((bytes[i] - 128) << 8)).All(v => v),
            context + ": every displayed sample equals the actual 8-bit DAC data");
        Check(ReferenceEquals(wave, sample.GetPcmPreviewWave(pal)), context + ": unchanged waveform reuses cache");

        var audio = wave.GetPlaybackSamples();
        double rate = Mmc5PcmPreviewWave.PlaybackSampleRate;
        Check(Math.Abs(audio.Length / rate - bytes.Length / wave.SampleRate) <= 0.5 / rate + 1e-12,
            context + ": preview duration follows fractional hardware rate to the nearest output sample");
        Check(ReferenceEquals(audio, wave.GetPlaybackSamples()), context + ": unchanged audio reuses cache");
        bool matches = true;
        for (int i = 0; i < audio.Length; i++)
        {
            double pos = i * wave.SampleRate / rate;
            int index = Math.Min((int)pos, wave.Samples.Length - 1);
            int next = Math.Min(index + 1, wave.Samples.Length - 1);
            short expected = (short)Math.Round(wave.Samples[index] +
                (wave.Samples[next] - wave.Samples[index]) * (pos - index));
            if (audio[i] != expected) { matches = false; break; }
        }
        Check(matches, context + ": audio resampling consumes those same DAC samples");
    }

    public static void Run(string output)
    {
        checks.Clear();
        Directory.CreateDirectory(output);
        var project = new Project();
        var sample = project.CreatePcmSample("PCM preview");
        var source = new short[11025];
        // Distinct leading/trailing silence exposes using the DPCM trim offset.
        for (int i = 1100; i < 8800; i++)
            source[i] = (short)(Math.Sin(i * Math.PI / 31) * 24000);
        sample.SetWavSourceData(source.ToArray(), 22050, "", true);
        sample.Process();
        var independentDpcm = project.CreateDPCMSampleFromWavData("DPCM preview", source.ToArray(), 22050);
        var originalDpcm = independentDpcm.ProcessedData.ToArray();
        foreach (bool pal in new[] { false, true })
            VerifyWave(sample, pal, pal ? "PAL WAV" : "NTSC WAV");
        Check(sample.SourceWavData.Samples.SequenceEqual(source) && independentDpcm.ProcessedData.SequenceEqual(originalDpcm),
            "preview leaves the source WAV and independent DPCM encoding unchanged");

        var oldWave = sample.GetPcmPreviewWave(false);
        sample.VolumeAdjust = 60;
        sample.FinePitch = 1.035f;
        sample.TrimZeroVolume = true;
        sample.VolumeEnvelope[0] = new SampleVolumePair(0, 0);
        sample.VolumeEnvelope[1] = new SampleVolumePair(2500, 0.8f);
        sample.VolumeEnvelope[2] = new SampleVolumePair(7000, 0.4f);
        sample.VolumeEnvelope[3] = new SampleVolumePair(source.Length - 1, 0);
        sample.Process();
        VerifyWave(sample, false, "volume envelope / fine pitch / trimming");
        var changed = sample.GetPcmPreviewWave(false);
        Check(!ReferenceEquals(oldWave, changed) && !oldWave.Samples.SequenceEqual(changed.Samples),
            "editing volume-envelope points invalidates the displayed and played PCM");

        var conditioned = source.ToArray();
        var envelope = sample.VolumeEnvelope.Select(p => new SampleVolumePair(p.sample, p.volume * 0.6f)).ToList();
        WaveUtils.AdjustVolume(conditioned, envelope);
        WaveUtils.GetWaveNonZeroVolumeRange(conditioned, 512, out int first, out _);
        float expectedStart = (float)(first / (22050 * (double)sample.FinePitch));
        Check(first > 0 && changed.StartTime == expectedStart && changed.EndTime == changed.StartTime + changed.Duration,
            "processed range uses the actual PCM trim and fine-pitch time origin");

        var saved = new ProjectSaveBuffer(project, ProjectBufferFlags.UndoRedo);
        sample.Serialize(saved);
        sample.VolumeAdjust = 10;
        sample.Process();
        var quiet = sample.GetPcmPreviewWave(false);
        sample.Serialize(new ProjectLoadBuffer(project, saved.GetBuffer(), Project.Version, ProjectBufferFlags.UndoRedo));
        var restored = sample.GetPcmPreviewWave(false);
        Check(!ReferenceEquals(quiet, restored) && restored.Samples.SequenceEqual(changed.Samples) &&
            restored.StartTime == changed.StartTime && restored.GetPlaybackSamples().SequenceEqual(changed.GetPlaybackSamples()),
            "undo restores PCM waveform, time origin, and preview audio without stale caches");

        sample.PreviewRate = 0;
        sample.SampleRate = 0;
        sample.PaddingMode = DPCMPaddingType.PadTo16BytesPlusOne;
        sample.Bank = 3;
        sample.Process();
        var irrelevant = sample.GetPcmPreviewWave(false);
        Check(irrelevant.Samples.SequenceEqual(restored.Samples) && irrelevant.StartTime == restored.StartTime,
            "DPCM preview/encoding/padding/bank options cannot alter the PCM waveform");

        var dmc = project.CreatePcmSample("DMC source");
        dmc.SetDmcSourceData(Enumerable.Range(0, 65).Select(i => (byte)(i * 37)).ToArray(), "", true);
        dmc.ReverseBits = true;
        dmc.TrimZeroVolume = true;
        dmc.VolumeAdjust = 70;
        dmc.DmcInitialValueDiv2 = 20;
        dmc.Process();
        VerifyWave(dmc, false, "DMC source / reverse / DAC");
        VerifyWave(dmc, true, "PAL DMC source");

        sample.VolumeAdjust = 0;
        sample.Process();
        VerifyWave(sample, false, "silent trimmed waveform");
        Check(sample.GetPcmPreviewWave(false).StartTime == 0,
            "empty processed waveform has a valid zero time origin");
        File.WriteAllText(Path.Combine(output, "pcm-preview-report.json"),
            JsonSerializer.Serialize(new { passed = true, count = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void RunUi(FamiStudioWindow window, string language, string output, Action<FamiStudioWindow, string> capture)
    {
        var app = window.FamiStudio;
        var sample = app.Project.PcmSamples.First(s => s.SourceDataIsWav);
        var dpcm = app.Project.Samples.First(s => s.SourceDataIsWav);
        int savedChannel = app.SelectedChannelIndex;
        app.SelectedChannelIndex = Array.FindIndex(app.SelectedSong.Channels, c => c.IsMmc5PcmChannel);
        app.StartEditDPCMSample(sample);
        var isPcm = typeof(PianoRoll).GetProperty("IsEditingPcmSample", BindingFlags.Instance | BindingFlags.NonPublic);
        Check((bool)isPcm.GetValue(window.PianoRoll), "PCM waveform context follows the sample library");
        app.PreviewDPCMSample(sample, false);
        Check(!app.PreviewDPCMIsSource && app.PreviewDPCMSampleRate == Mmc5PcmPreviewWave.PlaybackSampleRate &&
            app.PreviewDPCMStartTime == sample.GetPcmPreviewWave(app.PalPlayback).StartTime,
            "processed PCM audition and cursor use the PCM preview data/timebase");
        capture(window, Path.Combine(output, language + "-pcm-processed-wave.ppm"));

        app.PreviewDPCMSample(sample, true);
        Check(app.PreviewDPCMIsSource && app.PreviewDPCMSampleRate == sample.SourceWavData.SampleRate &&
            app.PreviewDPCMStartTime == 0, "source audition keeps the original WAV rate and origin");
        app.SelectedChannelIndex = Array.FindIndex(app.SelectedSong.Channels, c => c.Type == ChannelType.Dpcm);
        Check((bool)isPcm.GetValue(window.PianoRoll), "PCM waveform stays PCM after selecting a DPCM channel");
        app.PreviewDPCMSample(sample, false);
        Check(app.PreviewDPCMSampleRate == Mmc5PcmPreviewWave.PlaybackSampleRate, "PCM audition is independent of selected channel");
        app.SelectedChannelIndex = Array.FindIndex(app.SelectedSong.Channels, c => c.IsMmc5PcmChannel);
        app.StartEditDPCMSample(dpcm);
        Check(!(bool)isPcm.GetValue(window.PianoRoll), "DPCM waveform stays DPCM while selecting the PCM channel");
        app.PreviewDPCMSample(dpcm, false);
        Check(app.PreviewDPCMSampleRate == (int)dpcm.ProcessedSampleRate &&
            app.PreviewDPCMStartTime == dpcm.ProcessedStartTime, "DPCM preview retains its original data and timebase");
        app.SelectedChannelIndex = savedChannel;
    }
}
