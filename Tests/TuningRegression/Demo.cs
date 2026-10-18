using System;
using System.IO;
using System.Linq;
using FamiStudio;

static class Demo
{
    public static void Run(string folder)
    {
        folder = Path.GetFullPath(folder); Directory.CreateDirectory(folder);
        var p = new Project { Name = "Tuning and stereo demo", Author = "FamiStudio Tuning" };
        p.SetExpansionAudioMask(ExpansionType.Vrc6Mask);
        var lead = p.CreateInstrument(ExpansionType.None, "Lead");
        var harmony = p.CreateInstrument(ExpansionType.Vrc6, "Harmony");
        int[] notes = { 49, 53, 56, 60, 58, 56, 53, 51 };
        for (int index = 0; index < 2; index++)
        {
            var song = p.CreateSong(index == 0 ? "432 with pattern retuning" : "440 with microtonal color");
            song.ChangeFamiStudioTempoGroove(new[] { 5 }, false);
            song.SetDefaultPatternLength(160); song.SetBeatLength(20); song.SetLength(4); song.SetLoopPoint(-1);
            song.TuningSettings = new TuningSettings { Override = true, ReferenceHz = index == 0 ? 432 : 440 };
            if (index == 1) song.TuningSettings.Cents = new[] { 0, 12, 4, 16, -14, -2, -10, 2, 14, 0, 18, -12 };
            song.GetPatternCustomSettings(2).Tuning = song.TuningSettings.Clone();
            song.GetPatternCustomSettings(2).Tuning.ReferenceHz = 450;
            var c = song.Channels[0]; var h = song.GetChannelByType(ChannelType.Vrc6Square1);
            c.StereoEnabled = h.StereoEnabled = true; c.Pan = 20; h.Pan = 80;
            for (int pattern = 0; pattern < 4; pattern++)
            {
                var melody = c.GetOrCreatePattern(pattern); var backing = h.GetOrCreatePattern(pattern);
                for (int n = 0; n < notes.Length; n++)
                {
                    melody.Notes[n * 20] = new Note((byte)notes[n]) { Instrument = lead, Duration = 15 };
                    backing.Notes[n * 20] = new Note((byte)(notes[n] - 12)) { Instrument = harmony, Duration = 15, Volume = 7 };
                }
            }
        }
        Save(p, folder, "Song-and-pattern-tuning");
        var ids = p.Songs.Select(s => s.Id).ToArray();
        if (!new NsfFile().Save(p, FamiToneKernel.FamiStudio, Path.Combine(folder, "Song-and-pattern-tuning.nsf"), ids, p.Name, p.Author, "", MachineType.Dual, false)) throw new Exception("Demo NSF export failed.");
        string engine = Path.Combine(folder, "SoundEngine"); Directory.CreateDirectory(engine);
        for (int format = 0; format < 4; format++)
        {
            var path = Path.Combine(engine, "demo-" + AssemblyFormat.Names[format].ToLowerInvariant() + ".s");
            if (!new FamitoneMusicFile(FamiToneKernel.FamiStudio, false).Save(p, ids, format, 8192, false, path, null, DpcmExportMode.Minimum, false, null, MachineType.Dual)) throw new Exception("Demo engine export failed.");
        }
        var counts = Enumerable.Repeat(1, ExpansionType.Count).ToArray(); counts[ExpansionType.Vrc6] = 2; p.SetExpansionInstances(counts);
        p.DeleteAllSongsBut(new[] { p.Songs[0].Id });
        var duplicateSong = p.Songs[0]; duplicateSong.Name = "Two VRC6 chips - left and right";
        foreach (var channel in duplicateSong.Channels) foreach (var pattern in channel.Patterns) pattern.Notes.Clear();
        for (int instance = 0; instance < 2; instance++)
        {
            var channel = duplicateSong.GetChannelByType(ChannelType.Vrc6Square1, instance);
            channel.StereoEnabled = true; channel.Pan = instance * 100;
            for (int pattern = 0; pattern < 4; pattern++) for (int n = 0; n < notes.Length; n++)
                channel.GetOrCreatePattern(pattern).Notes[n * 20] = new Note((byte)(notes[instance == 0 ? n : 7 - n] - instance * 12)) { Instrument = harmony, Duration = 15, Volume = 9 };
        }
        Save(p, folder, "Two-VRC6-stereo");
        SaveCurveDemo(folder);
        Console.WriteLine("PASS demo projects, audio, NSF and four engine formats");
    }
    static void SaveCurveDemo(string folder)
    {
        var p = new Project { Name = "Fixed hardware curves", Author = "FamiStudio Tuning", AutoSortSongs = false };
        p.SetExpansionAudioMask(ExpansionType.S5BMask);
        p.ChipTuning.SetValue(ExpansionType.None, 46, 260);
        p.ChipTuning.SetValue(ExpansionType.S5B, 46, 246);
        foreach (int chip in new[] { ExpansionType.None, ExpansionType.S5B })
        {
            var inst = p.CreateInstrument(chip, chip == 0 ? "Pulse" : "S5B tone");
            var song = p.CreateSong(chip == 0 ? "APU timer 260" : "S5B period 246");
            song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
            song.SetDefaultPatternLength(120); song.SetBeatLength(30); song.SetLength(1); song.SetLoopPoint(-1);
            song.GetChannelByType(chip == 0 ? ChannelType.Square1 : ChannelType.S5BSquare1)
                .GetOrCreatePattern(0).Notes[0] = new Note(46) { Instrument = inst, Duration = 120 };
        }
        Save(p, folder, "Fixed-hardware-curves");
        if (!new NsfFile().Save(p, FamiToneKernel.FamiStudio, Path.Combine(folder, "Fixed-hardware-curves.nsf"),
            p.Songs.Select(s => s.Id).ToArray(), p.Name, p.Author, "", MachineType.NTSC, false))
            throw new Exception("Fixed hardware curve demo NSF export failed.");
        Console.WriteLine("PASS fixed hardware demo: APU timer 260, S5B period 246");
    }
    static void Save(Project project, string folder, string name)
    {
        new ProjectFile().Save(project, Path.Combine(folder, name + ".fms"));
        new FamistudioTextFile().Save(project, Path.Combine(folder, name + ".txt"), project.Songs.Select(s => s.Id).ToArray(), false);
        AudioExportUtils.Save(project.Songs[0], Path.Combine(folder, name + ".wav"), 44100, 1, -1, -1L, false, false, true, null, 0, false, false, (a, channels, path) => WaveFile.Save(a, path, 44100, channels));
    }
}
