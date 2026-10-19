using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using FamiStudio;

// Local user-owned conversion artifacts are supplied at runtime. No ROM data is embedded.
static class GimmickNsfRegression
{
    public static void Run(string input, string output)
    {
        Directory.CreateDirectory(output);
        var project = new ProjectFile().Load(Path.Combine(input, "Gimmick-Editable.fms"));
        if (project == null) throw new Exception("Cannot load local Gimmick project.");
        string bundle = Path.Combine(output, "Gimmick-Editable.nsf");
        if (!new NsfFile().Save(project, FamiToneKernel.FamiStudio, bundle, project.Songs.Select(s=>s.Id).ToArray(), "Gimmick editable validation", "", "", MachineType.NTSC, false))
            throw new Exception("Combined 20-song NSF export failed.");
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(input, "conversion-report.json")));
        var results = new List<object>(); long allChecks = 0, allFrames = 0, allErrors = 0;
        foreach (var track in report.RootElement.GetProperty("tracks").EnumerateArray())
        {
            int id = track.GetProperty("id").GetInt32(); string name = track.GetProperty("name").GetString();
            var song = project.GetSong(name); string nsf = Path.Combine(output, $"{id:x2}.nsf");
            if (!new NsfFile().Save(project, FamiToneKernel.FamiStudio, nsf, new[] { song.Id }, name, "", "", MachineType.NTSC, false))
                throw new Exception($"NSF export failed for {name}.");
            using var stream = File.OpenRead(Path.Combine(input, "traces", track.GetProperty("trace").GetString()));
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var trace = JsonDocument.Parse(gzip);
            var frames = trace.RootElement.GetProperty("register_frames");
            var expectedWrites = trace.RootElement.GetProperty("writes");
            var state = new Dictionary<int, int>(); var writes = new List<(int Address, int Value)>(); int latch = 0;
            int previousEditorControl = 0x0f, previousEngineControl = 0x0f;
            static int[] ControlChanges(IEnumerable<int> values, ref int previous)
            {
                var changes = new List<int>();
                foreach (int raw in values)
                {
                    int value = raw & 0x1f;
                    // The editor writes 0F on every silent frame. A repeated
                    // 1F can restart a naturally finished sample, so never
                    // discard writes that enable DPCM, even with the same value.
                    if (value == previous && (value & 0x10) == 0) continue;
                    changes.Add(value); previous = value;
                }
                return changes.ToArray();
            }
            long checks = 0, errors = 0; var examples = new List<string>();
            void Compare(bool condition, int frame, string message)
            {
                checks++; if (condition) return; errors++;
                if (examples.Count < 30) examples.Add($"frame {frame}: {message}");
            }
            int R(int address) => state.GetValueOrDefault(address);
            var handle = NotSoFatso.NsfOpen(bundle);
            if (handle == IntPtr.Zero) throw new Exception($"NSF cannot open: {name}");
            if (NotSoFatso.NsfGetTrackCount(handle) != project.Songs.Count) throw new Exception("Combined NSF lost tracks.");
            NotSoFatso.WriteRegisterDelegate callback = (address, value) => {
                if (address == 0xc000) latch = value & 15;
                else if (address == 0xe000) { state[0x5000 + latch] = value; writes.Add((0x5000 + latch, value)); }
                else if (address >= 0x4000 && address <= 0x4017) { state[address] = value; writes.Add((address, value)); }
            };
            try
            {
                NotSoFatso.NsfSetApuWriteCallback(handle, callback); NotSoFatso.NsfSetTrack(handle, project.Songs.IndexOf(song));
                NotSoFatso.NsfRunFrame(handle); writes.Clear();
                for (int frame = 0; frame < frames.GetArrayLength(); frame++)
                {
                    NotSoFatso.NsfRunFrame(handle); var expected = frames[frame]; var ew = expectedWrites[frame];
                    for (int channel = 0; channel < 3; channel++)
                    {
                        int period = NotSoFatso.NsfGetState(handle, channel, NotSoFatso.STATE_PERIOD, 0);
                        int volume = channel == 2 ? ((R(0x4008) & 127) != 0 ? 15 : 0) : R(0x4000 + channel * 4) & 15;
                        int ep = expected[channel][0].GetInt32(), ev = expected[channel][1].GetInt32();
                        if (channel < 2 && period < 8) volume = 0;
                        if (channel < 2 && ep < 8) ev = 0;
                        Compare(volume == ev, frame, $"APU {channel} volume {volume} != {ev}");
                        if (ev != 0) {
                            Compare(period == ep, frame, $"APU {channel} period {period} != {ep}");
                            if (channel < 2) Compare((R(0x4000 + channel * 4) >> 6) == expected[channel][2].GetInt32(), frame, $"APU {channel} duty");
                        }
                    }
                    static int Control(int v) => (v & 31) == 16 ? 16 : v & 63;
                    int nc = Control(R(0x400c)), ec = Control(expected[3][3].GetInt32());
                    Compare(nc == ec, frame, $"noise control {nc:x2} != {ec:x2}");
                    if (ec != 16) Compare((R(0x400e) & 143) == (expected[3][0].GetInt32() | (expected[3][2].GetInt32() << 7)), frame, "noise period/mode");
                    var nr = writes.Where(w => w.Address == 0x400f).Select(w => w.Value).ToArray();
                    var er = ew.EnumerateArray().Where(w => w[0].GetInt32() == 0x400f).Select(w => w[1].GetInt32()).ToArray();
                    Compare(nr.SequenceEqual(er), frame, $"noise restart [{string.Join(',',nr)}] != [{string.Join(',',er)}]");
                    for (int channel = 0; channel < 3; channel++)
                    {
                        int State(int kind) => NotSoFatso.NsfGetState(handle, ChannelType.S5BSquare1 + channel, kind, 0);
                        int rawMixer = State(NotSoFatso.STATE_S5BMIXER);
                        int mixer = (rawMixer & 1) | ((rawMixer >> 2) & 2);
                        int volume = State(NotSoFatso.STATE_VOLUME), em = expected[5 + channel][2].GetInt32();
                        int ev = em == 3 ? 0 : expected[5 + channel][1].GetInt32();
                        Compare(volume == ev, frame, $"S5B {channel} volume {volume} != {ev}");
                        if (ev != 0) {
                            Compare(mixer == em, frame, $"S5B {channel} mixer {mixer} != {em}");
                            if ((em & 1) == 0) Compare(State(NotSoFatso.STATE_PERIOD) == expected[5 + channel][0].GetInt32(), frame, $"S5B {channel} period {State(NotSoFatso.STATE_PERIOD)} != {expected[5+channel][0].GetInt32()}");
                            if ((em & 2) == 0) Compare(State(NotSoFatso.STATE_S5BNOISEFREQUENCY) == expected[5 + channel][3].GetInt32(), frame, $"S5B {channel} noise");
                        }
                    }
                    var sampleWrites = ew.EnumerateArray().Where(w => w[0].GetInt32() == 0x4012).ToArray();
                    int actualStarts = writes.Count(w => w.Address == 0x4012);
                    var engineControls = writes.Where(w => w.Address == 0x4015).Select(w => w.Value).ToArray();
                    var editorControls = ew.EnumerateArray().Where(w => w[0].GetInt32() == 0x4015).Select(w => w[1].GetInt32()).ToArray();
                    if (engineControls.Length > 0 || editorControls.Length > 0)
                    {
                        var actualChanges = ControlChanges(engineControls, ref previousEngineControl);
                        var expectedChanges = ControlChanges(editorControls, ref previousEditorControl);
                        Compare(actualChanges.SequenceEqual(expectedChanges), frame,
                            $"DPCM stop/retrigger 4015 [{string.Join(',', actualChanges)}] != [{string.Join(',', expectedChanges)}]");
                    }
                    Compare(actualStarts == sampleWrites.Length, frame, $"DPCM triggers {actualStarts} != {sampleWrites.Length}");
                    if (sampleWrites.Length > 0 && actualStarts > 0)
                    {
                        var sample = project.GetSample(sampleWrites[^1][2].GetInt32());
                        Compare((R(0x4010) & 79) == (expected[4][0].GetInt32() & 79), frame, "DPCM rate/loop");
                        Compare(R(0x4011) == expected[4][1].GetInt32(), frame, "DPCM initial DAC");
                        Compare(R(0x4013) == expected[4][3].GetInt32(), frame, "DPCM length");
                        if (sample != null) {
                            int length = R(0x4013) * 16 + 1;
                            var data = Enumerable.Range(0,length).Select(i => (byte)NotSoFatso.NsfGetState(handle,ChannelType.Dpcm,NotSoFatso.STATE_DPCMSAMPLEDATA,i)).ToArray();
                            Compare(sample.ProcessedData.SequenceEqual(data), frame, "DPCM exact sample bytes");
                        }
                    }
                    writes.Clear();
                }
            }
            finally { NotSoFatso.NsfClose(handle); GC.KeepAlive(callback); }
            allChecks += checks; allFrames += frames.GetArrayLength(); allErrors += errors;
            results.Add(new { id, name, frames = frames.GetArrayLength(), checks, errors, examples });
            Console.WriteLine($"{(errors==0?"PASS":"FAIL")} NSF {id:x2} {name}: {frames.GetArrayLength()} frames, {checks} comparisons, {errors} errors");
            foreach (var example in examples.Take(4)) Console.WriteLine(example);
            File.WriteAllText(Path.Combine(output,"nsf-validation.json"),JsonSerializer.Serialize(new { frames = allFrames, checks = allChecks, errors = allErrors, tracks = results },new JsonSerializerOptions { WriteIndented = true }));
        }
        if (allErrors != 0) throw new Exception($"Gimmick NSF comparison failed: {allErrors} differences.");
        Console.WriteLine($"PASS all {allChecks} actual NSF CPU comparisons over {allFrames} frames.");
    }
}
