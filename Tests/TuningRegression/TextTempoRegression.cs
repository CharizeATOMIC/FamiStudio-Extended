using System;
using System.IO;
using System.Linq;
using FamiStudio;

static class TextTempoRegression
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
            Console.WriteLine("PASS text tempo: " + message);
        }
        foreach (bool pal in new[] { false, true })
        foreach (var (grid, steps) in new[] { (17, 8), (17, Pattern.MaxLength / 17), (1, Pattern.MaxLength) })
        {
            var project = new Project { PalMode = pal };
            var instrument = project.CreateInstrument(0, "Synthetic pulse");
            var song = project.CreateSong("Integer frame grid");
            song.ChangeFamiStudioTempoGroove(new[] { grid }, false);
            song.SetDefaultPatternLength(grid * steps); song.SetBeatLength(2 * grid);
            song.SetLength(1); song.SetLoopPoint(-1);
            var pattern = song.Channels[0].GetOrCreatePattern(0);
            int middle = steps / 2 * grid;
            pattern.Notes[0] = new Note(46) { Instrument = instrument, Duration = middle };
            pattern.Notes[middle] = new Note(49) { Instrument = instrument, Duration = grid * steps - middle };
            string path = Path.Combine(output, $"tempo-{pal}-{grid}-{steps}.txt");
            Check(new FamistudioTextFile().Save(project, path, new[] { song.Id }, false), "save valid stepped pattern");
            var restored = new FamistudioTextFile().Load(path);
            Check(restored != null && restored.Songs[0].PatternLength == grid * steps && restored.Songs[0].NoteLength == grid,
                $"valid {steps} steps at {grid} frames per step survive import, PAL={pal}");
            var originalPlayer = new CapturePlayer(pal, false); originalPlayer.Render(song);
            var restoredPlayer = new CapturePlayer(pal, false); restoredPlayer.Render(restored.Songs[0]);
            Check(originalPlayer.Frames.Count == grid * steps && originalPlayer.Frames.Count == restoredPlayer.Frames.Count &&
                originalPlayer.Frames.Zip(restoredPlayer.Frames).All(pair => pair.First.OrderBy(kv => kv.Key).SequenceEqual(pair.Second.OrderBy(kv => kv.Key))),
                "complete actual playback register sequence survives text round trip");
            if (steps == Pattern.MaxLength / grid)
            {
                string text = File.ReadAllText(path);
                foreach (int invalid in new[] { 0, -1, steps + 1, int.MaxValue })
                {
                    File.WriteAllText(path, text.Replace($"PatternLength=\"{steps}\"", $"PatternLength=\"{invalid}\""));
                    Check(new FamistudioTextFile().Load(path) == null, $"reject invalid or overflowing step length {invalid}");
                }
            }
        }
        Console.WriteLine($"PASS all {checks} text-tempo checks");
    }
}
