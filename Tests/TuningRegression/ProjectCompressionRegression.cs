using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using FamiStudio;

static class ProjectCompressionRegression
{
    static int checks;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
        Console.WriteLine("PASS project compression: " + message);
    }

    static byte[] Serialize(Project project, ProjectBufferFlags flags = ProjectBufferFlags.None)
    {
        var writer = new ProjectSaveBuffer(project, flags);
        project.Serialize(writer);
        return writer.GetBuffer();
    }

    static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    static void Rejected(Action action, string label)
    {
        bool rejected = false;
        try { action(); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, label);
    }

    static void RejectedProject(byte[] bytes, string path, string label)
    {
        File.WriteAllBytes(path, bytes);
        bool rejected;
        try { rejected = new ProjectFile().Load(path) == null; }
        catch (IOException) { rejected = true; }
        Check(rejected, label);
    }

    sealed class OutputCapture : BasePlayer
    {
        int frame;
        public readonly List<(int Frame, int Register, int Value, int Metadata)> Writes = new();
        public readonly List<short> Samples = new();
        public OutputCapture(bool pal) : base(NesApu.APU_WAV_EXPORT, pal, false) { Loop = LoopMode.None; }
        public override void NotifyRegisterWrite(int apu, int register, int value, int metadata = 0)
            => Writes.Add((frame, register, value, metadata));
        protected override short[] EndFrame()
        {
            var samples = base.EndFrame();
            Samples.AddRange(samples);
            frame++;
            return samples;
        }
        public void Render(Song song, int frames)
        {
            try
            {
                BeginPlaySong(song);
                for (int index = 0; index < frames && PlaySongFrame(); index++) { }
            }
            finally { Shutdown(); }
        }
    }

    static Project Create(bool large)
    {
        var project = new Project();
        var instrument = project.CreateInstrument(ExpansionType.None, "Compression test voice");
        for (int songIndex = 0; songIndex < (large ? 3 : 1); songIndex++)
        {
            var song = project.CreateSong("Storage test " + songIndex);
            song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
            song.SetDefaultPatternLength(large ? 128 : 32);
            song.SetBeatLength(4);
            song.SetLength(large ? 48 : 1);
            song.SetLoopPoint(-1);
            for (int channel = 0; channel < (large ? 4 : 1); channel++)
            for (int index = 0; index < song.Length; index++)
            {
                var pattern = song.Channels[channel].GetOrCreatePattern(index);
                for (int frame = 0; frame < song.PatternLength; frame += 4)
                    pattern.Notes[frame] = new Note((byte)(31 + (frame * 7 + index * 3 + channel * 11 + songIndex * 5) % 48))
                    { Instrument = instrument, Duration = 2 };
            }
        }
        return project;
    }

    static object CheckProject(Project project, string label, string output, bool playback)
    {
        byte[] before = Serialize(project);
        string path = Path.Combine(output, label + ".fms");
        var timer = Stopwatch.StartNew();
        bool saved = new ProjectFile().Save(project, path);
        timer.Stop();
        double saveMs = timer.Elapsed.TotalMilliseconds;
        Check(saved, label + " saves through the actual ProjectFile API");
        byte[] file = File.ReadAllBytes(path);
        Check(BitConverter.ToUInt32(file, 0) == 0x21534d46 && BitConverter.ToInt32(file, 4) == Project.Version,
            label + " retains magic/version in the first eight bytes");
        Check((BitConverter.ToUInt32(file, 8) & 0x80000000) != 0 &&
              (BitConverter.ToUInt32(file, 8) & 0x7fffffff) == before.Length,
            label + " marks Brotli in the size high bit and preserves the exact low31-bit length");
        Check(Compression.DecompressProjectBytes(file[8..], Project.Version).SequenceEqual(before),
            label + " disk compression preserves every serialized byte");
        timer.Restart();
        var restored = new ProjectFile().Load(path);
        timer.Stop();
        double loadMs = timer.Elapsed.TotalMilliseconds;
        Check(restored != null && Serialize(restored).SequenceEqual(before),
            label + " all model objects serialize identically after actual save/reopen");

        string development = Path.Combine(output, label + "-unmarked-deflate.fms");
        File.WriteAllBytes(development, file[..8].Concat(Compression.CompressBytes(before, CompressionLevel.Optimal)).ToArray());
        var developmentReload = new ProjectFile().Load(development);
        Check(developmentReload != null && Serialize(developmentReload).SequenceEqual(before),
            label + " unmarked format1006 Deflate development project remains readable");

        foreach (var flags in new[] { ProjectBufferFlags.UndoRedo, ProjectBufferFlags.Clipboard })
        {
            byte[] state = Serialize(project, flags);
            byte[] packed = Compression.CompressBytes(state, CompressionLevel.Fastest);
            Check(BitConverter.ToInt32(packed, 0) == state.Length && Compression.DecompressBytes(packed).SequenceEqual(state),
                label + " existing " + flags + " compression stays unmarked Deflate");
        }
        if (playback)
        {
            for (int song = 0; song < project.Songs.Count; song++)
            {
                var a = new OutputCapture(project.PalMode);
                var b = new OutputCapture(restored.PalMode);
                a.Render(project.Songs[song], 128);
                b.Render(restored.Songs[song], 128);
                Check(a.Writes.SequenceEqual(b.Writes), label + " actual register writes are unchanged for song " + song);
                Check(a.Samples.SequenceEqual(b.Samples), label + " actual PCM is unchanged for song " + song);
            }
        }
        RejectedProject(file[..^1], Path.Combine(output, label + "-truncated.fms"), label + " rejects truncated disk payload");
        RejectedProject(file[..11], Path.Combine(output, label + "-truncated-header.fms"), label + " rejects truncated size header");
        RejectedProject(file.Concat(new byte[] { 0 }).ToArray(), Path.Combine(output, label + "-trailing-data.fms"),
            label + " rejects data appended after the Brotli end marker");
        return new { label, file = path, raw_bytes = before.Length, fms_bytes = file.Length,
            equivalent_deflate_fms_bytes = new FileInfo(development).Length, save_ms = saveMs, load_ms = loadMs,
            serialized_sha256 = Hash(before), fms_sha256 = Hash(file), model_exact = true,
            playback_songs = playback ? project.Songs.Count : 0, playback_frames_per_song_limit = 128 };
    }

    public static void Run(string output, string legacyProject = null)
    {
        checks = 0;
        Directory.CreateDirectory(output);
        Check(Project.Version >= Compression.ProjectBrotliFormatVersion && Compression.ProjectBrotliFormatVersion == 1006,
            "Brotli disk storage starts at format1006");
        var random = new byte[65537];
        new Random(1729).NextBytes(random);
        foreach (byte[] data in new[] { Array.Empty<byte>(), new byte[] { 5 }, new byte[65537], random })
        {
            byte[] compressed = Compression.CompressProjectBytes(data);
            Check(Compression.DecompressProjectBytes(compressed, 1006).SequenceEqual(data),
                "exact Brotli roundtrip for payload length " + data.Length);
            Rejected(() => Compression.DecompressProjectBytes(compressed, 1005), "old formats reject Brotli marker before decoding");
            Rejected(() => Compression.DecompressProjectBytes(compressed[..^1], 1006), "incomplete Brotli cannot silently return zero-filled data");
            byte[] longer = (byte[])compressed.Clone();
            BitConverter.GetBytes(0x80000000u | (uint)(data.Length + 1)).CopyTo(longer, 0);
            Rejected(() => Compression.DecompressProjectBytes(longer, 1006), "Brotli output must fill the declared length exactly");
            if (data.Length > 0)
            {
                byte[] shorter = (byte[])compressed.Clone();
                BitConverter.GetBytes(0x80000000u | (uint)(data.Length - 1)).CopyTo(shorter, 0);
                Rejected(() => Compression.DecompressProjectBytes(shorter, 1006), "Brotli output may not exceed the declared length");
            }
        }
        foreach (var level in new[] { CompressionLevel.Fastest, CompressionLevel.Optimal, CompressionLevel.SmallestSize })
        {
            byte[] deflate = Compression.CompressBytes(random, level);
            Check(Compression.DecompressProjectBytes(deflate, 1005).SequenceEqual(random) &&
                  Compression.DecompressProjectBytes(deflate, 1006).SequenceEqual(random),
                "positive-size Deflate accepts old/new project versions at " + level);
            Rejected(() => Compression.DecompressProjectBytes(deflate[..(deflate.Length / 2)], 1006),
                "truncated Deflate cannot silently pad the remaining model with zeros");
            byte[] wrongSize = (byte[])deflate.Clone();
            BitConverter.GetBytes(random.Length - 1).CopyTo(wrongSize, 0);
            Rejected(() => Compression.DecompressProjectBytes(wrongSize, 1006), "Deflate rejects extra decompressed bytes");
        }
        Rejected(() => Compression.DecompressProjectBytes(new byte[3], 1006), "rejects incomplete payload header");
        foreach (string fixture in Directory.GetFiles(Path.Combine("Tests", "TuningRegression", "Fixtures"), "*.fms"))
        {
            int version = BitConverter.ToInt32(File.ReadAllBytes(fixture), 4);
            Check(version <= 1005 && new ProjectFile().Load(fixture) != null,
                "frozen legacy Deflate fixture still loads: " + Path.GetFileName(fixture));
        }
        var projects = new List<object>
        {
            CheckProject(Create(false), "default-project", output, true),
            CheckProject(Create(true), "large-project", output, false)
        };
        if (legacyProject != null)
        {
            byte[] source = File.ReadAllBytes(legacyProject);
            Check(BitConverter.ToInt32(source, 4) <= 1005 && (BitConverter.ToUInt32(source, 8) & 0x80000000) == 0,
                "real compatibility project is old-format unmarked Deflate");
            var project = new ProjectFile().Load(legacyProject);
            Check(project != null, "real old-format project loads before migration");
            projects.Add(CheckProject(project, "legacy-project-migrated", output, true));
            Check(File.ReadAllBytes(legacyProject).SequenceEqual(source), "source compatibility project was never modified");
        }
        File.WriteAllText(Path.Combine(output, "project-compression-report.json"), JsonSerializer.Serialize(
            new { checks, passed = true, projects, legacy_source = legacyProject }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS all {checks} project compression checks");
    }
}
