using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FamiStudio;

static class PcmNesRegression
{
    static readonly int[] Types = { ChannelType.Dpcm, ChannelType.Mmc5Pcm, ChannelType.Vrc6Square1, ChannelType.Vrc6Square2 };
    static readonly string[] Names = { "apu", "mmc5", "vrc6-1", "vrc6-2" };

    static Project Make(int destination, bool pal, bool tracker)
    {
        var p = new Project { Name = "Original PCM cartridge", Author = "FamiStudio Extended regression", PalMode = pal };
        int expansion = destination == 1 ? ExpansionType.Mmc5 : destination >= 2 ? ExpansionType.Vrc6 : ExpansionType.None;
        p.SetExpansionAudioMask(expansion == ExpansionType.None ? 0 : 1 << (expansion - 1), pcmEnabled: destination == 1);
        var pcm = p.CreatePcmSampleFromWavData("Six second original sine",
            Enumerable.Range(0, 132300).Select(i => (short)(Math.Sin(2 * Math.PI * 330 * i / 22050) * 26000)).ToArray(), 22050);
        var tiny = p.CreatePcmSampleFromWavData("One output byte", Enumerable.Repeat((short)12000, 6).ToArray(), 22050);
        var mapped = p.CreateInstrument(expansion, "Sample keys");
        mapped.MapPcmSample(37, pcm);
        mapped.MapPcmSample(38, tiny, loop: true);
        var regular = p.CreateInstrument(ExpansionType.None, "Quiet ordinary pulse");
        var dpcm = p.CreateDPCMSample("Independent DPCM");
        dpcm.SetDmcSourceData(Enumerable.Repeat((byte)0x96, 65).ToArray(), "", true); dpcm.Process();
        var dpcmInstrument = destination == 0 ? mapped : p.CreateInstrument(ExpansionType.None, "DPCM keys");
        dpcmInstrument.MapDPCMSample(49, dpcm, pitch: 15, loop: true);
        var song = p.CreateSong("Long sample and normal voice handoff");
        song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        song.SetDefaultPatternLength(256); song.SetLength(2); song.SetLoopPoint(0);
        foreach (int type in new[] { ChannelType.Square1, ChannelType.Triangle, ChannelType.Noise })
        {
            var ch = song.GetChannelByType(type);
            ch.GetOrCreatePattern(0).Notes[0] = new Note(46) { Instrument = regular, Duration = 512, Volume = 3 };
            ch.GetOrCreatePattern(1).Notes[0] = new Note(49) { Instrument = regular, Duration = 256, Volume = 3 };
        }
        var channel = song.GetChannelByType(Types[destination]);
        channel.GetOrCreatePattern(0).Notes[0] = new Note(37) { Instrument = mapped, Duration = 360 };
        channel.GetOrCreatePattern(1).Notes[128] = new Note(38) { Instrument = mapped, Duration = 64 };
        if (destination != 1)
            channel.GetOrCreatePattern(1).Notes[208] = new Note(49) { Instrument = mapped, Duration = 32 };
        if (destination != 0)
            song.GetChannelByType(ChannelType.Dpcm).GetOrCreatePattern(0).Notes[0] =
                new Note(49) { Instrument = dpcmInstrument, Duration = 512 };
        var second = p.DuplicateSong(song);
        second.Name = "One byte loop stress";
        foreach (var pattern in second.GetChannelByType(Types[destination]).Patterns) pattern.Notes.Clear();
        second.GetChannelByType(Types[destination]).GetOrCreatePattern(0).Notes[0] =
            new Note(38) { Instrument = mapped, Duration = 512 };
        if (tracker)
        {
            p.ConvertToFamiTrackerTempo(true);
            foreach (var s in p.Songs)
            {
                s.FamitrackerTempo = pal ? Song.NativeTempoPAL : Song.NativeTempoNTSC;
                s.FamitrackerSpeed = 1;
            }
        }
        return p;
    }

