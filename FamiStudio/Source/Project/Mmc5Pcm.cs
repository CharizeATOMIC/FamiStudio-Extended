using System;
using System.Collections.Generic;

namespace FamiStudio
{
    // Keep editor playback and the 6502 feeder on exactly the same clock.
    public static class Mmc5PcmTiming
    {
        public static int GetPeriod(bool pal) => pal ? 416 : 448;
        public static double GetSampleRate(bool pal) => (pal ? 1662607.125 : 1789772.7272727273) / GetPeriod(pal);
    }

    // The editor displays the actual DAC samples, rather than the separate
    // DPCM encoding kept in the shared sample library.
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
        private readonly Dictionary<bool, (byte[] Data, Mmc5PcmPreviewWave Wave)> pcmPreviewCache =
            new Dictionary<bool, (byte[], Mmc5PcmPreviewWave)>();

        public Mmc5PcmPreviewWave GetPcmPreviewWave(bool pal)
        {
            lock (ProcessedDataLock)
            {
                var data = GetPcmData(pal);
                if (pcmPreviewCache.TryGetValue(pal, out var cached) && ReferenceEquals(cached.Data, data))
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

                var result = new Mmc5PcmPreviewWave(data, Mmc5PcmTiming.GetSampleRate(pal), startTime);
                // GetPcmData invalidates its arrays on source/processing edits,
                // including undo. Array identity avoids a second cache epoch.
                pcmPreviewCache[pal] = (data, result);
                return result;
            }
        }

        // The original WAV is retained in the shared sample library. PCM must
        // never be produced from its lossy DPCM preview when that WAV is present.
        public byte[] GetPcmData(bool pal, int semitones = 0, double? sampleRate = null)
        {
            if (semitones < -48 || semitones > 48) throw new ArgumentOutOfRangeException(nameof(semitones));
            double targetRate = sampleRate ?? Mmc5PcmTiming.GetSampleRate(pal);
            if (double.IsNaN(targetRate) || targetRate < 1000 || targetRate > 48000)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            lock (ProcessedDataLock)
            {
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
