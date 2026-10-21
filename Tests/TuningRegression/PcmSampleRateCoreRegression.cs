using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FamiStudio;

static class PcmSampleRateCoreRegression
{
    static readonly List<string> checks = new();
    static readonly int[] multipliers = { 1, 2, 4, 8 };
    const int SourceRate = 48000;

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks.Add(message);
        Console.WriteLine("PASS PCM sample rate: " + message);
    }

    static short[] Tone(double frequency, int length = SourceRate / 4) =>
        Enumerable.Range(0, length).Select(i => (short)Math.Round(24000 * Math.Sin(2 * Math.PI * frequency * i / SourceRate))).ToArray();

    // Independent spectral measurement of the resampled signal; exclude the
    // finite-source filter boundaries rather than asserting filter internals.
    static double Amplitude(byte[] data, double rate, double frequency)
    {
        double real = 0, imaginary = 0;
        int count = data.Length - 128;
        for (int i = 64; i < data.Length - 64; i++)
        {
            double phase = 2 * Math.PI * frequency * i / rate;
            real += (data[i] - 128) * Math.Cos(phase);
            imaginary += (data[i] - 128) * Math.Sin(phase);
        }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / count;
    }

    static double Rms(byte[] data) => Math.Sqrt(data.Skip(64).Take(data.Length - 128)
        .Average(v => (double)(v - 128) * (v - 128)));

    sealed class Player : BasePlayer
    {
        public bool Audible;
        public Player(bool pal) : base(NesApu.APU_WAV_EXPORT, pal, false) { Loop = LoopMode.None; }
        protected override short[] EndFrame()
        {
            var audio = base.EndFrame();
            Audible |= audio.Any(v => Math.Abs((int)v) > 50);
            return audio;
        }
    }

    static void DataAndPreview()
    {
        var source = Tone(997);
        foreach (bool pal in new[] { false, true })
        {
            var project = new Project { PalMode = pal };
            var sample = project.CreatePcmSampleFromWavData("997 Hz original", source, SourceRate);
            sample.PalProcessing = pal;
            sample.Process();
            var original = sample.GetPcmData(pal).ToArray();
            byte[] previousData = null;
            Mmc5PcmPreviewWave previousWave = null;
            foreach (int multiplier in multipliers)
            {
                project.PcmRateMultiplier = multiplier;
                var bytes = sample.GetPcmData(pal);
                var wave = sample.GetPcmPreviewWave(pal);
                double rate = (pal ? 1662607.125 : 1789772.7272727273) / ((pal ? 416 : 448) / multiplier);
                string label = $"PAL={pal}, PCM={multiplier}x";
                Check(Math.Abs(bytes.Length / rate - 0.25) <= 0.5 / rate + 1e-12,
                    "source duration remains 250 ms within one resampling half-step, " + label);
                Check(Amplitude(bytes, rate, 997) > 85 && Amplitude(bytes, rate, 997 * 1.1) < 8,
                    "997 Hz pitch remains unchanged, " + label);
                Check(ReferenceEquals(bytes, sample.GetPcmData(pal)) &&
                      ReferenceEquals(bytes, sample.GetPcmData(pal, sampleRate: rate)),
                    "matching default and explicit rates share cache, " + label);
                Check(ReferenceEquals(bytes, sample.ProcessedData) && sample.ProcessedSampleRate == (float)rate &&
                      Math.Abs(sample.ProcessedDuration - 0.25) <= 0.5 / rate + 1e-7,
                    "processed data and displayed duration refresh together, " + label);
                Check(wave.SampleRate == rate && wave.Samples.Length == bytes.Length &&
                      wave.Samples.Select((value, i) => value == (short)((bytes[i] - 128) << 8)).All(v => v),
                    "waveform represents current DAC bytes and clock, " + label);
                var preview = wave.GetPlaybackSamples();
                Check(Math.Abs(preview.Length / 44100.0 - bytes.Length / rate) <= 0.5 / 44100 + 1e-12 &&
                      ReferenceEquals(preview, wave.GetPlaybackSamples()) &&
                      ReferenceEquals(wave, sample.GetPcmPreviewWave(pal)),
                    "processed audition preserves duration and reuses its cache, " + label);
                if (previousData != null)
                    Check(!ReferenceEquals(previousData, bytes) && !ReferenceEquals(previousWave, wave),
                        "rate change cannot reuse stale data or preview, " + label);
                var octave = sample.GetPcmData(pal, 12);
                Check(Math.Abs(octave.Length / rate - 0.125) <= 0.5 / rate + 1e-12,
                    "mapped octave retains its intended duration at every rate, " + label);
                Check(sample.SourceWavData.Samples.SequenceEqual(source), "rate conversion retains original WAV, " + label);
                previousData = bytes; previousWave = wave;
            }
            project.PcmRateMultiplier = 1;
            Check(sample.GetPcmData(pal).SequenceEqual(original), "return to 1x restores identical original PCM, PAL=" + pal);
            project.PcmRateMultiplier = 8;
            var alternate = sample.GetPcmData(pal, sampleRate: 6000);
            Check(alternate.Length == 1500 && !ReferenceEquals(alternate, sample.GetPcmData(pal)),
                "explicit cartridge clock remains independent of project rate, PAL=" + pal);
            var cached = sample.GetPcmPreviewWave(pal);
            sample.VolumeAdjust = 40; sample.Process();
            Check(!ReferenceEquals(cached, sample.GetPcmPreviewWave(pal)) &&
                  Amplitude(sample.GetPcmData(pal), sample.GetPcmSampleRate(pal), 997) < 45,
                "processing edit invalidates both data and preview caches, PAL=" + pal);
        }

        foreach (double frequency in new[] { 3000.0, 6000.0, 12000.0 })
        {
            var project = new Project();
            var sample = project.CreatePcmSampleFromWavData("Bandwidth " + frequency, Tone(frequency), SourceRate);
            int audibleMultiplier = frequency == 3000 ? 2 : frequency == 6000 ? 4 : 8;
            project.PcmRateMultiplier = audibleMultiplier / 2;
            Check(Rms(sample.GetPcmData(false)) < 2, frequency + " Hz is filtered below the required PCM bandwidth");
            project.PcmRateMultiplier = audibleMultiplier;
            Check(Amplitude(sample.GetPcmData(false), sample.GetPcmSampleRate(false), frequency) > 80,
                frequency + " Hz is recovered from original WAV at higher PCM rate");
        }
    }

    static void OwnershipAndIsolation()
    {
        var low = new Project();
        var high = new Project { PcmRateMultiplier = 8 };
        var sample = low.CreatePcmSampleFromWavData("Moved original", Tone(997), SourceRate);
        var oldData = sample.GetPcmData(false);
        var oldWave = sample.GetPcmPreviewWave(false);
        low.PcmSamples.Remove(sample); high.PcmSamples.Add(sample);
        sample.SetProject(high);
        Check(sample.ProcessedData.Length > oldData.Length * 7 &&
              ReferenceEquals(sample.ProcessedData, sample.GetPcmData(false)) &&
              !ReferenceEquals(oldWave, sample.GetPcmPreviewWave(false)),
            "moving an asset between projects refreshes target-rate data and preview");
        high.PcmSamples.Remove(sample); low.PcmSamples.Add(sample); sample.SetProject(low);
        Check(sample.ProcessedData.SequenceEqual(oldData), "moving back preserves the original low-rate sound");

        var dpcm = low.CreateDPCMSampleFromDmcData("Unchanged DPCM", Enumerable.Repeat((byte)0x96, 65).ToArray());
        var dpcmData = dpcm.ProcessedData.ToArray();
        var dpcmRate = dpcm.ProcessedSampleRate;
        low.PcmRateMultiplier = 8;
        Check(dpcm.ProcessedData.SequenceEqual(dpcmData) && dpcm.ProcessedSampleRate == dpcmRate,
            "project PCM rate does not change DPCM encoding or rate");
        Check(high.PcmRateMultiplier == 8 && sample.GetPcmPeriod(false) == 56,
            "sample uses its owning project's rate");
        low.PcmRateMultiplier = 1;
        Check(high.PcmRateMultiplier == 8 && sample.GetPcmPeriod(false) == 448,
            "projects keep independent PCM rates");
    }

    static void NativePlayback()
    {
        int[] types = { ChannelType.Dpcm, ChannelType.Mmc5Pcm, ChannelType.Vrc6Square1, ChannelType.Vrc6Square2 };
        for (int destination = 0; destination < 4; destination++)
        foreach (bool pal in new[] { false, true })
        foreach (int multiplier in multipliers)
        {
            var project = new Project { PalMode = pal, PcmRateMultiplier = multiplier };
            project.SetExpansionAudioMask(ExpansionType.Mmc5Mask | ExpansionType.Vrc6Mask, pcmEnabled: true);
            var sample = project.CreatePcmSampleFromWavData("250 ms tone", Tone(997), SourceRate);
            var instrument = project.CreateInstrument(destination == 0 ? ExpansionType.None :
                destination == 1 ? ExpansionType.Mmc5 : ExpansionType.Vrc6, "Mapped PCM");
            instrument.MapPcmSample(49, sample);
            var song = project.CreateSong("Sample clock independent of song clock");
            song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
            song.SetDefaultPatternLength(64); song.SetLength(1); song.SetLoopPoint(-1);
            song.GetChannelByType(types[destination]).GetOrCreatePattern(0).Notes[0] =
                new Note(49) { Instrument = instrument, Duration = 64 };
            int outputRate = multiplier == 8 ? 4 : 1;
            project.UpdateRateMultiplier = outputRate;
            var player = new Player(pal);
            string label = $"destination={destination}, PAL={pal}, PCM={multiplier}x, output={outputRate}x";
            try
            {
                player.BeginPlaySong(song);
                for (int tick = 0; tick < outputRate; tick++) player.PlaySongFrame();
                int first = NesApu.GetPcmPosition(NesApu.APU_WAV_EXPORT, destination);
                for (int tick = 0; tick < 7 * outputRate; tick++) player.PlaySongFrame();
                int last = NesApu.GetPcmPosition(NesApu.APU_WAV_EXPORT, destination);
                double expected = 7 * sample.GetPcmSampleRate(pal) / (pal ? NesApu.FpsPAL : NesApu.FpsNTSC);
                Check(first >= 0 && last > first && Math.Abs(last - first - expected) <= 2,
                    "actual native cursor follows selected CPU sample clock, " + label);
                Check(player.Audible, "actual DAC produces audio, " + label);
                for (int tick = 0; tick < 22 * outputRate; tick++) player.PlaySongFrame();
                Check(NesApu.GetPcmPosition(NesApu.APU_WAV_EXPORT, destination) == -1,
                    "one-shot completes without rate-dependent extension, " + label);
            }
            finally { player.Shutdown(); }
        }
    }

    public static void Run(string output)
    {
        checks.Clear(); Directory.CreateDirectory(output);
        DataAndPreview(); OwnershipAndIsolation(); NativePlayback();
        File.WriteAllText(Path.Combine(output, "pcm-sample-rate-core.json"),
            JsonSerializer.Serialize(new { passed = true, count = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS all {checks.Count} PCM sample-rate core checks");
    }
}
