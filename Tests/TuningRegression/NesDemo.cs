using System;
using System.IO;
using FamiStudio;

static class NesDemo
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var project = new Project { Name = "TuningVerification", Author = "FamiStudio Tuning" };
        var instrument = project.CreateInstrument(ExpansionType.None, "Reference pulse");
        var song = project.CreateSong("Pattern overrides and microtones");
        song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        song.SetDefaultPatternLength(128); song.SetBeatLength(16); song.SetLength(4); song.SetLoopPoint(0);
        song.TuningSettings = new TuningSettings { Override = true, ReferenceHz = 432 };
        song.TuningSettings.Cents[0] = 50;
        song.GetPatternCustomSettings(0).Tuning = new TuningSettings { Override = true, ReferenceHz = 440 };
        song.GetPatternCustomSettings(2).Tuning = new TuningSettings { Override = true, ReferenceHz = 450 };
        for (int pattern = 0; pattern < 4; pattern++)
            song.Channels[0].GetOrCreatePattern(pattern).Notes[0] = new Note((byte)(pattern == 3 ? 37 : 46)) { Instrument = instrument, Duration = 128, Volume = 10 };
        new ProjectFile().Save(project, Path.Combine(output, "TuningVerification.fms"));
        new FamistudioTextFile().Save(project, Path.Combine(output, "TuningVerification.txt"), new[] { song.Id }, false);
        if (!new FamitoneMusicFile(FamiToneKernel.FamiStudio, false).Save(project, new[] { song.Id }, AssemblyFormat.CA65, 8192, false, Path.Combine(output, "music.s"), null, DpcmExportMode.Minimum, false, null, MachineType.NTSC)) throw new Exception("NES demo music export failed");
        Console.WriteLine("PASS NES demo music export");
    }
}
