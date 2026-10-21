using System;
using System.Collections.Generic;

namespace FamiStudio
{
    // The compatible 1x clock matches the supplied 6502 feeder. Higher
    // multipliers improve editor/audio rendering without changing note timing.
    public static class Mmc5PcmTiming
    {
        public static int GetPeriod(bool pal, int multiplier = 1)
        {
            if (multiplier != 1 && multiplier != 2 && multiplier != 4 && multiplier != 8)
                throw new ArgumentOutOfRangeException(nameof(multiplier));
            return (pal ? 416 : 448) / multiplier;
        }
        public static double GetSampleRate(bool pal, int multiplier = 1) =>
            (pal ? 1662607.125 : 1789772.7272727273) / GetPeriod(pal, multiplier);
    }

    // The independent PCM library displays its actual DAC samples without
    // passing the source through the DPCM bit encoder.
    public sealed class Mmc5PcmPreviewWave
    {
        public const int PlaybackSampleRate = 44100;

        public short[] Samples { get; }
        public double SampleRate { get; }
        public float StartTime { get; }
        public float Duration => (float)(Samples.Length / SampleRate);
        public float EndTime => StartTime + Duration;
        private short[] playbackSamples;

        internal Mmc5PcmPreviewWave(byte[] data, double rate, float startTime)
        {
            SampleRate = rate;
            StartTime = startTime;
            Samples = new short[data.Length];
            for (int i = 0; i < data.Length; i++)
                Samples[i] = (short)((data[i] - 128) << 8);
        }

        // Audio backends accept integer sample rates. Resample using the exact
        // CPU/IRQ rate, instead of rounding that rate and changing the pitch.
        public short[] GetPlaybackSamples()
        {
            lock (DPCMSample.ProcessedDataLock)
            {
                if (playbackSamples != null)
                    return playbackSamples;

                var length = (int)Math.Round(Samples.Length * PlaybackSampleRate / SampleRate);
                playbackSamples = new short[length];
                for (int i = 0; i < length; i++)
                {
                    double position = i * SampleRate / PlaybackSampleRate;
                    int first = Math.Min((int)position, Samples.Length - 1);
                    int second = Math.Min(first + 1, Samples.Length - 1);
                    playbackSamples[i] = (short)Math.Round(Samples[first] +
                        (Samples[second] - Samples[first]) * (position - first));
                }
                return playbackSamples;
            }
        }
    }

    public partial class DPCMSample
    {
        private readonly Dictionary<(bool Pal, int Pitch, double Rate), byte[]> pcmDataCache = new Dictionary<(bool, int, double), byte[]>();
        private readonly Dictionary<(bool Pal, double Rate), (byte[] Data, Mmc5PcmPreviewWave Wave)> pcmPreviewCache =
            new Dictionary<(bool, double), (byte[], Mmc5PcmPreviewWave)>();

        public int GetPcmPeriod(bool pal) => Mmc5PcmTiming.GetPeriod(pal, project?.PcmRateMultiplier ?? 1);
        public double GetPcmSampleRate(bool pal) => Mmc5PcmTiming.GetSampleRate(pal, project?.PcmRateMultiplier ?? 1);

        private void ClearPcmCaches()
        {
            pcmDataCache.Clear();
            pcmPreviewCache.Clear();
        }

        public Mmc5PcmPreviewWave GetPcmPreviewWave(bool pal)
        {
            lock (ProcessedDataLock)
            {
                var rate = GetPcmSampleRate(pal);
                var key = (pal, rate);
                var data = GetPcmData(pal, sampleRate: rate);
                if (pcmPreviewCache.TryGetValue(key, out var cached) && ReferenceEquals(cached.Data, data))
                    return cached.Wave;

                float startTime = 0;
                if (trimZeroVolume && data.Length > 0)
                {
                    // Recover the source-time origin of the exact PCM trim.
                    // DPCM's ProcessedStartTime includes its own bit encoder
                    // and padding trim, so cannot describe this waveform.
                    short[] wave;
                    if (SourceDataIsWav)
                        wave = WaveUtils.CopyWave(SourceWavData.Samples);
                    else
                    {
                        var dmc = WaveUtils.CopyDpcm(SourceDmcData.Data);
                        if (reverseBits) WaveUtils.ReverseDpcmBits(dmc);
                        WaveUtils.DpcmToWave(dmc, dmcInitialValueDiv2, out wave);
                    }
                    if (UsesVolumeEnvelope)
                    {
                        var envelope = new List<SampleVolumePair>();
                        foreach (var point in volumeEnvelope)
                            envelope.Add(new SampleVolumePair(Utils.Clamp(point.sample, 0, wave.Length - 1),
                                point.volume * Math.Max(0, volumeAdjust) / 100.0f));
                        envelope[0] = new SampleVolumePair(0, envelope[0].volume);
                        envelope[envelope.Count - 1] = new SampleVolumePair(wave.Length - 1, envelope[envelope.Count - 1].volume);
                        WaveUtils.AdjustVolume(wave, envelope);
                    }
                    else if (volumeAdjust != 100)
                        WaveUtils.AdjustVolume(wave, Math.Max(0, volumeAdjust) / 100.0f);

                    WaveUtils.GetWaveNonZeroVolumeRange(wave, SourceDataIsWav ? 512 : 1024, out var first, out _);
                    startTime = (float)(first / (sourceData.GetSampleRate(palProcessing) * (double)finePitch));
                }

                var result = new Mmc5PcmPreviewWave(data, rate, startTime);
                // GetPcmData invalidates its arrays on source/processing edits,
                // including undo. Array identity avoids a second cache epoch.
                pcmPreviewCache[key] = (data, result);
                return result;
            }
        }

