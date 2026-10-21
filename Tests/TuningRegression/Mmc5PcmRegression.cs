using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FamiStudio;

static class Mmc5PcmRegression
{
    static readonly List<string> results = new();
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        results.Add(message);
        Console.WriteLine("PASS " + message);
    }

    public static Project MakeProject(bool dpcm = true)
    {
        var project = new Project();
        project.Name = "MMC5 PCM";
        project.Author = "FamiStudio Extended";
        project.Copyright = "Original synthetic test tones";
        project.SetExpansionAudioMask(ExpansionType.Mmc5Mask, pcmEnabled: true);
        var sample = project.CreatePcmSample("PCM test tone");
        var data = new short[11025];
        for (int i = 0; i < data.Length; i++)
        {
            double t = i / 22050.0;
            double envelope = Math.Min(1, i / 220.0) * Math.Min(1, (data.Length - i) / 220.0);
            data[i] = (short)Math.Round(Math.Sin(2 * Math.PI * 330 * t) * 26000 * envelope);
        }
        sample.SetWavSourceData(data, 22050, "", true);
        sample.Process();
        var instrument = project.CreateInstrument(ExpansionType.Mmc5, "PCM keys");
        instrument.MapPcmSample(37, sample);
        instrument.MapPcmSample(49, sample).PcmPitch = 12;
        var loop = instrument.MapPcmSample(25, sample, loop: true);
        loop.PcmPitch = -12;
        var song = project.CreateSong("PCM and pulse");
        song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        song.SetDefaultPatternLength(64);
        song.SetBeatLength(16);
        song.SetLength(2);
        song.SetLoopPoint(-1);
        var channel = song.GetChannelByType(ChannelType.Mmc5Pcm);
        channel.GetOrCreatePattern(0).Notes[0] = new Note(37) { Instrument = instrument, Duration = 32 };
        channel.GetOrCreatePattern(0).Notes[32] = new Note(49) { Instrument = instrument, Duration = 32 };
        channel.GetOrCreatePattern(1).Notes[0] = new Note(25) { Instrument = instrument, Duration = 48 };
        channel.GetOrCreatePattern(1).Notes[16] = new Note { Volume = 8 };
        channel.GetOrCreatePattern(1).Notes[32] = new Note { Volume = 4 };
        var pulse = project.CreateInstrument(ExpansionType.Mmc5, "MMC5 pulse");
        song.GetChannelByType(ChannelType.Mmc5Square1).GetOrCreatePattern(0).Notes[0] =
            new Note(37) { Instrument = pulse, Duration = 64, Volume = 4 };
        if (dpcm)
        {
            var dpcmSample = project.CreateDPCMSampleFromWavData("DPCM test tone", (short[])data.Clone(), 22050);
            var dpcmInstrument = project.CreateInstrument(ExpansionType.None, "DPCM keys");
            dpcmInstrument.MapDPCMSample(37, dpcmSample);
            song.GetChannelByType(ChannelType.Dpcm).GetOrCreatePattern(1).Notes[4] =
                new Note(37) { Instrument = dpcmInstrument, Duration = 30 };
        }
        return project;
    }

    sealed class PcmPlayer : BasePlayer
    {
        public List<short> Audio = new();
        public List<(int Frame, int Reg, int Value, int Metadata)> Events = new();
        public PcmPlayer(bool pal) : base(NesApu.APU_WAV_EXPORT, pal, false) { Loop = LoopMode.None; }
        public override void NotifyRegisterWrite(int apu, int reg, int value, int metadata = 0)
        {
            if (reg >= 0x20010 && reg <= 0x20013) Events.Add((frameNumber, reg, value, metadata));
        }
        protected override short[] EndFrame()
        {
            var audio = base.EndFrame();
            Audio.AddRange(audio);
            return audio;
        }
        public void Render(Song song)
        {
            BeginPlaySong(song);
            while (PlaySongFrame()) { }
            Shutdown();
        }
    }

    static void WriteWav(string filename, IList<short> audio)
    {
        using var file = new BinaryWriter(File.Create(filename));
        file.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        file.Write(36 + audio.Count * 2);
        file.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
        file.Write(16); file.Write((short)1); file.Write((short)1);
        file.Write(44100); file.Write(44100 * 2);
        file.Write((short)2); file.Write((short)16);
        file.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        file.Write(audio.Count * 2);
        foreach (var sample in audio) file.Write(sample);
    }

    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var p = MakeProject();
        Check(p.GetActiveChannelCount() == 8, "MMC5 PCM adds a distinct eighth channel");
        Check(p.Songs[0].GetChannelByType(ChannelType.Mmc5Pcm).SupportsEffect(Note.EffectVolume), "PCM supports note volume");
        Check(!p.Songs[0].GetChannelByType(ChannelType.Mmc5Pcm).SupportsSlideNotes, "PCM key mappings do not expose unsupported pitch slides");
        var clone = p.DeepClone();
        Check(clone.Mmc5PcmEnabled && clone.Songs[0].Channels.Length == 8, "project clone preserves PCM layout");
        var inst = clone.Instruments.First(i => i.HasAnyPcmMappings);
        Check(inst.GetPcmMapping(49).PcmPitch == 12, "clone preserves independent PCM transpose");
        clone.UnmapUnusedSamples();
        Check(inst.GetPcmMapping(49) != null && inst.GetPcmMapping(25) != null, "cleanup retains keys referenced only by PCM");
        clone.TransposeDPCMMapping(49, 50, inst);
        Check(clone.Songs[0].GetChannelByType(ChannelType.Mmc5Pcm).PatternInstances[0].Notes[32].Value == 50, "key relocation also updates PCM notes");
        string binary = Path.Combine(output, "MMC5-PCM.fms"), text = Path.Combine(output, "MMC5-PCM.txt");
        Check(new ProjectFile().Save(p, binary), "binary save");
        var loaded = new ProjectFile().Load(binary);
        Check(loaded.Mmc5PcmEnabled && loaded.Songs[0].GetChannelByType(ChannelType.Mmc5Pcm) != null, "binary load preserves PCM");
        Check(new FamistudioTextFile().Save(p, text, new[] { p.Songs[0].Id }, false), "text save");
        loaded = new FamistudioTextFile().Load(text);
        Check(loaded.Mmc5PcmEnabled && loaded.Instruments.First(i => i.HasAnyPcmMappings).GetPcmMapping(49).PcmPitch == 12, "text round trip preserves PCM keys and layout");
        Check(loaded.PcmSamples[0].SourceDataIsWav && loaded.PcmSamples[0].SourceWavData.Samples.SequenceEqual(p.PcmSamples[0].SourceWavData.Samples), "text round trip preserves the original PCM source WAV");
        Check(loaded.Samples[0].ProcessedData.SequenceEqual(p.Samples[0].ProcessedData), "text round trip preserves independent DPCM bytes");
        Check(loaded.PcmSamples[0].GetPcmData(false, 12).SequenceEqual(p.PcmSamples[0].GetPcmData(false, 12)) &&
              loaded.PcmSamples[0].GetPcmData(true, -12).SequenceEqual(p.PcmSamples[0].GetPcmData(true, -12)), "text round trip preserves exact PCM in both regions");
        var sample = p.PcmSamples[0];
        var originalPcm = sample.GetPcmData(false);
        var undoSample = new ProjectSaveBuffer(p, ProjectBufferFlags.UndoRedo);
        sample.Serialize(undoSample);
        sample.VolumeAdjust = 20;
        sample.Process();
        Check(!sample.GetPcmData(false).SequenceEqual(originalPcm), "sample processing refreshes PCM");
        sample.Serialize(new ProjectLoadBuffer(p, undoSample.GetBuffer(), Project.Version, ProjectBufferFlags.UndoRedo));
        Check(sample.GetPcmData(false).SequenceEqual(originalPcm), "sample undo invalidates the processed PCM cache");
        foreach (bool pal in new[] { false, true })
        {
            p.PalMode = pal;
            var bytes = sample.GetPcmData(pal);
            Check(bytes.Length == (int)Math.Round(Mmc5PcmTiming.GetSampleRate(pal) * .5), $"PCM source duration ({pal})");
            Check(bytes.All(v => v != 0) && bytes.Distinct().Count() > 128, $"full 8-bit source conversion excludes hardware hold code ({pal})");
            Check(Math.Abs(sample.GetPcmData(pal, 12).Length * 2 - bytes.Length) <= 1, $"PCM +12 transpose doubles pitch ({pal})");
            Check(ReferenceEquals(bytes, sample.GetPcmData(pal)), $"processed PCM is cached ({pal})");
            var duration = 0;
            foreach (int rate in new[] { 1, 2, 4 })
            {
                p.UpdateRateMultiplier = rate;
                var player = new PcmPlayer(pal);
                player.Render(p.Songs[0]);
                if (duration == 0) duration = player.Audio.Count;
                Check(Math.Abs(player.Audio.Count - duration) <= 3, $"music duration preserved at {rate}x ({pal})");
                Check(player.Events.Count(e => e.Reg == 0x20010) == 3, $"exactly three PCM attacks at {rate}x ({pal})");
                Check(player.Events.Count(e => e.Reg == 0x20011) >= 1, $"PCM duration stop at {rate}x ({pal})");
                Check(player.Events.Any(e => e.Reg == 0x20012 && e.Value == 8) && player.Events.Any(e => e.Reg == 0x20012 && e.Value == 4),
                    $"volume changes keep the current PCM sample at {rate}x ({pal})");
                Check(player.Audio.Select(x => Math.Abs((int)x)).Max() > 100, $"audible PCM/pulse output at {rate}x ({pal})");
                if (rate == 1 && !pal) WriteWav(Path.Combine(output, "MMC5-PCM.wav"), player.Audio);
            }
        }
        var original = p.DeepClone();
        p.UpdateRateMultiplier = 1;
        p.PalMode = false;
        var continuous = new PcmPlayer(false);
        continuous.BeginPlaySong(p.Songs[0]);
        for (int i = 0; i < 20; i++) continuous.PlaySongFrame();
        int continuousPosition = NesApu.GetMmc5PcmPosition(NesApu.APU_WAV_EXPORT);
        continuous.Shutdown();
        var seeking = new PcmPlayer(false);
        seeking.AccurateSeek = false;
        seeking.BeginPlaySong(p.Songs[0]);
        while (seeking.SeekTo(20)) { }
        Check(NesApu.GetMmc5PcmPosition(NesApu.APU_WAV_EXPORT) == continuousPosition, "PCM seek advances sample time exactly like continuous playback");
        Check(!seeking.AccurateSeek, "required PCM seeking preserves the user's seek preference");
        seeking.Shutdown();
        original.Mmc5PcmEnabled = false;
        Check(original.Songs[0].Channels.Length == 7 && original.Songs[0].GetChannelByType(ChannelType.Mmc5Square1).Patterns.Count > 0, "disabling PCM preserves pulse channel data");
        original.Mmc5PcmEnabled = true;
        Check(original.Songs[0].Channels.Length == 8, "PCM channel can be enabled again");
        var sparse = new Project();
        sparse.SetExpansionAudioMask(ExpansionType.Mmc5Mask);
        sparse.CreateSong("Existing");
        Check(sparse.MergeProject(p.DeepClone()), "merge accepts a PCM project with the same expansion");
        Check(sparse.Mmc5PcmEnabled && sparse.Songs.All(s => s.Channels.Length == 8), "merge aligns existing and imported song layouts");
        File.WriteAllText(Path.Combine(output, "managed-pcm-report.json"), JsonSerializer.Serialize(new { checks = results.Count, passed = results }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
