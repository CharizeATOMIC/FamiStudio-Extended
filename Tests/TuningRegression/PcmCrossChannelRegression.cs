using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FamiStudio;

static class PcmCrossChannelRegression
{
    static readonly List<string> checks = new();
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks.Add(message);
        Console.WriteLine("PASS shared PCM keys: " + message);
    }

    sealed class Player : BasePlayer
    {
        public readonly List<short> Audio = new();
        public readonly List<(int Frame, int Register, int Value)> Events = new();
        public Player(bool pal) : base(NesApu.APU_WAV_EXPORT, pal, false) { Loop = LoopMode.None; }
        public override void NotifyRegisterWrite(int apu, int register, int value, int metadata = 0) =>
            Events.Add((frameNumber, register, value));
        protected override short[] EndFrame()
        {
            var samples = base.EndFrame();
            Audio.AddRange(samples);
            return samples;
        }
        public void Render(Song song)
        {
            BeginPlaySong(song);
            try { while (PlaySongFrame()) { } }
            finally { Shutdown(); }
        }
    }

    static Project Make(bool pal)
    {
        var p = new Project { Name = "Shared PCM keys", PalMode = pal };
        p.SetExpansionAudioMask(ExpansionType.Mmc5Mask, pcmEnabled: true);
        var wave = Enumerable.Range(0, 2205).Select(i => (short)(24000 * Math.Sin(i * 0.08))).ToArray();
        var pcm = p.CreatePcmSampleFromWavData("PCM tone", wave, 22050);
        var dpcm = p.CreateDPCMSampleFromWavData("DPCM tone", wave, 22050);
        var instrument = p.CreateInstrument(ExpansionType.None, "Shared keys");
        var dpcmMapping = instrument.MapDPCMSample(37, dpcm, 15);
        dpcmMapping.DmcInitialValue = 103;
        dpcmMapping.OverrideDmcInitialValue = true;
        instrument.MapPcmSample(38, pcm, 7, true);
        instrument.VolumeEnvelope.Length = 2;
        instrument.VolumeEnvelope.Values[0] = 15;
        instrument.VolumeEnvelope.Values[1] = 9;
        instrument.VolumeEnvelope.Loop = 1;
        var song = p.CreateSong("One instrument, two PCM destinations");
        song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        song.SetDefaultPatternLength(64); song.SetBeatLength(16);
        song.SetLength(1); song.SetLoopPoint(-1);
        var mmc5 = song.GetChannelByType(ChannelType.Mmc5Pcm).GetOrCreatePattern(0).Notes;
        mmc5[0] = new Note(38) { Instrument = instrument, Duration = 24 };
        mmc5[16] = new Note(37) { Instrument = instrument, Duration = 8 };
        mmc5[32] = new Note(38) { Instrument = instrument, Duration = 16 };
        var apu = song.GetChannelByType(ChannelType.Dpcm).GetOrCreatePattern(0).Notes;
        apu[0] = new Note(37) { Instrument = instrument, Duration = 12 };
        apu[24] = new Note(38) { Instrument = instrument, Duration = 16 };
        return p;
    }

    static void CheckMappings(Project project, string context)
    {
        var instrument = project.GetInstrument("Shared keys");
        var notes = project.Songs[0].GetChannelByType(ChannelType.Mmc5Pcm).PatternInstances[0].Notes;
        Check(instrument.IsRegular && instrument.GetPcmMapping(38)?.Sample == project.PcmSamples[0] &&
              instrument.GetPcmMapping(38).PcmPitch == 7 && instrument.GetPcmMapping(38).Loop,
              context + ": original PCM mapping is retained without conversion");
        Check(instrument.GetDPCMMapping(37)?.Sample == project.Samples[0] &&
              instrument.GetDPCMMapping(37).DmcInitialValue == 103 && instrument.GetPcmMapping(37) == null,
              context + ": DPCM key and parameters remain independent");
        Check(new[] { 0, 16, 32 }.All(frame => notes[frame].Instrument == instrument),
              context + ": MMC5 notes keep the same regular instrument");
    }

    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        checks.Clear();
        foreach (var pal in new[] { false, true })
        {
            string region = pal ? "PAL" : "NTSC";
            var project = Make(pal);
            var song = project.Songs[0];
            var instrument = project.GetInstrument("Shared keys");
            Check(song.GetChannelByType(ChannelType.Mmc5Pcm).SupportsInstrument(instrument), region + ": MMC5 accepts regular PCM keys");
            var original = new Player(pal); original.Render(song);
            int mmc5Register = NesApu.PcmRegister(NesApu.PcmDestinationMmc5, 0);
            int apuRegister = NesApu.PcmRegister(NesApu.PcmDestinationApu, 0);
            Check(original.Events.Where(e => e.Register == mmc5Register).Select(e => e.Frame).SequenceEqual(new[] { 0, 32 }),
                  region + ": only the two PCM keys trigger MMC5");
            Check(original.Events.Count(e => e.Register == apuRegister) == 1,
                  region + ": the same PCM key also triggers the 2A03 DAC");
            Check(original.Events.Any(e => e.Register == 0x4015 && (e.Value & 16) != 0 && e.Frame == 0) &&
                  !original.Events.Any(e => e.Register == 0x4015 && (e.Value & 16) != 0 && e.Frame == 16),
                  region + ": DPCM plays on 2A03, never from an MMC5 DPCM-only key");
            Check(original.Audio.Any(v => Math.Abs((int)v) > 200), region + ": native mixed PCM playback is audible");

            var reference = project.DeepClone();
            var converted = reference.CreateInstrument(ExpansionType.Mmc5, "Converted reference");
            InstrumentConverter.Convert(reference.GetInstrument("Shared keys"), converted);
            foreach (var note in reference.Songs[0].GetChannelByType(ChannelType.Mmc5Pcm).Patterns.SelectMany(p => p.Notes.Values).Where(n => n.IsMusical))
                note.Instrument = converted;
            var expected = new Player(pal); expected.Render(reference.Songs[0]);
            Check(original.Audio.SequenceEqual(expected.Audio), region + ": shared keys produce exactly the converted MMC5 waveform");
            Check(original.Events.SequenceEqual(expected.Events), region + ": shared keys preserve register writes and timing");

            string binary = Path.Combine(output, region + ".fms"), text = Path.Combine(output, region + ".txt");
            Check(new ProjectFile().Save(project, binary) && new FamistudioTextFile().Save(project, text, new[] { song.Id }, false), region + ": save shared-key binary and text");
            foreach (var item in new[] { ("clone", project.DeepClone()), ("binary", new ProjectFile().Load(binary)), ("text", new FamistudioTextFile().Load(text)) })
            {
                Check(item.Item2 != null, region + " " + item.Item1 + ": loads");
                CheckMappings(item.Item2, region + " " + item.Item1);
                var replay = new Player(pal); replay.Render(item.Item2.Songs[0]);
                Check(replay.Audio.SequenceEqual(original.Audio), region + " " + item.Item1 + ": exact audio survives persistence");
            }
            var capture = Mmc5PcmExportData.Capture(project, new[] { song.Id }, pal);
            var captured = capture.Tracks[song.Id].Events;
            Check(captured.Count(e => e.Destination == NesApu.PcmDestinationMmc5 && e.Command == 1) == 2 &&
                  captured.Count(e => e.Destination == NesApu.PcmDestinationApu && e.Command == 1) == 1,
                  region + ": hardware exporter captures both destinations from one instrument");
            Check(capture.Samples.Count == 1, region + ": shared PCM mapping exports one sample payload");

            var pulse = project.DeepClone();
            foreach (var channel in pulse.Songs[0].Channels) channel.PatternInstances[0] = null;
            pulse.Songs[0].GetChannelByType(ChannelType.Mmc5Square1).GetOrCreatePattern(0).Notes[0] =
                new Note(38) { Instrument = pulse.GetInstrument("Shared keys"), Duration = 16 };
            var pulsePlayer = new Player(pal); pulsePlayer.Render(pulse.Songs[0]);
            Check(!pulsePlayer.Events.Any(e => e.Register >= 0x20000 && (e.Register & 15) == 0) &&
                  pulsePlayer.Events.Any(e => e.Register == 0x5000 && (e.Value & 15) > 0),
                  region + ": MMC5 pulse remains an ordinary pulse despite the mapped key");

            var silent = project.DeepClone();
            foreach (var channel in silent.Songs[0].Channels) channel.PatternInstances[0] = null;
            silent.Songs[0].GetChannelByType(ChannelType.Mmc5Pcm).GetOrCreatePattern(0).Notes[0] =
                new Note(37) { Instrument = silent.GetInstrument("Shared keys"), Duration = 32 };
            var silentPlayer = new Player(pal); silentPlayer.Render(silent.Songs[0]);
            Check(!silentPlayer.Events.Any(e => e.Register >= 0x20000 && (e.Register & 15) == 0) &&
                  !silentPlayer.Events.Any(e => e.Register == 0x4015 && (e.Value & 16) != 0),
                  region + ": DPCM-only key on MMC5 starts no sample engine");
            Check(silent.GetInstrument("Shared keys").GetDPCMMapping(37) != null,
                  region + ": silent MMC5 key does not delete its original DPCM mapping");
        }
        File.WriteAllText(Path.Combine(output, "shared-pcm-keys.json"), JsonSerializer.Serialize(
            new { passed = true, checks = checks.Count, results = checks }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
