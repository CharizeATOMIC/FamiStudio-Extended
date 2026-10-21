using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using FamiStudio;

static class Mmc5PcmNativeGuardRegression
{
    sealed class Capture : ILogOutput
    {
        public readonly List<string> Messages = new();
        public void LogMessage(string message) => Messages.Add(message);
        public void ReportProgress(float fraction) { }
        public bool AbortOperation => false;
    }

    public static void Run(string output, bool expectedSupport)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception(description);
            checks.Add(description);
            Console.WriteLine("PASS native PCM guard: " + description);
        }

        Check(NesApu.SupportsPcm == expectedSupport, "actual native export probe");
        Check(NesApu.SupportsPcm == expectedSupport, "cached capability remains stable");
        string message = Mmc5PcmStrings.NativeLibraryRequired;
        Check(!string.IsNullOrWhiteSpace(message) && !message.Contains("### MISSING ###"),
            "capability error is localized");

        var project = Mmc5PcmRegression.MakeProject();
        var binary = Path.Combine(output, "PCM-capability.fms");
        var text = Path.Combine(output, "PCM-capability.txt");
        Check(new ProjectFile().Save(project, binary), "model can be saved independently of playback support");
        Check(new FamistudioTextFile().Save(project, text, new[] { project.Songs[0].Id }, false),
            "text model can be saved independently of playback support");
        var raw = new ProjectFile().Load(binary);
        Check(raw != null && raw.Mmc5PcmEnabled &&
            raw.Songs[0].GetChannelByType(ChannelType.Mmc5Pcm) != null,
            "raw model loading preserves PCM instead of dropping the channel");

        var app = new global::FamiStudio.FamiStudio();
        var log = new Capture();
        var previousLog = Log.LogOutput;
        Log.LogOutput = log;
        try
        {
            foreach (var path in new[] { binary, text })
            {
                int callbacks = 0;
                Project loaded = null;
                int previousErrors = log.Messages.Count(s => s.Contains(message));
                app.OpenProjectFileAsync(path, false, p => { callbacks++; loaded = p; });
                Check(callbacks == 1, Path.GetExtension(path) + " invokes its callback exactly once");
                Check(expectedSupport ? loaded?.Mmc5PcmEnabled == true : loaded == null,
                    Path.GetExtension(path) + " accepts or rejects PCM according to the actual native library");
                Check(log.Messages.Count(s => s.Contains(message)) - previousErrors == (expectedSupport ? 0 : 1),
                    Path.GetExtension(path) + " logs one clear error only when unsupported");
            }
        }
        finally
        {
            Log.LogOutput = previousLog;
        }

        var player = new CapturePlayer(false, false);
        Exception playbackError = null;
        try
        {
            player.BeginPlaySong(project.Songs[0]);
            Check(player.PlaySongFrame(), "supported native PCM begins playback");
        }
        catch (Exception ex) { playbackError = ex; }
        finally { player.Shutdown(); }
        Check(expectedSupport ? playbackError == null :
            playbackError is InvalidOperationException && playbackError.Message == message,
            "playback guard reports capability before any missing entry point can be called");

        foreach (int expansion in new[] { ExpansionType.None, ExpansionType.Vrc6 })
        {
            var independent = new Project();
            if (expansion != ExpansionType.None) independent.SetExpansionAudioMask(ExpansionType.Vrc6Mask);
            var asset = independent.CreatePcmSampleFromWavData("PCM", new short[] { -24000, 24000, -16000, 16000 }, 8000);
            var instrument = independent.CreateInstrument(expansion, "PCM keys");
            instrument.MapPcmSample(37, asset, loop: true);
            var song = independent.CreateSong("Independent PCM");
            var channel = song.GetChannelByType(expansion == ExpansionType.None ? ChannelType.Dpcm : ChannelType.Vrc6Square1);
            channel.GetOrCreatePattern(0).Notes[0] = new Note(37) { Instrument = instrument, Duration = 4 };
            Check(!independent.Mmc5PcmEnabled && independent.UsesPcm, expansion + ": PCM capability does not depend on the MMC5 checkbox");
            string path = Path.Combine(output, "independent-" + expansion + ".fms");
            Check(new ProjectFile().Save(independent, path), expansion + ": standalone PCM model saves");
            Project loaded = null;
            app.OpenProjectFileAsync(path, false, p => loaded = p);
            Check(expectedSupport ? loaded?.UsesPcm == true : loaded == null,
                expansion + ": standalone PCM project opening honors native capability");
            var independentPlayer = new CapturePlayer(false, false);
            Exception error = null;
            try
            {
                independentPlayer.BeginPlaySong(song);
                if (!independentPlayer.PlaySongFrame()) throw new Exception("PCM frame did not run.");
            }
            catch (Exception ex) { error = ex; }
            finally { independentPlayer.Shutdown(); }
            Check(expectedSupport ? error == null : error is InvalidOperationException && error.Message == message,
                expansion + ": standalone PCM playback honors native capability");
        }

        var ordinary = Program.MakeProject();
        string ordinaryPath = Path.Combine(output, "ordinary-apu.fms");
        Check(new ProjectFile().Save(ordinary, ordinaryPath), "ordinary APU fixture saves");
        Project ordinaryLoaded = null;
        app.OpenProjectFileAsync(ordinaryPath, false, p => ordinaryLoaded = p);
        Check(ordinaryLoaded != null && !ordinaryLoaded.Mmc5PcmEnabled,
            "ordinary projects remain available with the older native library");
        var ordinaryPlayer = new CapturePlayer(false, false);
        try
        {
            ordinaryPlayer.BeginPlaySong(ordinaryLoaded.Songs[0]);
            Check(ordinaryPlayer.PlaySongFrame(), "ordinary APU playback is unaffected");
        }
        finally { ordinaryPlayer.Shutdown(); }

        string dll = Path.Combine(AppContext.BaseDirectory, "NesSndEmu.dll");
        File.WriteAllText(Path.Combine(output, "native-pcm-capability.json"),
            JsonSerializer.Serialize(new
            {
                passed = true,
                expected_support = expectedSupport,
                actual_support = NesApu.SupportsPcm,
                native_sha256 = File.Exists(dll) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))).ToLowerInvariant() : null,
                checks
            }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
