using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FamiStudio;

static class PcmPlaybackRegression
{
    static readonly List<string> checks = new();
    static readonly int[] types = { ChannelType.Dpcm, ChannelType.Mmc5Pcm, ChannelType.Vrc6Square1, ChannelType.Vrc6Square2 };
    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
        checks.Add(name);
        Console.WriteLine("PASS " + name);
    }

    sealed class Player : BasePlayer
    {
        public readonly List<short> Audio = new();
        public readonly List<(int Frame, int Apu, int Reg, int Value, int Metadata)> Events = new();
        public Player(bool pal, bool stereo = false) : base(NesApu.APU_WAV_EXPORT, pal, stereo) { Loop = LoopMode.None; }
        public override void NotifyRegisterWrite(int apu, int reg, int value, int metadata = 0) =>
            Events.Add((frameNumber, apu, reg, value, metadata));
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

    static Project Make(int destination, bool pal, int rate, bool stereo = false)
    {
        var project = new Project { Name = "PCM destination regression", PalMode = pal };
        project.SetExpansionAudioMask(ExpansionType.Mmc5Mask | ExpansionType.Vrc6Mask, pcmEnabled: true);
        var source = Enumerable.Range(0, 2205).Select(i => (short)(Math.Sin(i * 2 * Math.PI / 50) * 29000)).ToArray();
        var pcm = project.CreatePcmSampleFromWavData("Independent PCM asset", source, 22050);
        var inst = project.CreateInstrument(destination == 0 ? ExpansionType.None : destination == 1 ? ExpansionType.Mmc5 : ExpansionType.Vrc6, "Mapped keys");
        inst.MapPcmSample(37, pcm, loop: true);
        inst.MapPcmSample(38, pcm, pitch: 12, loop: true);
        inst.VolumeEnvelope.Length = 2;
        inst.VolumeEnvelope.Values[0] = 15;
        inst.VolumeEnvelope.Values[1] = 9;
        inst.VolumeEnvelope.Loop = 1;
        if (destination == 0)
        {
            var dpcm = project.CreateDPCMSample("Independent DPCM asset");
            dpcm.SetDmcSourceData(Enumerable.Repeat((byte)0x96, 65).ToArray(), "", true);
            dpcm.Process();
            inst.MapDPCMSample(49, dpcm, pitch: 15);
        }
        var song = project.CreateSong("PCM, rest, ordinary note, PCM");
        song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        song.SetDefaultPatternLength(64); song.SetLength(1); song.SetLoopPoint(-1);
        var channel = song.GetChannelByType(types[destination]);
        channel.StereoEnabled = stereo;
        channel.Pan = 0;
        var notes = channel.GetOrCreatePattern(0).Notes;
        notes[0] = new Note(37) { Instrument = inst, Duration = 16 };
        notes[8] = new Note { Volume = 8 };
        // MMC5 has a dedicated PCM channel; the others return their physical
        // voice to a DPCM mapping or ordinary unmapped VRC6 pulse key.
        if (destination != 1)
            notes[24] = new Note(49) { Instrument = inst, Duration = 12 };
        notes[40] = new Note(38) { Instrument = inst, Duration = 16, Volume = 15 };
        project.UpdateRateMultiplier = rate; // Existing song/envelope clocks stay at 1x.
        return project;
    }

    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        Check(NesApu.SupportsPcm, "generalized native PCM ABI is present");
        for (int destination = 0; destination < 4; destination++)
        foreach (bool pal in new[] { false, true })
        {
            int duration = 0;
            foreach (int rate in new[] { 1, 2, 4 })
            {
                var project = Make(destination, pal, rate);
                var player = new Player(pal);
                player.Render(project.Songs[0]);
                var events = player.Events.Where(e => e.Reg >= 0x20000).ToArray();
                int reg = NesApu.PcmRegister(destination, 0);
                string id = $"destination={destination} pal={pal} rate={rate}";
                Check(events.Count(e => e.Reg == reg) == 2, "two mapped PCM attacks " + id);
                Check(events.Count(e => e.Reg == reg + 1) == 2, "duration stops own PCM voice " + id);
                Check(events.Count(e => e.Reg == reg + 3) == (destination == 1 ? 0 : 1), "ordinary note explicitly takes ownership " + id);
                Check(events.Any(e => e.Reg == reg + 2 && e.Value == 9) &&
                      events.Any(e => e.Reg == reg + 2 && e.Value > 0 && e.Value < 9), "instrument envelope and note gain reach DAC " + id);
                Check(player.Audio.Any(x => Math.Abs((int)x) > 100), "real mapped voice is audible " + id);
                if (duration == 0) duration = player.Audio.Count;
                Check(Math.Abs(duration - player.Audio.Count) <= 3, "output multiplier preserves arrangement duration " + id);
                if (destination >= 2)
                {
                    int vol = destination == 2 ? 0x9000 : 0xa000;
                    Check(!player.Events.Any(e => e.Reg >= vol && e.Reg <= vol + 2 && e.Frame >= rate && e.Frame < 24 * rate),
                          "ordinary VRC6 update cannot overwrite PCM or centered rest " + id);
                    Check(player.Events.Any(e => e.Reg == vol && e.Frame >= 24 * rate && e.Frame < 36 * rate && (e.Value & 0x80) == 0),
                          "VRC6 ordinary pulse returns after mapped playback " + id);
                }
                if (destination == 0)
                    Check(player.Events.Any(e => e.Reg == 0x4015 && e.Value == 0x1f && e.Frame >= 24 * rate),
                          "DPCM key remains functional in the shared mapping " + id);
            }
        }
        for (int destination = 0; destination < 4; destination++)
        {
            var project = Make(destination, false, 1, true);
            // Isolate the voice. The real APU DAC also changes the nonlinear
            // triangle/noise mix, which belongs to those other channel stems.
            long solo = 1L << project.Songs[0].GetChannelByType(types[destination]).Index;
            var left = new Player(false, true) { ChannelMask = solo }; left.Render(project.Songs[0]);
            Check(left.Audio.Skip(15000).Where((x, i) => (i & 1) == 1).All(x => x == 0) &&
                  left.Audio.Where((x, i) => (i & 1) == 0).Any(x => x != 0), $"managed pan isolates hardware voice {destination}");
            var clone = project.DeepClone();
            Check(project.PcmSamples[0].GetPcmData(false).SequenceEqual(clone.PcmSamples[0].GetPcmData(false)),
                $"clone preserves source PCM bytes {destination}");
            var clonedPlayer = new Player(false, true) { ChannelMask = solo }; clonedPlayer.Render(clone.Songs[0]);
            if (!left.Audio.SequenceEqual(clonedPlayer.Audio))
            {
                int first = Enumerable.Range(0, Math.Min(left.Audio.Count, clonedPlayer.Audio.Count)).FirstOrDefault(i => left.Audio[i] != clonedPlayer.Audio[i]);
                File.WriteAllText(Path.Combine(output, "clone-mismatch.json"), JsonSerializer.Serialize(new {
                    destination, first, lengths = new[] { left.Audio.Count, clonedPlayer.Audio.Count },
                    values = new[] { left.Audio[first], clonedPlayer.Audio[first] },
                    original = left.Events.Select(e => new { e.Frame, e.Apu, e.Reg, e.Value, e.Metadata }),
                    clone = clonedPlayer.Events.Select(e => new { e.Frame, e.Apu, e.Reg, e.Value, e.Metadata })
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            Check(left.Audio.SequenceEqual(clonedPlayer.Audio), $"clone preserves PCM mapping playback {destination}");
            var text = Path.Combine(output, $"pcm-{destination}.txt");
            var binary = Path.Combine(output, $"pcm-{destination}.fms");
            Check(new ProjectFile().Save(project, binary) &&
                  new FamistudioTextFile().Save(project, text, new[] { project.Songs[0].Id }, false), $"PCM fixture persists {destination}");
            foreach (var loaded in new[] { new ProjectFile().Load(binary), new FamistudioTextFile().Load(text) })
            {
                Check(loaded != null, $"PCM fixture reloads {destination}");
                var replay = new Player(false, true) { ChannelMask = solo }; replay.Render(loaded.Songs[0]);
                Check(left.Audio.SequenceEqual(replay.Audio), $"binary/text playback exact {destination}");
            }
            project.Songs[0].GetChannelByType(types[destination]).StereoEnabled = false;
            var continuous = new Player(false);
            continuous.BeginPlaySong(project.Songs[0]);
            for (int tick = 0; tick < 10; tick++) continuous.PlaySongFrame();
            int position = NesApu.GetPcmPosition(NesApu.APU_WAV_EXPORT, destination);
            continuous.Shutdown();
            var seeking = new Player(false) { AccurateSeek = false };
            seeking.BeginPlaySong(project.Songs[0]);
            while (seeking.SeekTo(10)) { }
            Check(position >= 0 && NesApu.GetPcmPosition(NesApu.APU_WAV_EXPORT, destination) == position,
                  $"mandatory accurate seeking preserves PCM phase {destination}");
            seeking.Shutdown();
        }
        var repeated = Make(2, false, 1);
        foreach (int destination in new[] { 0, 1, 2, 3 })
        {
            var tied = Make(destination, false, 1);
            var instrument = tied.Instruments[0];
            instrument.VolumeEnvelope.Length = 1;
            instrument.VolumeEnvelope.Values[0] = 7;
            instrument.VolumeEnvelope.Loop = 0;
            var notes = tied.Songs[0].GetChannelByType(types[destination]).PatternInstances[0].Notes;
            notes.Clear();
            notes[0] = new Note(37) { Instrument = instrument, Duration = 16, HasAttack = false };
            notes[8] = new Note(38) { Instrument = instrument, Duration = 8, HasAttack = false };
            notes[24] = new Note(37) { Instrument = instrument, Duration = 8, HasAttack = false };
            var player = new Player(false); player.Render(tied.Songs[0]);
            Check(player.Events.Count(e => e.Reg == NesApu.PcmRegister(destination, 0)) == 2,
                $"first tied PCM key acquires DAC, continuing tie does not retrigger {destination}");
            Check(player.Events.Where(e => e.Reg == NesApu.PcmRegister(destination, 2)).All(e => e.Value == 7),
                $"first tied PCM key initializes its instrument volume {destination}");
        }
        var counts = Enumerable.Repeat(1, ExpansionType.Count).ToArray();
        counts[ExpansionType.Vrc6] = 2;
        repeated.SetExpansionInstances(counts);
        var second = repeated.Songs[0].GetChannelByType(ChannelType.Vrc6Square1, 1);
        second.GetOrCreatePattern(0).Notes[4] = new Note(38) { Instrument = repeated.Instruments[0], Duration = 32 };
        var mixed = new Player(false); mixed.Render(repeated.Songs[0]);
        Check(mixed.Events.Where(e => e.Reg == 0x20020).Select(e => e.Apu).Distinct().Count() == 2,
              "repeated VRC6 instances use separate PCM contexts");
        File.WriteAllText(Path.Combine(output, "pcm-playback-verification.json"),
            JsonSerializer.Serialize(new { passed = true, count = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS all {checks.Count} PCM playback checks");
    }
}
