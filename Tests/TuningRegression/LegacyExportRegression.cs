using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FamiStudio;

static class LegacyExportRegression
{
    static int checks;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS legacy export: " + message); }
    static Project Make(out Pattern pattern, out Instrument instrument)
    {
        var project = new Project();
        instrument = project.CreateInstrument(ExpansionType.None, "Pulse");
        var song = project.CreateSong("Compatible song");
        song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        song.SetDefaultPatternLength(8); song.SetBeatLength(4); song.SetLength(1); song.SetLoopPoint(-1);
        pattern = song.Channels[0].GetOrCreatePattern(0);
        pattern.Notes[0] = new Note(46) { Instrument = instrument, Duration = 8 };
        return project;
    }
    static bool Save(Project project, Song song, string format, string path)
    {
        if (format == "txt") return new FamitrackerTextFile().Save(project, path, new[] { song.Id });
        if (format == "mid") return new MidiFileWriter().Save(project, path, song.Id, MidiExportInstrumentMode.Channel, new int[song.Channels.Length], true, false, 2);
        return VgmFile.Save(song, path, song.Name, "Test", "NES", "", "", "", "", false);
    }
    static List<(int Register, int Value)> ReadVgmApuWrites(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int index = 0x34 + BitConverter.ToInt32(bytes, 0x34);
        var writes = new List<(int, int)>();
        while (index < bytes.Length)
        {
            int command = bytes[index++];
            if (command == 0x66) break;
            if (command == 0xb4) { writes.Add((0x4000 + bytes[index], bytes[index + 1])); index += 2; }
            else if (command == 0x61) index += 2;
            else if (command == 0x62 || command == 0x63 || command >= 0x70 && command <= 0x7f) { }
            else if (command == 0x67) { int size = BitConverter.ToInt32(bytes, index + 2); index += 6 + size; }
            else throw new Exception($"Unexpected VGM command {command:x2}");
        }
        return writes;
    }
    public static void Run(string output)
    {
        checks = 0; Directory.CreateDirectory(output);
        foreach (string feature in new[] { "delay", "variant", "decay", "cancel-decay", "noise-period", "noise-control", "noise-restart" })
        {
            var project = Make(out var pattern, out var instrument);
            var song = project.Songs[0];
            if (feature == "delay") pattern.PlaybackDelay = 2;
            else if (feature == "variant") pattern.LoopVariant = pattern.Channel.CreatePattern("Repeat");
            else if (feature == "decay" || feature == "cancel-decay") pattern.Notes[2] = new Note { EnvelopeDecay = (byte)(feature == "decay" ? 2 : 0) };
            else
            {
                int type = feature == "noise-period" ? EnvelopeType.NoisePeriod : feature == "noise-control" ? EnvelopeType.NoiseEnvelope : EnvelopeType.NoiseEnvelopeRestart;
                instrument.Envelopes[type].Length = 1; // All-zero enabled envelopes are still meaningful.
                instrument.Envelopes[type].Values[0] = 0;
                song.GetChannelByType(ChannelType.Noise).GetOrCreatePattern(0).Notes[0] = new Note(8) { Instrument = instrument, Duration = 8 };
            }
            foreach (string format in feature == "delay" || feature == "variant" ? new[] { "txt", "mid", "vgm" } : new[] { "txt", "mid" })
            {
                string path = Path.Combine(output, feature + "." + format);
                if (File.Exists(path)) File.Delete(path);
                Check(!Save(project, song, format, path), $"{format} rejects {feature}");
                Check(!File.Exists(path), $"{format} rejection creates no file for {feature}");
                File.WriteAllText(path, "Existing export must survive");
                Check(!Save(project, song, format, path) && File.ReadAllText(path) == "Existing export must survive", $"{format} rejection preserves existing file for {feature}");
            }
        }

        var plain = Make(out var mainPattern, out var mainInstrument);
        // None of these resources are scheduled by the selected compatible song.
        var unused = mainPattern.Channel.CreatePattern("Unused delayed pattern"); unused.PlaybackDelay = 5;
        mainPattern.Notes[100] = new Note { EnvelopeDecay = 1 };
        var other = plain.CreateSong("Unselected song"); other.Channels[0].GetOrCreatePattern(0).PlaybackDelay = 2;
        var unusedInstrument = plain.CreateInstrument(ExpansionType.None, "Unused hardware noise"); unusedInstrument.NoiseControlEnvelope.Length = 1;
        foreach (string format in new[] { "txt", "mid", "vgm" })
        {
            string path = Path.Combine(output, "compatible." + format);
            Check(Save(plain, plain.Songs[0], format, path), $"{format} accepts old behavior with unused extended resources");
            Check(new FileInfo(path).Length > 0, $"{format} writes compatible selection");
        }

        var supported = Make(out var volumePattern, out var noiseInstrument);
        volumePattern.Notes[2] = new Note { EnvelopeDecay = 1 };
        noiseInstrument.NoisePeriodEnvelope.Length = 1; noiseInstrument.NoisePeriodEnvelope.Values[0] = 3;
        noiseInstrument.NoiseControlEnvelope.Length = 1; noiseInstrument.NoiseControlEnvelope.Values[0] = 15;
        noiseInstrument.NoiseRestartEnvelope.Length = 1; noiseInstrument.NoiseRestartEnvelope.Values[0] = 2;
        supported.Songs[0].GetChannelByType(ChannelType.Noise).GetOrCreatePattern(0).Notes[0] = new Note(8) { Instrument = noiseInstrument, Duration = 8 };
        string vgm = Path.Combine(output, "supported-noise-decay.vgm");
        Check(Save(supported, supported.Songs[0], "vgm", vgm), "VGM retains supported decay and hardware noise export");
        var writes = ReadVgmApuWrites(vgm);
        Check(writes.Any(w => w.Register == 0x4000 && (w.Value & 15) == 14) && writes.Any(w => w.Register == 0x4000 && (w.Value & 15) == 9), "VGM contains real decaying pulse volume writes");
        Check(writes.Contains((0x400c, 15)) && writes.Contains((0x400e, 3)) && writes.Contains((0x400f, 8)), "VGM contains hardware noise control, period and restart writes");
        Console.WriteLine($"PASS all {checks} legacy-export checks");
    }
}