        // Retain the PCM asset's original WAV so a higher output rate can
        // recover its bandwidth; never resample an already reduced preview.
        public byte[] GetPcmData(bool pal, int semitones = 0, double? sampleRate = null)
        {
            if (semitones < -48 || semitones > 48) throw new ArgumentOutOfRangeException(nameof(semitones));
            lock (ProcessedDataLock)
            {
                double targetRate = sampleRate ?? GetPcmSampleRate(pal);
                if (double.IsNaN(targetRate) || targetRate < 1000 || targetRate > 48000)
                    throw new ArgumentOutOfRangeException(nameof(sampleRate));
                var key = (pal, semitones, targetRate);
                if (pcmDataCache.TryGetValue(key, out var cached)) return cached;
                if (sourceData == null || sourceData.NumSamples == 0) return Array.Empty<byte>();
                short[] wave;
                if (SourceDataIsWav)
                    wave = WaveUtils.CopyWave(SourceWavData.Samples);
                else
                {
                    var dmc = WaveUtils.CopyDpcm(SourceDmcData.Data);
                    if (reverseBits) WaveUtils.ReverseDpcmBits(dmc);
                    WaveUtils.DpcmToWave(dmc, dmcInitialValueDiv2, out wave);
                }

                if (UsesVolumeEnvelope)
                {
                    var envelope = new List<SampleVolumePair>();
                    foreach (var point in volumeEnvelope)
                        envelope.Add(new SampleVolumePair(Utils.Clamp(point.sample, 0, wave.Length - 1),
                            point.volume * Math.Max(0, volumeAdjust) / 100.0f));
                    envelope[0] = new SampleVolumePair(0, envelope[0].volume);
                    envelope[envelope.Count - 1] = new SampleVolumePair(wave.Length - 1, envelope[envelope.Count - 1].volume);
                    WaveUtils.AdjustVolume(wave, envelope);
                }
                else if (volumeAdjust != 100)
                    WaveUtils.AdjustVolume(wave, Math.Max(0, volumeAdjust) / 100.0f);

                int first = 0, end = wave.Length;
                if (trimZeroVolume)
                    WaveUtils.GetWaveNonZeroVolumeRange(wave, SourceDataIsWav ? 512 : 1024, out first, out end);
                if (end <= first) return Array.Empty<byte>();
                double ratio = sourceData.GetSampleRate(palProcessing) * finePitch *
                    Math.Pow(2.0, semitones / 12.0) / targetRate;
                if (!(ratio > 0) || double.IsInfinity(ratio)) throw new InvalidOperationException("Invalid PCM source rate.");
                double requestedLength = Math.Max(1, Math.Round((end - first) / ratio));
                if (requestedLength > 0x1000000) throw new InvalidOperationException("PCM sample exceeds 16 MiB.");
                var result = new byte[(int)requestedLength];
                // Windowed sinc low-pass filtering prevents WAV content above
                // the PCM stream's Nyquist limit from aliasing into the music.
                double cutoff = Math.Min(1.0, 1.0 / ratio) * 0.94;
                int radius = Math.Min(256, Math.Max(16, (int)Math.Ceiling(16.0 / cutoff)));
                for (int i = 0; i < result.Length; i++)
                {
                    double position = first + i * ratio, sum = 0, weights = 0;
                    int center = (int)Math.Floor(position);
                    for (int j = Math.Max(first, center - radius); j < Math.Min(end, center + radius + 1); j++)
                    {
                        double distance = position - j;
                        double phase = Math.PI * distance * cutoff;
                        double sinc = Math.Abs(phase) < 1e-12 ? 1.0 : Math.Sin(phase) / phase;
                        double weight = sinc * (0.5 + 0.5 * Math.Cos(Math.PI * distance / (radius + 1)));
                        sum += wave[j] * weight;
                        weights += weight;
                    }
                    // $00 is a hold command on MMC5, not its lowest DAC level.
                    int value = (int)Math.Round((weights == 0 ? 0 : sum / weights) / 256.0) + 128;
                    result[i] = (byte)Utils.Clamp(value, 1, 255);
                }
                pcmDataCache.Add(key, result);
                return result;
            }
        }
    }
}
