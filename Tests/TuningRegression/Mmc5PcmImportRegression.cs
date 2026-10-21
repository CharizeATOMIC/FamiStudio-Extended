using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FamiStudio;

static class Mmc5PcmImportRegression
{
    sealed class Capture : ILogOutput
    {
        public readonly List<string> Messages = new();
        public void LogMessage(string message) => Messages.Add(message);
        public void ReportProgress(float fraction) { }
        public bool AbortOperation => false;
    }

    static string MakeNsf(string directory, string name, byte[] play, bool readMode = false, bool mmc5 = true)
    {
        var header = new byte[128];
        System.Text.Encoding.ASCII.GetBytes("NESM\u001a").CopyTo(header, 0);
        header[5] = header[6] = header[7] = 1;
        // Synthetic, non-banked NSF1 at $8000. INIT writes multiple DAC levels
        // so the test distinguishes initialization from actual PCM playback.
        header[9] = header[11] = 0x80;
        header[12] = 0x40; header[13] = 0x80;
        header[0x6e] = 0xff; header[0x6f] = 0x40;
        header[0x78] = 0x1d; header[0x79] = 0x4e;
        header[0x7b] = mmc5 ? (byte)8 : (byte)0;
        var data = new byte[0x100];
        byte[] init =
        {
            0xa9,0,0x85,0,                         // LDA #0 / STA $00
            0xa9,0,0x8d,0x10,0x50,                // direct mode
            0xa9,32,0x8d,0x11,0x50,
            0xa9,64,0x8d,0x11,0x50,
            0xa9,128,0x8d,0x11,0x50,
            0xa9,(byte)(readMode ? 1 : 0),0x8d,0x10,0x50,
            0x60
        };
        init.CopyTo(data, 0);
        play.CopyTo(data, 0x40);
        var path = Path.Combine(directory, name + ".nsf");
        File.WriteAllBytes(path, header.Concat(data).ToArray());
        return path;
    }

    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var oldLog = Log.LogOutput;
        var results = new List<object>();
        byte[] varying = { 0xe6,0,0xa5,0,0x09,0x80,0x8d,0x11,0x50,0x60 };
        var cases = new[]
        {
            (Name:"pcm-changes", Play:varying, Read:false, Mmc5:true, Warning:true),
            (Name:"init-only", Play:new byte[]{0x60}, Read:false, Mmc5:true, Warning:false),
            (Name:"constant-neutral", Play:new byte[]{0xa9,128,0x8d,0x11,0x50,0x60}, Read:false, Mmc5:true, Warning:false),
            (Name:"zero-hold", Play:new byte[]{0xa9,0,0x8d,0x11,0x50,0x60}, Read:false, Mmc5:true, Warning:false),
            (Name:"read-mode-ignored-writes", Play:varying, Read:true, Mmc5:true, Warning:false),
            (Name:"ordinary-apu", Play:varying, Read:false, Mmc5:false, Warning:false)
        };
        try
        {
            foreach (var test in cases)
            {
                var path = MakeNsf(output, test.Name, test.Play, test.Read, test.Mmc5);
                var log = new Capture();
                Log.LogOutput = log;
                var project = new NsfFile().Load(path, 0, 1, 64, 0, false, false, false, false);
                if (project == null) throw new Exception(test.Name + ": synthetic NSF failed to import");
                string expected = Mmc5PcmStrings.ImportPcmWarning.Format(project.Songs[0].Name);
                int warnings = log.Messages.Count(s => s.Contains(expected));
                if (warnings != (test.Warning ? 1 : 0))
                    throw new Exception(test.Name + ": unexpected warning count " + warnings);
                if (project.Mmc5PcmEnabled)
                    throw new Exception("Importer pretended to reconstruct a PCM track.");
                Console.WriteLine("PASS PCM import warning: " + test.Name);
                results.Add(new { name = test.Name, warnings, passed = true });
            }
        }
        finally { Log.LogOutput = oldLog; }
        File.WriteAllText(Path.Combine(output, "pcm-import-warning-report.json"),
            JsonSerializer.Serialize(new { passed = true, cases = results }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