    static void SaveEvidence(Project project, string folder, string family, bool pal)
    {
        Directory.CreateDirectory(folder);
        var ids = project.Songs.Select(s => s.Id).ToArray();
        if (!new RomFile().Save(project, Path.Combine(folder, "PCM.nes"), ids, project.Name, "", pal))
            throw new Exception(Log.GetLastMessage(LogSeverity.Error));
        new ProjectFile().Save(project, Path.Combine(folder, "PCM.fms"));
        double rate = family == "mmc5"
            ? (pal ? 1662607.125 * 78 / 33247.5 : 1789772.7272727273 * 131 / (29780.5 * 2))
            : (pal ? 1662607.125 : 1789772.7272727273) / (341 * 4.0 / 3);
        int bankSize = family == "mmc5" ? 8192 : 16384;
        var capture = Mmc5PcmExportData.Capture(project, ids, pal, rate, bankSize);
        File.WriteAllBytes(Path.Combine(folder, "pcm-data.bin"), capture.BankData);
        var frames = new List<object>();
        foreach (var song in project.Songs)
        {
            var player = new CapturePlayer(pal, false); player.Render(song);
            frames.Add(new { song = song.Name, registers = player.Frames });
        }
        File.WriteAllText(Path.Combine(folder, "fixture.json"), JsonSerializer.Serialize(new
        {
            pal, tracker = false, destination_mask = capture.DestinationsMask,
            bank_size = bankSize, sample_rate = rate, rom_filename = "PCM.nes",
            template = $"rom_{family}_pcm_m{capture.DestinationsMask}_{(pal ? "pal" : "ntsc")}",
            samples = capture.Samples.Select(s => new { offset = s.Offset, length = s.Data.Length, loop = s.Loop }),
            tracks = capture.Tracks.Values.Select(t => new { end = t.EndTick, loop = t.LoopTick,
                events = t.Events.Select(e => new { tick = e.Tick, command = e.Command, sample = e.SampleIndex, destination = e.Destination }) }),
            editor = frames
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        int checks = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception(name + ": " + Log.GetLastMessage(LogSeverity.Error));
            checks++;
            Console.WriteLine("PASS " + name);
        }
        var variants = new List<object>();
        foreach (int destination in new[] { 0, 1, 2, 3 })
        foreach (bool pal in new[] { false, true })
        foreach (bool tracker in new[] { false, true })
        {
            var p = Make(destination, pal, tracker);
            string name = Names[destination] + (pal ? "-pal" : "-ntsc") + (tracker ? "-tracker" : "-studio");
            string dir = Path.Combine(output, name);
            Directory.CreateDirectory(dir);
            var ids = p.Songs.Select(s => s.Id).ToArray();
            string path = Path.Combine(dir, "PCM.nes");
            Check(new RomFile().Save(p, path, ids, p.Name, p.Author, pal), name + " actual NES export");
            var data = File.ReadAllBytes(path);
            Check(((data[6] >> 4) | (data[7] & 0xf0)) == (destination == 1 ? 5 : 24), name + " hardware mapper");
            Check(new ProjectFile().Save(p, Path.Combine(dir, "PCM.fms")), name + " editable project");
            double rate = destination == 1
                ? (pal ? 1662607.125 * 78 / 33247.5 : 1789772.7272727273 * 131 / (29780.5 * 2))
                : (pal ? 1662607.125 : 1789772.7272727273) / (341 * 4.0 / 3);
            int bankSize = destination == 1 ? 8192 : 16384;
            var capture = Mmc5PcmExportData.Capture(p, ids, pal, rate, bankSize);
            File.WriteAllBytes(Path.Combine(dir, "pcm-data.bin"), capture.BankData);
            var frames = new List<object>();
            foreach (var song in p.Songs)
            {
                var player = new CapturePlayer(pal, false);
                player.Render(song);
                frames.Add(new { song = song.Name, registers = player.Frames });
            }
            string family = destination == 0 ? "apu" : destination == 1 ? "mmc5" : "vrc6";
            string template = $"rom_{family}_pcm_m{1 << destination}_{(pal ? "pal" : "ntsc")}{(tracker ? "_famitracker" : "")}";
            File.WriteAllText(Path.Combine(dir, "fixture.json"), JsonSerializer.Serialize(new
            {
                pal, tracker, destination, destination_mask = 1 << destination, bank_size = bankSize,
                sample_rate = rate, rom_filename = "PCM.nes", template,
                samples = capture.Samples.Select(s => new { offset = s.Offset, length = s.Data.Length, loop = s.Loop }),
                tracks = capture.Tracks.Values.Select(t => new { end = t.EndTick, loop = t.LoopTick,
                    events = t.Events.Select(e => new { tick = e.Tick, command = e.Command, sample = e.SampleIndex, destination = e.Destination }) }),
                editor = frames
            }, new JsonSerializerOptions { WriteIndented = true }));
            variants.Add(new { name, path, template, destination, rate });
            p.UpdateRateMultiplier = 2;
            string rejected = Path.Combine(dir, "unsupported-rate.nes");
            Check(!new RomFile().Save(p, rejected, ids, p.Name, p.Author, pal) && !File.Exists(rejected),
                name + " unsupported update rate does not create a file");
        }
        var dual = Make(1, false, false);
        var dualSong = dual.Songs[0];
        dual.DeleteAllSongsBut(new[] { dualSong.Id });
        var apu = dual.CreateInstrument(ExpansionType.None, "APU PCM handoff");
        apu.MapPcmSample(37, dual.PcmSamples.First(s => s.Name == "One output byte"), loop: true);
        foreach (var pattern in dualSong.GetChannelByType(ChannelType.Dpcm).Patterns) pattern.Notes.Clear();
        foreach (var pattern in dualSong.GetChannelByType(ChannelType.Mmc5Pcm).Patterns) pattern.Notes.Clear();
        dualSong.GetChannelByType(ChannelType.Mmc5Pcm).GetOrCreatePattern(0).Notes[0] =
            new Note(38) { Instrument = dual.Instruments.Single(i => i.IsMmc5 && i.HasAnyPcmMappings), Duration = 32 };
        dualSong.GetChannelByType(ChannelType.Dpcm).GetOrCreatePattern(0).Notes[32] =
            new Note(37) { Instrument = apu, Duration = 32 };
        string sequential = Path.Combine(output, "alternating-apu-mmc5.nes");
        Check(new RomFile().Save(dual, sequential, new[] { dualSong.Id }, "Alternating DACs", "", false),
            "same-tick cross-DAC handoff is allowed");
        foreach (bool pal in new[] { false, true })
        {
            dual.PalMode = pal;
            SaveEvidence(dual, Path.Combine(output, "alternating-mmc5-" + (pal ? "pal" : "ntsc") + "-studio"), "mmc5", pal);
            Check(true, "APU/MMC5 same-tick handoff fixture " + pal);
            var vrc6 = Make(2, pal, false);
            var song = vrc6.Songs[0];
            vrc6.DeleteAllSongsBut(new[] { song.Id });
            foreach (int type in new[] { ChannelType.Dpcm, ChannelType.Vrc6Square1, ChannelType.Vrc6Square2 })
                foreach (var pattern in song.GetChannelByType(type).Patterns) pattern.Notes.Clear();
            var apuInstrument = vrc6.CreateInstrument(ExpansionType.None, "APU alternating PCM");
            apuInstrument.MapPcmSample(37, vrc6.PcmSamples.First(s => s.Name == "One output byte"), loop: true);
            var vrc6Instrument = vrc6.Instruments.Single(i => i.IsVrc6 && i.HasAnyPcmMappings);
            song.GetChannelByType(ChannelType.Vrc6Square1).GetOrCreatePattern(0).Notes[0] =
                new Note(38) { Instrument = vrc6Instrument, Duration = 32 };
            song.GetChannelByType(ChannelType.Dpcm).GetOrCreatePattern(0).Notes[32] =
                new Note(37) { Instrument = apuInstrument, Duration = 32 };
            song.GetChannelByType(ChannelType.Vrc6Square2).GetOrCreatePattern(0).Notes[64] =
                new Note(38) { Instrument = vrc6Instrument, Duration = 32 };
            SaveEvidence(vrc6, Path.Combine(output, "alternating-vrc6-" + (pal ? "pal" : "ntsc") + "-studio"), "vrc6", pal);
            Check(true, "APU/VRC6 pulse1/pulse2 same-tick handoff fixture " + pal);
        }
        dual.PalMode = false;
        var apuNotes = dualSong.GetChannelByType(ChannelType.Dpcm).PatternInstances[0].Notes;
        apuNotes[16] = apuNotes[32]; apuNotes.Remove(32);
        var sentinel = new byte[] { 1, 2, 3, 4 };
        string overlap = Path.Combine(output, "rejected-overlap.nes");
        File.WriteAllBytes(overlap, sentinel);
        Check(!new RomFile().Save(dual, overlap, new[] { dualSong.Id }, "", "", false) &&
              File.ReadAllBytes(overlap).SequenceEqual(sentinel), "overlapping PCM rejects without overwriting target");
        var excessive = new Project();
        excessive.SetExpansionAudioMask(ExpansionType.Mmc5Mask, pcmEnabled: true);
        var overflowSong = excessive.CreateSong("DPCM bank overflow");
        overflowSong.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        overflowSong.SetDefaultPatternLength(Project.MaxDPCMBanks * 2); overflowSong.SetLength(1); overflowSong.SetLoopPoint(-1);
        var dpcmNotes = overflowSong.GetChannelByType(ChannelType.Dpcm).GetOrCreatePattern(0).Notes;
        for (int index = 0; index < Project.MaxDPCMBanks * 2; index++)
        {
            var sample = excessive.CreateDPCMSample("DPCM " + index);
            var bytes = Enumerable.Repeat((byte)0x55, DPCMSample.MaxSampleSize).ToArray(); bytes[0] = (byte)index;
            sample.SetDmcSourceData(bytes, "", true); sample.Process();
            var instrument = excessive.CreateInstrument(ExpansionType.None, "DPCM mapping " + index);
            instrument.MapDPCMSample(37, sample);
            dpcmNotes[index] = new Note(37) { Instrument = instrument, Duration = 1 };
        }
        var isolatedPcm = excessive.CreatePcmSampleFromWavData("Separate PCM library", new short[] { 1000, -1000, 1000, -1000, 1000, -1000 }, 22050);
        var overflowPcm = excessive.CreateInstrument(ExpansionType.Mmc5, "PCM mapping");
        overflowPcm.MapPcmSample(37, isolatedPcm, loop: true);
        overflowSong.GetChannelByType(ChannelType.Mmc5Pcm).GetOrCreatePattern(0).Notes[0] =
            new Note(37) { Instrument = overflowPcm, Duration = 128 };
        string overflow = Path.Combine(output, "dpcm-overflow.nes");
        Check(!new RomFile().Save(excessive, overflow, new[] { overflowSong.Id }, "", "", false) &&
              !File.Exists(overflow) && Log.GetLastMessage(LogSeverity.Error) == Mmc5PcmStrings.SampleBankOverflow.ToString(),
              "DPCM bank overflow rejected before creating PCM ROM");
        overflow = Path.Combine(output, "dpcm-overflow-existing.nes");
        File.WriteAllBytes(overflow, sentinel);
        Check(!new RomFile().Save(excessive, overflow, new[] { overflowSong.Id }, "", "", false) &&
              File.ReadAllBytes(overflow).SequenceEqual(sentinel), "DPCM overflow preserves existing output");
        File.WriteAllText(Path.Combine(output, "export-verification.json"),
            JsonSerializer.Serialize(new { passed = true, checks, variants }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS all {checks} PCM NES export checks");
    }
}
