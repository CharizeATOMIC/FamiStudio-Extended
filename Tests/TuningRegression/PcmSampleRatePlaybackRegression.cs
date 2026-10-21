using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using FamiStudio;

static class PcmSampleRatePlaybackRegression
{
    static readonly int[] ChannelTypes = { ChannelType.Dpcm, ChannelType.Mmc5Pcm, ChannelType.Vrc6Square1, ChannelType.Vrc6Square2 };
    static int checks;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
        Console.WriteLine("PASS PCM sample clock: " + message);
    }

    sealed class Player : BasePlayer
    {
        public readonly List<short> Audio = new();
        public readonly List<(int Tick, int Register)> Events = new();
        public Player(bool pal) : base(NesApu.APU_WAV_EXPORT, pal, false) { Loop = LoopMode.None; }
        public override void NotifyRegisterWrite(int apu, int reg, int value, int metadata = 0) => Events.Add((frameNumber, reg));
        protected override short[] EndFrame()
        {
            var audio = base.EndFrame(); Audio.AddRange(audio); return audio;
        }
        public void Render(Song song)
        {
            try { BeginPlaySong(song); while (PlaySongFrame()) { } }
            finally { Shutdown(); }
        }
    }

    static Project Make(int destination, bool pal, int pcmRate, int musicRate, double frequency = 441)
    {
        var p = new Project { Name = "PCM sample-clock test", PalMode = pal, PcmRateMultiplier = pcmRate };
        p.SetExpansionAudioMask(ExpansionType.Mmc5Mask | ExpansionType.Vrc6Mask, pcmEnabled: true);
        var wave = Enumerable.Range(0, 4410).Select(i => (short)(Math.Sin(i * 2 * Math.PI * frequency / 44100) * 26000)).ToArray();
        var sample = p.CreatePcmSampleFromWavData("PCM sine", wave, 44100);
        var instrument = p.CreateInstrument(destination == 0 ? ExpansionType.None : destination == 1 ? ExpansionType.Mmc5 : ExpansionType.Vrc6, "PCM sine keys");
        instrument.MapPcmSample(37, sample, loop: true);
        var song = p.CreateSong("Fixed-duration PCM note");
        song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        song.SetDefaultPatternLength(48); song.SetBeatLength(4); song.SetLength(1); song.SetLoopPoint(-1);
        song.GetChannelByType(ChannelTypes[destination]).GetOrCreatePattern(0).Notes[0] =
            new Note(37) { Instrument = instrument, Duration = 32 };
        p.UpdateRateMultiplier = musicRate;
        return p;
    }

    static double EnergyAt(IReadOnlyList<short> audio, double frequency)
    {
        const int first = 4410, count = 8820;
        double real = 0, imaginary = 0;
        for (int i = 0; i < count; i++)
        {
            double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (count - 1));
            double angle = 2 * Math.PI * frequency * i / 44100;
            real += audio[first + i] * window * Math.Cos(angle);
            imaginary += audio[first + i] * window * Math.Sin(angle);
        }
        return (real * real + imaginary * imaginary) / (count * (double)count);
    }

    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var observations = new List<object>();
        Check(NesApu.SupportsPcm, "native PCM ABI available");
        for (int destination = 0; destination < 4; destination++)
        foreach (bool pal in new[] { false, true })
        foreach (int musicRate in new[] { 1, 2, 4 })
        {
            int expectedLength = 0;
            foreach (int pcmRate in new[] { 1, 2, 4, 8 })
            {
                var project = Make(destination, pal, pcmRate, musicRate);
                var player = new Player(pal);
                var watch = Stopwatch.StartNew(); player.Render(project.Songs[0]); watch.Stop();
                string id = $"DAC={destination} PAL={pal} music={musicRate} PCM={pcmRate}";
                if (expectedLength == 0) expectedLength = player.Audio.Count;
                Check(player.Audio.Count == expectedLength, "sample rate preserves arrangement duration " + id);
                double fundamental = EnergyAt(player.Audio, 441);
                Check(fundamental > 100 && fundamental > 10 * EnergyAt(player.Audio, 220.5) &&
                    fundamental > 10 * EnergyAt(player.Audio, 882), "native playback preserves pitch " + id);
                Check(player.Events.Count(e => e.Register == NesApu.PcmRegister(destination, 0)) == 1,
                    "one attack without rate-induced retriggers " + id);
                Check(player.Events.Any(e => e.Register == NesApu.PcmRegister(destination, 1)), "duration stops PCM " + id);
                observations.Add(new { destination, pal, musicRate, pcmRate, samples = player.Audio.Count,
                    renderedMilliseconds = watch.Elapsed.TotalMilliseconds, fundamental });
            }
        }
        // A source tone above the old 4 kHz stream's Nyquist limit must reach
        // the real DAC only once the selected PCM clock can represent it.
        var low = new Player(false); low.Render(Make(1, false, 1, 1, 6000).Songs[0]);
        var high = new Player(false); high.Render(Make(1, false, 4, 1, 6000).Songs[0]);
        double lowEnergy = EnergyAt(low.Audio, 6000), highEnergy = EnergyAt(high.Audio, 6000);
        Check(highEnergy > 100 && highEnergy > lowEnergy * 100,
            "16 kHz PCM preserves 6 kHz source content through the real MMC5 DAC");
        WaveFile.Save(high.Audio.ToArray(), Path.Combine(output, "mmc5-16k-bandwidth.wav"), 44100, 1);
        var uiProject = Make(1, false, 1, 1);
        uiProject.SetExpansionAudioMask(ExpansionType.Mmc5Mask, pcmEnabled: true);
        Check(new ProjectFile().Save(uiProject, Path.Combine(output, "pcm-rate-ui.fms")),
            "isolated UI fixture saved");
        File.WriteAllText(Path.Combine(output, "pcm-sample-rate-playback.json"), JsonSerializer.Serialize(
            new { passed = true, checks, lowEnergy, highEnergy, observations }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
