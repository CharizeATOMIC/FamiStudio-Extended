using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using FamiStudio;

// A one-off local conversion harness. No game ROM, music, envelopes or samples are embedded here.
// Input is the external game-specific decoder's event/envelope JSON, not an NSF frame capture.
static class GimmickConversion
{
    static readonly int[] ChannelTypes = {
        ChannelType.Square1, ChannelType.Square2, ChannelType.Triangle, ChannelType.Noise,
        ChannelType.Dpcm, ChannelType.S5BSquare1, ChannelType.S5BSquare2, ChannelType.S5BSquare3
    };
    static readonly Dictionary<string, int> EnvelopeTypes = new() {
        ["Volume"] = EnvelopeType.Volume, ["Arpeggio"] = EnvelopeType.Arpeggio,
        ["Pitch"] = EnvelopeType.Pitch, ["DutyCycle"] = EnvelopeType.DutyCycle,
        ["S5BMixer"] = EnvelopeType.S5BMixer, ["S5BNoiseFreq"] = EnvelopeType.S5BNoiseFreq
    };

    static int Int(JsonElement item, string name, int fallback = 0) => item.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;
    static bool Bool(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.GetBoolean();
    static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    static int NoteCount(Project p) => p.Songs.Sum(s => s.Channels.Sum(c => c.Patterns.Sum(pattern => pattern.Notes.Values.Count(n => n.IsMusical))));
    static int EventCount(Project p) => p.Songs.Sum(s => s.Channels.Sum(c => c.Patterns.Sum(pattern => pattern.Notes.Count)));

    static IEnumerable<int> AudibleState(int[][] frame)
    {
        for (int channel = 0; channel < 8; channel++)
        {
            var state = frame[channel];
            if (channel == 4) continue; // DPCM is compared at each sample start below.
            int volume = channel < 2 && state[0] < 8 || channel >= 5 && state[2] == 3 ? 0 : state[1];
            yield return volume;
            if (volume == 0) continue;
            if (channel < 5 || (state[2] & 1) == 0) yield return state[0];
            if (channel < 2 || channel == 3) yield return state[2];
            if (channel >= 5)
            {
                yield return state[2];
                if ((state[2] & 2) == 0) yield return state[3];
            }
        }
    }

    public static void Run(string input, string output)
    {
        input = Path.GetFullPath(input); output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        using var document = JsonDocument.Parse(File.ReadAllBytes(input));
        var root = document.RootElement;
        var project = new Project { Name = "Gimmick! (Japan) - Editable", Author = "", Copyright = "", PalMode = false, Tuning = 440, AutoSortSongs = false };
        project.SetExpansionAudioMask(ExpansionType.S5BMask);
        foreach (var folder in new[] { "Pulse", "Triangle", "Noise", "S5B", "DPCM" }) project.CreateFolder(FolderType.Instrument, folder);

        var samples = new Dictionary<int, DPCMSample>();
        var sourceSampleIds = new Dictionary<int, int>();
        foreach (var source in root.GetProperty("samples").EnumerateArray())
        {
            int id = Int(source, "id");
            var data = Convert.FromHexString(source.GetProperty("data").GetString());
            Require(data.Length > 0 && data.Length <= DPCMSample.MaxSampleSize && (data.Length & 15) == 1, $"Invalid hardware DPCM length for source {id}.");
            var sample = project.CreateDPCMSampleFromDmcData(source.GetProperty("name").GetString(), data);
            Require(sample != null, $"Duplicate DPCM name for source {id}.");
            sample.PaddingMode = DPCMPaddingType.Unpadded;
            sample.DmcInitialValueDiv2 = Int(source, "delta", 64) / 2;
            sample.Process();
            Require(sample.ProcessedData.SequenceEqual(data), $"DPCM processing changed source bytes for sample {id}.");
            samples.Add(id, sample); sourceSampleIds.Add(sample.Id, id);
        }

        var instruments = new Dictionary<int, Instrument>();
        var dpcmKeys = new Dictionary<int, int>();
        Instrument dpcm = null;
        foreach (var source in root.GetProperty("instruments").EnumerateArray())
        {
            int id = Int(source, "id");
            if (source.TryGetProperty("sample", out var sampleId))
            {
                if (dpcm == null) { dpcm = project.CreateInstrument(ExpansionType.None, "DPCM - original samples and rates"); dpcm.FolderName = "DPCM"; }
                int note = dpcmKeys.Count + 1, pitch = Int(source, "sample_rate"), delta = Int(source, "delta", 64);
                Require(note <= 96 && pitch >= 0 && pitch <= 15 && delta >= 0 && delta <= 127, $"Invalid DPCM mapping {id}.");
                var mapping = dpcm.MapDPCMSample(note, samples[sampleId.GetInt32()], pitch, Bool(source, "sample_loop"));
                mapping.OverrideDmcInitialValue = true; mapping.DmcInitialValueDiv2 = delta / 2;
                dpcmKeys.Add(id, note); instruments.Add(id, dpcm);
                continue;
            }

            var name = source.GetProperty("name").GetString();
            int chip = Int(source, "chip");
            Require(chip == ExpansionType.None || chip == ExpansionType.S5B, $"Unexpected instrument chip {chip}.");
            var instrument = project.CreateInstrument(chip, name);
            Require(instrument != null, $"Duplicate instrument name {name}.");
            instrument.FolderName = name.Split(' ')[0];
            Require(new[] { "Pulse", "Triangle", "Noise", "S5B" }.Contains(instrument.FolderName), $"Unexpected instrument family {name}.");
            foreach (var entry in source.GetProperty("envelopes").EnumerateObject())
            {
                Require(EnvelopeTypes.TryGetValue(entry.Name, out var type), $"Unknown envelope type {entry.Name}.");
                var envelope = instrument.Envelopes[type];
                Require(envelope != null, $"Envelope {entry.Name} is unavailable for {name}.");
                var values = entry.Value.GetProperty("values").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                int loop = Int(entry.Value, "loop", -1), release = Int(entry.Value, "release", -1);
                Require(values.Length <= envelope.MaxLength && values.All(v => v >= sbyte.MinValue && v <= sbyte.MaxValue), $"Envelope values exceed exact storage limits: {name}/{entry.Name}.");
                Require(loop >= -1 && loop < values.Length && release >= -1 && release < values.Length, $"Invalid envelope loop/release: {name}/{entry.Name}.");
                Require(release < 0 || (envelope.CanRelease && loop >= 0 && release > loop), $"Unrepresentable release envelope: {name}/{entry.Name}.");
                envelope.Length = values.Length;
                for (int i = 0; i < values.Length; i++) envelope.Values[i] = (sbyte)values[i];
                envelope.Loop = loop;
                if (release >= 0) envelope.Release = release;
                envelope.Relative = Bool(entry.Value, "relative");
                Require(envelope.Length == values.Length && envelope.Loop == loop && envelope.Release == release, $"Envelope setters changed source structure: {name}/{entry.Name}.");
            }
            instruments.Add(id, instrument);
        }

        foreach (var curve in root.GetProperty("curves").EnumerateArray())
            project.ChipTuning.SetValue(Int(curve, "chip"), Int(curve, "note"), Int(curve, "period"));

        var songMetadata = new List<(int Id, Song Song, int Frames, int Loop, int Notes)>();
        int sourceNotes = 0, loopStopEvents = 0;
        foreach (var source in root.GetProperty("songs").EnumerateArray())
        {
            int sourceId = Int(source, "id"), frames = Int(source, "frames"), loop = Int(source, "loop", -1);
            Require(frames > 0 && (loop < 0 || loop < frames), $"Invalid song span {sourceId}.");
            var boundaries = new SortedSet<int> { 0, frames };
            for (int frame = 256; frame < frames; frame += 256) boundaries.Add(frame);
            if (loop >= 0) boundaries.Add(loop);
            var starts = boundaries.ToArray();
            Require(starts.Length - 1 <= Song.MaxLength, $"Song {sourceId} exceeds the pattern timeline limit.");
            var song = project.CreateSong(source.GetProperty("name").GetString());
            song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
            song.SetDefaultPatternLength(256); song.SetBeatLength(16); song.SetLength(starts.Length - 1);
            for (int i = 0; i < starts.Length - 1; i++)
            {
                int length = starts[i + 1] - starts[i];
                if (length != 256) song.SetPatternCustomSettings(i, length, Math.Min(16, length), new[] { 1 });
            }
            song.SetLoopPoint(loop < 0 ? -1 : Array.IndexOf(starts, loop));
            int count = 0;
            foreach (var sourceNote in source.GetProperty("notes").EnumerateArray())
            {
                int channel = Int(sourceNote, "channel"), start = Int(sourceNote, "start"), duration = Int(sourceNote, "duration"), release = Int(sourceNote, "release", -1);
                int originalInstrument = Int(sourceNote, "instrument"), value = Int(sourceNote, "note"), fine = Int(sourceNote, "fine");
                Require(channel >= 0 && channel < ChannelTypes.Length && start >= 0 && start < frames && duration > 0 && duration <= ushort.MaxValue && start + duration <= frames, $"Invalid note span in song {sourceId}.");
                Require(release == -1 || (release > 0 && release < duration), $"Unrepresentable note release in song {sourceId}.");
                Require(fine >= sbyte.MinValue && fine <= sbyte.MaxValue, $"Fine pitch cannot be stored exactly in song {sourceId}.");
                if (channel == 4) value = dpcmKeys[originalInstrument];
                Require(value >= 1 && value <= 96, $"Note outside editor range in song {sourceId}.");
                int patternIndex = Array.BinarySearch(starts, start);
                if (patternIndex < 0) patternIndex = ~patternIndex - 1;
                var pattern = song.GetChannelByType(ChannelTypes[channel]).GetOrCreatePattern(patternIndex);
                int row = start - starts[patternIndex];
                Require(!pattern.Notes.ContainsKey(row), $"Overlapping note starts in song {sourceId}, channel {channel}, frame {start}.");
                var note = new Note(value) { Instrument = instruments[originalInstrument], Duration = duration };
                if (release >= 1) note.Release = release;
                if (channel != 4) note.FinePitch = (sbyte)fine;
                if (Bool(sourceNote, "reset_phase")) note.PhaseReset = 1;
                if (sourceNote.TryGetProperty("delta", out var delta))
                {
                    Require(delta.GetInt32() >= 0 && delta.GetInt32() <= 127, "Invalid DPCM delta.");
                    note.DeltaCounter = (byte)delta.GetInt32();
                }
                pattern.Notes.Add(row, note); count++;
            }
            if (source.TryGetProperty("loop_stop_channels", out var stopChannels))
            {
                Require(loop >= 0 || stopChannels.GetArrayLength() == 0, "Loop stops require a valid loop point.");
                foreach (var stopChannel in stopChannels.EnumerateArray())
                {
                    int channel = stopChannel.GetInt32();
                    Require(channel >= 0 && channel < ChannelTypes.Length && channel != 4, "Invalid loop stop channel.");
                    var pattern = song.GetChannelByType(ChannelTypes[channel]).GetOrCreatePattern(Array.IndexOf(starts, loop));
                    Require(!pattern.Notes.ContainsKey(0), $"Loop stop overlaps a musical event: {sourceId}/{channel}.");
                    // Upstream intentionally holds notes whose duration ends at the
                    // song boundary. Explicit silence is required on a silent loop entry.
                    pattern.Notes.Add(0, new Note(Note.NoteStop));
                    loopStopEvents++;
                }
            }
            song.UpdatePatternStartNotes(); song.InvalidateCumulativePatternCache();
            Require(song.GetPatternStartAbsoluteNoteIndex(song.Length) == frames, $"Song {sourceId} pattern boundaries changed frame length.");
            songMetadata.Add((sourceId, song, frames, loop, count)); sourceNotes += count;
        }

        Require(NoteCount(project) == sourceNotes && EventCount(project) == sourceNotes + loopStopEvents, "Lost notes during event construction.");
        project.ValidateIntegrity();
        string fms = Path.Combine(output, "Gimmick-Editable.fms"), text = Path.Combine(output, "Gimmick-Inspection.txt");
        Require(new ProjectFile().Save(project, fms), "FMS save failed.");
        Require(new FamistudioTextFile().Save(project, text, project.Songs.Select(s => s.Id).ToArray(), false), "Text project save failed.");
        var restored = new ProjectFile().Load(fms);
        var textRestored = new FamistudioTextFile().Load(text);
        var textPitchLimitations = new List<string>();
        foreach (var roundtrip in new[] { restored, textRestored })
        {
            Require(roundtrip != null && roundtrip.Songs.Count == project.Songs.Count && roundtrip.Instruments.Count == project.Instruments.Count && roundtrip.Samples.Count == project.Samples.Count && NoteCount(roundtrip) == sourceNotes && EventCount(roundtrip) == EventCount(project), "FMS/text round trip changed structural counts.");
            Require(roundtrip.Songs.Select(s => s.Name).SequenceEqual(project.Songs.Select(s => s.Name)), "Round trip changed soundtrack order.");
            foreach (var sample in project.Samples)
                Require(roundtrip.GetSample(sample.Name).ProcessedData.SequenceEqual(sample.ProcessedData), "Round trip changed DPCM bytes.");
            foreach (var instrument in project.Instruments)
            {
                var other = roundtrip.GetInstrument(instrument.Name);
                if (other.ComputeCRC() != instrument.ComputeCRC())
                {
                    Require(!ReferenceEquals(roundtrip, restored), $"Binary round trip changed instrument {instrument.Name}.");
                    for (int type = 0; type < EnvelopeType.Count; type++)
                    {
                        var a = instrument.Envelopes[type]; var b = other.Envelopes[type];
                        if (a != null && b != null && a.CRC != b.CRC)
                        {
                            // Upstream text export omits constant defaults, and its importer
                            // clamps pitch envelopes to [-64,63]. The game uses five exact
                            // signed-byte triangle pitch envelopes outside that UI range.
                            if (a.IsEmpty(type) && b.IsEmpty(type)) continue;
                            Require(type == EnvelopeType.Pitch && a.Length == b.Length && a.Loop == b.Loop && a.Release == b.Release && a.Relative == b.Relative &&
                                a.Values.Take(a.Length).Select(v => (sbyte)Math.Clamp((int)v, -64, 63)).SequenceEqual(b.Values.Take(b.Length)),
                                $"Unexpected text inspection difference: {instrument.Name}/{EnvelopeType.InternalNames[type]}.");
                            textPitchLimitations.Add(instrument.Name);
                        }
                    }
                }
            }
            Require(roundtrip.ChipTuning.ContentEquals(project.ChipTuning), "Round trip changed period curves.");
        }

        // A text project is an editable counterpart only when its runtime behavior
        // survives the upstream importer. The original decoder's uncommon pitch
        // envelopes must first be normalized by the external decoder.
        bool textEditable = textPitchLimitations.Count == 0;
        if (textEditable)
        {
            var editableText = Path.Combine(output, "Gimmick-Editable.txt");
            File.Move(text, editableText, true);
            text = editableText;
        }

        var traceFolder = Path.Combine(output, "traces"); Directory.CreateDirectory(traceFolder);
        var traces = new List<object>();
        int loopTracks = 0, loopFrames = 0;
        foreach (var entry in songMetadata)
        {
            bool preview = entry.Id == 0x3d || entry.Id == 0x06 || entry.Id == 0x3c;
            var player = new TracePlayer(preview ? 600 : 0);
            player.Render(restored.GetSong(entry.Song.Name));
            Require(player.Frames.Count == entry.Frames, $"Playback frame count differs for {entry.Song.Name}: {player.Frames.Count} != {entry.Frames}.");
            if (textEditable)
            {
                var textPlayer = new TracePlayer(0);
                textPlayer.Render(textRestored.GetSong(entry.Song.Name));
                Require(textPlayer.Frames.Count == player.Frames.Count, $"Text round trip changed playback length: {entry.Song.Name}.");
                for (int frame = 0; frame < player.Frames.Count; frame++)
                {
                    Require(player.Frames[frame].SelectMany(c => c).SequenceEqual(textPlayer.Frames[frame].SelectMany(c => c)),
                        $"Text round trip changed playback registers: {entry.Song.Name}, frame {frame}.");
                    Require(player.Writes[frame].SelectMany(w => w.Take(2)).SequenceEqual(textPlayer.Writes[frame].SelectMany(w => w.Take(2))),
                        $"Text round trip changed register write timing: {entry.Song.Name}, frame {frame}.");
                    int binarySample = player.DpcmSamples[frame], textSample = textPlayer.DpcmSamples[frame];
                    Require((binarySample < 0 && textSample < 0) || (binarySample >= 0 && textSample >= 0 &&
                        restored.GetSample(binarySample).Name == textRestored.GetSample(textSample).Name),
                        $"Text round trip changed DPCM selection: {entry.Song.Name}, frame {frame}.");
                }
            }
            if (entry.Loop >= 0)
            {
                var loopPlayer = new TracePlayer(0);
                loopPlayer.Render(restored.GetSong(entry.Song.Name), 2);
                Require(loopPlayer.Frames.Count == entry.Frames * 2 - entry.Loop, $"Loop playback length changed: {entry.Song.Name}.");
                for (int offset = 0; offset < entry.Frames - entry.Loop; offset++)
                {
                    int first = entry.Loop + offset, second = entry.Frames + offset;
                    Require(AudibleState(loopPlayer.Frames[first]).SequenceEqual(AudibleState(loopPlayer.Frames[second])),
                        $"Loop changed audible register state: {entry.Song.Name}, offset {offset}; first {JsonSerializer.Serialize(loopPlayer.Frames[first])}; second {JsonSerializer.Serialize(loopPlayer.Frames[second])}.");
                    for (int channel = 0; channel < 2; channel++)
                    {
                        var state = loopPlayer.Frames[first][channel];
                        if (state[0] >= 8 && state[1] > 0)
                            Require(loopPlayer.Writes[first].Any(w => w[0] == 0x4003 + channel * 4) == loopPlayer.Writes[second].Any(w => w[0] == 0x4003 + channel * 4),
                                $"Loop changed pulse phase reset: {entry.Song.Name}, offset {offset}, channel {channel}.");
                    }
                    var firstDmc = loopPlayer.Writes[first].Where(w => w[0] == 0x4012).Select(w => w[2]);
                    var secondDmc = loopPlayer.Writes[second].Where(w => w[0] == 0x4012).Select(w => w[2]);
                    Require(firstDmc.SequenceEqual(secondDmc), $"Loop changed DPCM start: {entry.Song.Name}, offset {offset}.");
                    if (firstDmc.Any())
                        Require(loopPlayer.Frames[first][4].SequenceEqual(loopPlayer.Frames[second][4]), $"Loop changed DPCM parameters: {entry.Song.Name}, offset {offset}.");
                    loopFrames++;
                }
                loopTracks++;
            }
            var tracePath = Path.Combine(traceFolder, $"{entry.Id:x2}.fms.json.gz");
            using (var file = File.Create(tracePath))
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
                JsonSerializer.Serialize(gzip, new { source_id = entry.Id, name = entry.Song.Name, frames = entry.Frames, loop = entry.Loop,
                    register_frames = player.Frames, flat_register_frames = player.RawFrames, writes = player.Writes,
                    sample_ids = sourceSampleIds, dpcm_sample_frames = player.DpcmSamples });
            if (preview) WaveFile.Save(player.Audio.ToArray(), Path.Combine(output, $"fms-{entry.Id:x2}-10s.wav"), 44100, 1);
            traces.Add(new { id = entry.Id, name = entry.Song.Name, frames = entry.Frames, loop = entry.Loop, notes = entry.Notes, patterns = entry.Song.Length, trace = Path.GetFileName(tracePath) });
            Console.WriteLine($"PASS Gimmick playback {entry.Id:x2} {entry.Song.Name}: {entry.Frames} frames, {entry.Notes} notes");
        }
        var summary = new {
            source_sha256 = root.GetProperty("source_sha256").GetString(), input_json = input, project_format = Project.Version,
            songs = project.Songs.Count, source_instrument_variants = root.GetProperty("instruments").GetArrayLength(),
            instruments = project.Instruments.Count, dpcm_mappings = dpcmKeys.Count, samples = project.Samples.Count,
            notes = sourceNotes, loop_stop_events = loopStopEvents, curve_entries = root.GetProperty("curves").GetArrayLength(), curve_unit = "absolute hardware period/register",
            family_counts = project.Instruments.GroupBy(i => i.FolderName).ToDictionary(g => g.Key, g => g.Count()),
            binary_roundtrip_exact = true, text_structural_counts_match = true, text_inspection_only = !textEditable,
            text_playback_registers_exact = textEditable, text_file = Path.GetFileName(text),
            text_pitch_envelopes_that_reimport_would_clamp = textPitchLimitations, exact_dpcm_bytes = true, playback_frame_counts = true,
            repeated_loop_tracks = loopTracks, repeated_loop_frames = loopFrames, repeated_loop_audible_registers_and_phase_exact = true,
            register_comparison = "Pending independent comparison with original ROM traces; these files contain actual FMS playback registers.",
            tracks = traces
        };
        File.WriteAllText(Path.Combine(output, "conversion-report.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Converted {project.Songs.Count} songs, {project.Instruments.Count} instruments, {project.Samples.Count} samples, {sourceNotes} event notes.");
    }

    sealed class TracePlayer : BasePlayer
    {
        readonly Dictionary<int, int> registers = new();
        readonly List<int[]> currentWrites = new();
        readonly int previewFrames;
        int s5bAddress, sampleId = -1;
        public readonly List<int[][]> Frames = new();
        public readonly List<Dictionary<int, int>> RawFrames = new();
        public readonly List<int[][]> Writes = new();
        public readonly List<int> DpcmSamples = new();
        public readonly List<short> Audio = new();
        public TracePlayer(int preview) : base(NesApu.APU_WAV_EXPORT, false, false) { Loop = LoopMode.None; previewFrames = preview; }
        int R(int register) => registers.TryGetValue(register, out var value) ? value : 0;
        int S(int register) => R(0x5000 + register);
        public override void NotifyRegisterWrite(int apu, int register, int value, int metadata = 0)
        {
            if (register == NesApu.S5B_ADDR) s5bAddress = value & 15;
            else if (register == NesApu.S5B_DATA)
            {
                registers[0x5000 + s5bAddress] = value;
                currentWrites.Add(new[] { 0x5000 + s5bAddress, value, metadata });
            }
            else if (register >= 0x4000 && register <= 0x4017)
            {
                registers[register] = value;
                currentWrites.Add(new[] { register, value, metadata });
                if (register == 0x4012 && metadata != 0) sampleId = metadata;
            }
        }
        protected override short[] EndFrame()
        {
            var audio = base.EndFrame();
            if (Frames.Count < previewFrames) Audio.AddRange(audio);
            var frame = new int[8][];
            for (int channel = 0; channel < 2; channel++)
            {
                int reg = 0x4000 + channel * 4, raw = R(reg), period = R(reg + 2) | ((R(reg + 3) & 7) << 8);
                frame[channel] = new[] { period, raw & 15, raw >> 6, raw };
            }
            frame[2] = new[] { R(0x400a) | ((R(0x400b) & 7) << 8), (R(0x4008) & 127) != 0 ? 15 : 0, 0, R(0x4008) };
            frame[3] = new[] { R(0x400e) & 15, R(0x400c) & 15, (R(0x400e) >> 7) & 1, R(0x400c) };
            frame[4] = new[] { R(0x4010), R(0x4011), R(0x4012), R(0x4013), 0, R(0x4015) };
            for (int channel = 0; channel < 3; channel++)
                frame[5 + channel] = new[] { S(channel * 2) | ((S(channel * 2 + 1) & 15) << 8), S(8 + channel),
                    ((S(7) >> channel) & 1) | (((S(7) >> (channel + 3)) & 1) << 1), S(6), S(11) | (S(12) << 8), S(13) };
            Frames.Add(frame); RawFrames.Add(new Dictionary<int, int>(registers)); Writes.Add(currentWrites.ToArray()); DpcmSamples.Add(sampleId);
            currentWrites.Clear();
            return audio;
        }
        public void Render(Song song, int passes = 1)
        {
            if (passes > 1) { Loop = LoopMode.Song; maxLoopCount = passes; }
            try { BeginPlaySong(song); while (PlaySongFrame()) { } }
            finally { Shutdown(); }
        }
    }
}
