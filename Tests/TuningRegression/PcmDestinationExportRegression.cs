using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FamiStudio;

static class PcmDestinationExportRegression
{
    static int checks;
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message + "; " + Log.GetLastMessage(LogSeverity.Error));
        checks++;
        Console.WriteLine("PASS PCM destination export: " + message);
    }

    static Project Fixture(int destination, bool pal, bool handoff)
    {
        var project = new Project { PalMode = pal };
        int expansion = destination == 1 ? ExpansionType.Mmc5 : destination >= 2 ? ExpansionType.Vrc6 : ExpansionType.None;
        if (expansion != ExpansionType.None)
            project.SetExpansionAudioMask(1 << (expansion - 1));
        if (destination == 1) project.Mmc5PcmEnabled = true;
        var sample = project.CreatePcmSampleFromWavData("Original PCM staircase",
            Enumerable.Range(0, 31).Select(i => (short)(-28000 + i * 1800)).ToArray(),
            (int)Math.Round(Mmc5PcmTiming.GetSampleRate(pal)));
        var inst = project.CreateInstrument(expansion, "Mapped sampler");
        inst.MapPcmSample(49, sample, loop: true);
        var song = project.CreateSong("Destination " + destination);
        song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
        song.SetDefaultPatternLength(48);
        song.SetLength(1);
        song.SetLoopPoint(-1);
        int channelType = destination == 0 ? ChannelType.Dpcm : destination == 1 ? ChannelType.Mmc5Pcm :
            destination == 2 ? ChannelType.Vrc6Square1 : ChannelType.Vrc6Square2;
        var pattern = song.GetChannelByType(channelType).GetOrCreatePattern(0);
        pattern.Notes[0] = new Note(49) { Instrument = inst, Duration = 8 };
        pattern.Notes[24] = new Note(49) { Instrument = inst, Duration = 8 };
        if (handoff && destination != 1)
        {
            if (destination == 0)
            {
                var dmc = project.CreateDPCMSampleFromDmcData("Original DPCM", Enumerable.Repeat((byte)0x55, 17).ToArray());
                inst.MapDPCMSample(50, dmc, 15, false);
                pattern.Notes[12] = new Note(50) { Instrument = inst, Duration = 6 };
            }
            else
            {
                pattern.Notes[12] = new Note(46) { Instrument = inst, Duration = 6 };
            }
        }
        var pulse = project.CreateInstrument(ExpansionType.None, "Independent pulse");
        song.Channels[0].GetOrCreatePattern(0).Notes[0] = new Note(46) { Instrument = pulse, Duration = 48 };
        return project;
    }

    static int Quantize(int value, int destination) => destination == 0 ? value >> 1 : destination == 1 ? value : 0x80 | (value >> 4);

    sealed class EditorTrace : BasePlayer
    {
        public readonly List<(int Frame, int Register, int Value)> Writes = new();
        public EditorTrace(bool pal) : base(NesApu.APU_WAV_EXPORT, pal, false) { Loop = LoopMode.None; }
        public override void NotifyRegisterWrite(int apu, int register, int value, int metadata = 0) =>
            Writes.Add((frameNumber, register, value));
        public void Render(Song song)
        {
            BeginPlaySong(song);
            try { while (PlaySongFrame()) { } }
            finally { Shutdown(); }
        }
    }

    static void LegatoCases(string output)
    {
        foreach (bool pal in new[] { false, true })
        foreach (int destination in new[] { 2, 3 })
        {
            var project = Fixture(destination, pal, true);
            var song = project.Songs[0];
            int channelType = destination == 2 ? ChannelType.Vrc6Square1 : ChannelType.Vrc6Square2;
            var pattern = song.GetChannelByType(channelType).Patterns[0];
            var inst = project.GetInstrument("Mapped sampler");
            inst.VolumeEnvelope.Length = 5;
            inst.VolumeEnvelope.Loop = 0;
            for (int j = 0; j < 5; j++) inst.VolumeEnvelope.Values[j] = (sbyte)new[] { 15, 13, 9, 5, 1 }[j];
            pattern.Notes[0].Duration = 20;
            pattern.Notes[0].Volume = 11;
            pattern.Notes[0].FinePitch = 3;
            pattern.Notes[0].DutyCycle = 2;
            pattern.Notes[0].PhaseReset = 1;
            pattern.Notes[12].HasAttack = false;
            var editor = new EditorTrace(pal);
            editor.Render(song);
            var path = Path.Combine(output, $"vrc6-legato-{destination}-{(pal ? "pal" : "ntsc")}.nsf");
            Check(new NsfFile().Save(project, FamiToneKernel.FamiStudio, path, new[] { song.Id }, "PCM legato",
                "", "", pal ? MachineType.PAL : MachineType.NTSC, false), "legato NSF2 export");
            var writes = new List<(int Frame, int Register, int Value)>();
            var handle = NotSoFatso.NsfOpen(path);
            NotSoFatso.WriteRegisterDelegate callback = (r, v) =>
                writes.Add((NotSoFatso.NsfGetState(handle, -1, NotSoFatso.STATE_PLAYCALLS, 0) - 1, r, v));
            try
            {
                NotSoFatso.NsfSetApuWriteCallback(handle, callback); NotSoFatso.NsfSetTrack(handle, 0);
                for (int i = 0; i < 60; i++) NotSoFatso.NsfRunFrame(handle);
            }
            finally { NotSoFatso.NsfClose(handle); GC.KeepAlive(callback); }
            int firstRegister = destination == 2 ? 0x9000 : 0xa000;
            for (int frame = 12; frame < 18; frame++)
                for (int register = firstRegister; register < firstRegister + 3; register++)
                {
                    var expected = editor.Writes.Last(w => w.Frame <= frame && w.Register == register).Value;
                    var actual = writes.Last(w => w.Frame <= frame && w.Register == register).Value;
                    Check(actual == expected, $"legato keeps envelope/volume/fine/duty at frame {frame}, ${register:x4}");
                }
            Check(writes.Count(w => w.Frame >= 0 && w.Frame < 12 && w.Register == firstRegister + 2) == 1,
                "manual phase reset does not toggle a PCM-owned pulse");
        }
    }

    static void ConcurrentCases(string output)
    {
        foreach (bool pal in new[] { false, true })
        {
            var pair = Fixture(2, pal, false);
            var song = pair.Songs[0];
            song.GetChannelByType(ChannelType.Vrc6Square2).GetOrCreatePattern(0).Notes[0] =
                new Note(49) { Instrument = pair.GetInstrument("Mapped sampler"), Duration = 8 };
            var path = Path.Combine(output, $"vrc6-pair-{(pal ? "pal" : "ntsc")}.nsf");
            Check(new NsfFile().Save(pair, FamiToneKernel.FamiStudio, path, new[] { song.Id },
                "Two original PCM voices", "", "", pal ? MachineType.PAL : MachineType.NTSC, false),
                "two VRC6 PCM voices pass the actual CPU budget");
            Check(new ProjectFile().Save(pair, Path.ChangeExtension(path, ".fms")), "save two-voice original fixture");
            var handle = NotSoFatso.NsfOpen(path);
            var counts = new int[2];
            NotSoFatso.WriteRegisterDelegate callback = (r, v) =>
            {
                if (r == 0x9000 && (v & 128) != 0) counts[0]++;
                if (r == 0xa000 && (v & 128) != 0) counts[1]++;
            };
            try
            {
                NotSoFatso.NsfSetApuWriteCallback(handle, callback); NotSoFatso.NsfSetTrack(handle, 0);
                for (int i = 0; i < 60; i++) NotSoFatso.NsfRunFrame(handle);
            }
            finally { NotSoFatso.NsfClose(handle); GC.KeepAlive(callback); }
            Check(counts.All(c => c > 200), "both physical VRC6 DACs receive timed samples");

            var overload = Fixture(0, pal, false);
            overload.SetExpansionAudioMask(ExpansionType.Mmc5Mask | ExpansionType.Vrc6Mask);
            overload.Mmc5PcmEnabled = true;
            // A two-byte loop exercises real source reads and loop reloads.
            // Constant one-byte loops now use the equivalent cached path.
            var one = overload.CreatePcmSampleFromWavData("Two-byte stress", new short[] { 26000, -26000 },
                (int)Math.Round(Mmc5PcmTiming.GetSampleRate(pal)));
            foreach (int destination in Enumerable.Range(0, 4))
            {
                var expansion = destination == 0 ? ExpansionType.None : destination == 1 ? ExpansionType.Mmc5 : ExpansionType.Vrc6;
                var inst = overload.CreateInstrument(expansion, "Stress " + destination);
                inst.MapPcmSample(49, one, loop: true);
                int channel = new[] { ChannelType.Dpcm, ChannelType.Mmc5Pcm, ChannelType.Vrc6Square1, ChannelType.Vrc6Square2 }[destination];
                var pattern = overload.Songs[0].GetChannelByType(channel).GetOrCreatePattern(0);
                pattern.Notes.Clear();
                pattern.Notes[0] = new Note(49) { Instrument = inst, Duration = 40 };
            }
            path = Path.Combine(output, $"over-budget-{(pal ? "pal" : "ntsc")}.nsf");
            var original = new byte[] { 20, 26, 10, 21 };
            File.WriteAllBytes(path, original);
            Check(!new NsfFile().Save(overload, FamiToneKernel.FamiStudio, path, new[] { overload.Songs[0].Id },
                "Budget rejection", "", "", pal ? MachineType.PAL : MachineType.NTSC, false),
                "sustained four-voice overload is rejected");
            Check(File.ReadAllBytes(path).SequenceEqual(original), "CPU rejection preserves the existing destination");
            Check(!Directory.EnumerateFiles(output, ".pcm-nsf-*.tmp").Any(), "CPU rejection removes temporary files");
        }
    }

    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var results = new List<object>();
        foreach (bool pal in new[] { false, true })
        foreach (int destination in Enumerable.Range(0, 4))
        {
            var project = Fixture(destination, pal, true);
            var song = project.Songs[0];
            var captured = Mmc5PcmExportData.Capture(project, new[] { song.Id }, pal);
            var events = captured.Tracks[song.Id].Events;
            Check((captured.DestinationsMask & (1 << destination)) != 0, "capture retains destination " + destination);
            Check(events.Count(e => e.Destination == destination && e.Command == 1) == 2, "two sample attacks remain sparse");
            if (destination != 1) Check(events.Any(e => e.Destination == destination && e.Command == 6), "ordinary note requests ownership handoff");
            var path = Path.Combine(output, $"destination-{destination}-{(pal ? "pal" : "ntsc")}.nsf");
            Check(new ProjectFile().Save(project, Path.ChangeExtension(path, ".fms")), "save editable synthetic fixture");
            File.WriteAllText(path + ".events.json", JsonSerializer.Serialize(events, new JsonSerializerOptions { IncludeFields = true }));
            Check(new NsfFile().Save(project, FamiToneKernel.FamiStudio, path, new[] { song.Id },
                "PCM destination", "", "", pal ? MachineType.PAL : MachineType.NTSC, false), "actual NSF2 export " + Path.GetFileName(path));
            var native = NotSoFatso.NsfOpen(path);
            Check(native != IntPtr.Zero, "NSF2 native loader");
            var writes = new List<(int Play, int Register, int Value)>();
            NotSoFatso.WriteRegisterDelegate callback = (r, v) =>
                writes.Add((NotSoFatso.NsfGetState(native, -1, NotSoFatso.STATE_PLAYCALLS, 0), r, v));
            int maximumCycles = 0;
            try
            {
                NotSoFatso.NsfSetApuWriteCallback(native, callback);
                NotSoFatso.NsfSetTrack(native, 0);
                int startup = 0;
                while (NotSoFatso.NsfRunFrame(native) == 0 && ++startup < 40) { }
                Check(startup < 40, "INIT completes");
                for (int i = 0; i < 52; i++) Check(NotSoFatso.NsfRunFrame(native) != 0, "PLAY completes " + i);
                maximumCycles = NotSoFatso.NsfGetState(native, -1, 1003, 0);
            }
            finally { NotSoFatso.NsfClose(native); GC.KeepAlive(callback); }
            int register = new[] { 0x4011, 0x5011, 0x9000, 0xa000 }[destination];
            File.WriteAllText(path + ".writes.json", JsonSerializer.Serialize(writes.Select(w => new[] { w.Play, w.Register, w.Value })));
            var values = writes.Where(w => w.Register == register && w.Play >= 1 && w.Play <= 7).Select(w => w.Value).ToArray();
            var mapping = project.GetInstrument("Mapped sampler").GetPcmMapping(49);
            var expected = mapping.Sample.GetPcmData(pal).Select(v => Quantize(v, destination)).ToArray();
            int offset = Array.IndexOf(values, expected[0]);
            Check(offset >= 0 && values.Length >= offset + expected.Length * 2, "hardware DAC receives multiple sample loops: expected " +
                string.Join(",", expected.Take(8)) + ", actual " + string.Join(",", values.Take(8)) + ", count " + values.Length);
            for (int i = 0; i < expected.Length * 2; i++)
                Check(values[offset + i] == expected[i % expected.Length], "exact DAC sequence " + i);
            if (destination == 0)
            {
                Check(writes.Any(w => w.Play >= 12 && w.Play <= 15 && w.Register == 0x4015 && (w.Value & 16) != 0), "ordinary DPCM resumes DMA");
                Check(!writes.Any(w => w.Play >= 14 && w.Play <= 17 && w.Register == 0x4011), "PCM IRQ stops writing over DPCM");
            }
            else if (destination >= 2)
            {
                Check(writes.Any(w => w.Play >= 12 && w.Play <= 15 && w.Register == register && (w.Value & 0x80) == 0), "ordinary VRC6 pulse clears gate mode");
                Check(!writes.Any(w => w.Play >= 14 && w.Play <= 17 && w.Register == register && (w.Value & 0x80) != 0), "PCM IRQ does not overwrite ordinary pulse");
            }
            Check(writes.Any(w => w.Play >= 25 && w.Play <= 30 && w.Register == register && w.Value == expected[0]), "PCM takes ownership again");
            File.WriteAllText(path + ".writes.json", JsonSerializer.Serialize(writes.Select(w => new[] { w.Play, w.Register, w.Value })));
            results.Add(new { destination, pal, file = path, maximum_play_cycles = maximumCycles, passed = true });
        }
        ConcurrentCases(output);
        LegatoCases(output);
        File.WriteAllText(Path.Combine(output, "validation.json"), JsonSerializer.Serialize(new { passed = true, checks, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS all {checks} PCM destination export checks");
    }
}
