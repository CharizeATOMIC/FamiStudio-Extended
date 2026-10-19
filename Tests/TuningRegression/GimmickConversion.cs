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
        ["S5BMixer"] = EnvelopeType.S5BMixer, ["S5BNoiseFreq"] = EnvelopeType.S5BNoiseFreq,
        ["NoisePeriod"] = EnvelopeType.NoisePeriod, ["NoiseEnvelope"] = EnvelopeType.NoiseEnvelope,
        ["NoiseEnvelopeRestart"] = EnvelopeType.NoiseEnvelopeRestart
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
        Settings.SquareSmoothVibrato = false;
        input = Path.GetFullPath(input); output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        using var document = JsonDocument.Parse(File.ReadAllBytes(input));
        var root = document.RootElement;
        bool structuredLoops = root.TryGetProperty("layout_revision", out _);
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
            if (project.GetInstrument(name) != null) name += $" / variation {id}";
            int chip = Int(source, "chip");
            Require(chip == ExpansionType.None || chip == ExpansionType.S5B, $"Unexpected instrument chip {chip}.");
            var instrument = project.CreateInstrument(chip, name);
            Require(instrument != null, $"Duplicate instrument name {name}.");
            instrument.ResetPhaseOnAttack = Bool(source, "reset_phase_on_attack");
            instrument.PitchClampToTimerByte = Bool(source, "pitch_clamp_to_timer_byte");
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
            var tempos = source.TryGetProperty("tempo_segments", out var tempoArray) ? tempoArray.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
            if (tempos.Length == 0)
                for (int frame = 256; frame < frames; frame += 256) boundaries.Add(frame);
            for (int t = 0; t < tempos.Length; t++)
            {
                var tempo = tempos[t];
                int from = Int(tempo, "frame"), to = t + 1 < tempos.Length ? Int(tempos[t + 1], "frame") : frames;
                int origin = Int(tempo, "beat_grid_origin", from), bar = Int(tempo, "quarter_frames") * Int(tempo, "inferred_beats_per_bar", 4);
                boundaries.Add(from);
                int firstEnd = loop >= origin && loop < to ? loop : to;
                for (int frame = origin; frame < firstEnd; frame += bar) boundaries.Add(frame);
                if (firstEnd < to) for (int frame = firstEnd; frame < to; frame += bar) boundaries.Add(frame);
            }
            if (loop >= 0) boundaries.Add(loop);
            foreach (var group in source.GetProperty("notes").EnumerateArray().GroupBy(n => Int(n, "channel")))
            {
                int previousDelay = -1;
                foreach (var n in group.OrderBy(n => Int(n, "start")))
                {
                    int delay = Int(n, "playback_delay");
                    if (previousDelay >= 0 && delay != previousDelay) boundaries.Add(Int(n, "start"));
                    previousDelay = delay;
                }
            }
            var starts = boundaries.ToArray();
            Require(starts.Length - 1 <= Song.MaxLength, $"Song {sourceId} exceeds the pattern timeline limit.");
            var song = project.CreateSong(source.GetProperty("name").GetString());
            int baseBeat = tempos.Length > 0 ? Int(tempos[0], "quarter_frames") : 16;
            int baseLength = tempos.Length > 0 ? baseBeat * Int(tempos[0], "inferred_beats_per_bar", 4) : 256;
            int baseGrid = tempos.Length > 0 ? tempos[0].GetProperty("constant_groove_preserving_all_frames")[0].GetInt32() : 1;
            song.ChangeFamiStudioTempoGroove(new[] { baseGrid }, false);
            song.SetDefaultPatternLength(baseLength); song.SetBeatLength(baseBeat); song.SetLength(starts.Length - 1);
            for (int i = 0; i < starts.Length - 1; i++)
            {
                int length = starts[i + 1] - starts[i];
                int beat = baseBeat, grid = 1;
                if (tempos.Length > 0)
                {
                    var tempo = tempos.Last(t => Int(t, "frame") <= starts[i]);
                    beat = Int(tempo, "quarter_frames");
                    grid = tempo.GetProperty("constant_groove_preserving_all_frames")[0].GetInt32();
                    while (grid > 1 && (length % grid != 0 || beat % grid != 0)) grid--;
                }
                song.SetPatternCustomSettings(i, length, beat, new[] { grid });
            }
            song.SetLoopPoint(loop < 0 ? -1 : Array.IndexOf(starts, loop));
            int PatternIndex(int time) { int i = Array.BinarySearch(starts, time); return i < 0 ? ~i - 1 : i; }
            bool SameNote(Note a, Note b) => a.Value == b.Value && a.Instrument == b.Instrument && a.Flags == b.Flags &&
                a.Duration == b.Duration && a.Release == b.Release && a.EffectMask == b.EffectMask &&
                Enumerable.Range(0, Note.EffectCount).All(f => !a.HasValidEffectValue(f) || a.GetEffectValue(f) == b.GetEffectValue(f));
            var firstSource = source.GetProperty("notes").EnumerateArray().OrderBy(n => Int(n, "start")).ToArray();
            Pattern[][] BuildPatterns(JsonElement[] sourceNotes, bool repeat)
            {
                var result = Enumerable.Range(0, 8).Select(_ => new Pattern[song.Length]).ToArray();
                var previousFine = new int[8];
                var previousDuty = new int[8];
                var previousVolume = Enumerable.Repeat(15, 8).ToArray();
                var firstLoopNote = new bool[8];
                if (repeat)
                    for (int c = 0; c < 8; c++)
                    {
                        var tail = firstSource.LastOrDefault(n => Int(n, "channel") == c);
                        if (tail.ValueKind != JsonValueKind.Undefined) { previousFine[c] = Int(tail, "fine"); previousVolume[c] = Int(tail, "volume", 15); }
                        foreach (var previous in firstSource.Where(n => Int(n, "channel") == c && n.TryGetProperty("duty_cycle", out _)))
                            previousDuty[c] = Int(previous, "duty_cycle");
                    }
                Pattern Ensure(int channel, int time, int delay)
                {
                    int p = PatternIndex(time);
                    if (result[channel][p] == null)
                    {
                        var owner = song.GetChannelByType(ChannelTypes[channel]);
                        result[channel][p] = repeat ? owner.CreatePattern() : owner.GetOrCreatePattern(p);
                        result[channel][p].PlaybackDelay = delay;
                        result[channel][p].Name = $"{(repeat ? "Loop " : "")}{p + 1:D2}";
                    }
                    Require(result[channel][p].PlaybackDelay == delay, $"Conflicting pattern delays: {sourceId}/{channel}/{p}.");
                    return result[channel][p];
                }
                foreach (var n in sourceNotes)
                {
                    int c = Int(n, "channel"), time = Int(n, "start"), duration = Int(n, "duration"), release = Int(n, "release", -1);
                    int id = Int(n, "instrument"), value = Int(n, "note"), fine = Int(n, "fine"), delay = Int(n, "playback_delay");
                    Require(c >= 0 && c < 8 && time >= 0 && time < frames && duration > 0 && duration <= ushort.MaxValue, $"Invalid note in {sourceId}.");
                    Require(!repeat || time >= loop, "Variant note precedes the musical loop.");
                    Require(release == -1 || release > 0 && release < duration, $"Invalid release in {sourceId}.");
                    Require(fine >= -128 && fine <= 127, "Fine pitch cannot be represented.");
                    if (c == 4) value = dpcmKeys[id];
                    Require(value >= 1 && value <= 96, "Note outside keyboard range.");
                    var pattern = Ensure(c, time, delay);
                    int row = time - starts[PatternIndex(time)];
                    Require(!pattern.Notes.ContainsKey(row), $"Duplicate source attack: {sourceId}/{c}/{time}.");
                    var note = new Note(value) { Instrument = instruments[id], Duration = duration };
                    if (release > 0) note.Release = release;
                    bool entry = loop >= 0 && time >= loop && !firstLoopNote[c];
                    bool fineReset = fine != previousFine[c];
                    if (entry)
                    {
                        var tail = sourceNotes.Last(t => Int(t, "channel") == c);
                        fineReset |= Int(tail, "fine") != fine;
                        firstLoopNote[c] = true;
                    }
                    if (c != 3 && c != 4 && fineReset) note.FinePitch = (sbyte)fine;
                    previousFine[c] = fine;
                    if (n.TryGetProperty("volume", out var volume))
                    {
                        int v = volume.GetInt32();
                        int tailVolume = entry ? Int(sourceNotes.Last(t => Int(t, "channel") == c), "volume", 15) : v;
                        if (v != previousVolume[c] || tailVolume != v) note.Volume = (byte)v;
                        previousVolume[c] = v;
                    }
                    if (n.TryGetProperty("duty_cycle", out var duty))
                    {
                        int d = duty.GetInt32(), tailDuty = d;
                        if (entry)
                            foreach (var tail in sourceNotes.Where(t => Int(t, "channel") == c && t.TryGetProperty("duty_cycle", out _)))
                                tailDuty = Int(tail, "duty_cycle");
                        if (d != previousDuty[c] || tailDuty != d) note.DutyCycle = (byte)d;
                        previousDuty[c] = d;
                    }
                    if (Bool(n, "no_attack")) note.HasAttack = false;
                    if (Bool(n, "reset_phase") && !(note.HasAttack && note.Instrument.ResetPhaseOnAttack)) note.PhaseReset = 1;
                    if (n.TryGetProperty("delta", out var delta)) note.DeltaCounter = (byte)delta.GetInt32();
                    pattern.Notes.Add(row, note);
                }
                foreach (var n in sourceNotes)
                {
                    if (!n.TryGetProperty("envelope_decay_events", out var effects)) continue;
                    int c = Int(n, "channel"), delay = Int(n, "playback_delay");
                    foreach (var effect in effects.EnumerateArray())
                    {
                        int time = Int(n, "start") + Int(effect, "offset");
                        if (time >= frames)
                        {
                            if (!repeat || loop < 0) continue;
                            time = loop + (time - frames) % (frames - loop);
                        }
                        var pattern = Ensure(c, time, delay);
                        var note = pattern.GetOrCreateNoteAt(time - starts[PatternIndex(time)]);
                        if (note.HasEnvelopeDecay) Require(note.EnvelopeDecay == Int(effect, "period"), "Conflicting decay controls.");
                        note.EnvelopeDecay = (byte)Int(effect, "period");
                    }
                }
                return result;
            }
            var firstPatterns = BuildPatterns(firstSource, false);
            int count = firstSource.Length;
            Require(firstPatterns.SelectMany(c => c).Where(p => p != null).Sum(p => p.Notes.Values.Count(n => n.IsMusical)) == count, "Lost source attacks.");
            if (loop >= 0 && source.TryGetProperty("loop_notes", out var loopSource))
            {
                var repeatPatterns = BuildPatterns(loopSource.EnumerateArray().OrderBy(n => Int(n, "start")).ToArray(), true);
                for (int c = 0; c < 8; c++)
                for (int p = song.LoopPoint; p < song.Length; p++)
                {
                    var first = firstPatterns[c][p]; var next = repeatPatterns[c][p];
                    var owner = song.GetChannelByType(ChannelTypes[c]);
                    bool equal = first == null && next == null || first != null && next != null && first.PlaybackDelay == next.PlaybackDelay &&
                        first.Notes.Count == next.Notes.Count && first.Notes.All(kv => next.Notes.TryGetValue(kv.Key, out var other) && SameNote(kv.Value, other));
                    if (equal) { if (next != null) owner.Patterns.Remove(next); continue; }
                    if (first == null) first = owner.GetOrCreatePattern(p);
                    if (next == null) { next = owner.CreatePattern(); next.PlaybackDelay = first.PlaybackDelay; next.Name = $"Loop {p + 1:D2}"; }
                    first.LoopVariant = next;
                }
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
                    if (pattern.LoopVariant != null)
                    {
                        Require(!pattern.LoopVariant.Notes.TryGetValue(0, out var existingStop) || !existingStop.IsMusical, "Loop stop overlaps a variant attack.");
                        var stop = pattern.LoopVariant.GetOrCreateNoteAt(0);
                        stop.Value = Note.NoteStop;
                    }
                    loopStopEvents++;
                }
            }
            song.UpdatePatternStartNotes(); song.InvalidateCumulativePatternCache();
            Require(song.GetPatternStartAbsoluteNoteIndex(song.Length) == frames, $"Song {sourceId} pattern boundaries changed frame length.");
            songMetadata.Add((sourceId, song, frames, loop, count)); sourceNotes += count;
        }

        int firstPassSourceNotes = sourceNotes;
        sourceNotes = NoteCount(project); // Includes editable loop-variation patterns.
        int loopVariationPatterns = project.Songs.Sum(s => s.Channels.Sum(c => c.Patterns.Count(p => p.LoopVariant != null)));
        int phaseEffectsBefore = project.Songs.Sum(s => s.Channels.Sum(c => c.Patterns.Sum(p => p.Notes.Values.Count(n => n.HasPhaseReset))));
        int removedAutomaticPhaseEffects = 0;
        foreach (var entry in songMetadata)
        {
            var phaseTrace = new TracePlayer(0);
            phaseTrace.Render(entry.Song, entry.Loop < 0 ? 1 : 2);
            for (int c = 0; c < 2; c++)
            for (int p = 0; p < entry.Song.Length; p++)
            {
                var pattern = entry.Song.Channels[c].PatternInstances[p];
                if (pattern == null) continue;
                int patternStart = entry.Song.GetPatternStartAbsoluteNoteIndex(p);
                foreach (bool variation in new[] { false, true })
                {
                    var selected = variation ? pattern.LoopVariant : pattern;
                    if (selected == null) continue;
                    foreach (var kv in selected.Notes.Where(kv => kv.Value.HasPhaseReset))
                    {
                        var occurrences = new List<int>();
                        int at = patternStart + kv.Key + selected.PlaybackDelay;
                        if (!variation) occurrences.Add(at);
                        if (entry.Loop >= 0 && p >= entry.Song.LoopPoint && (variation || pattern.LoopVariant == null))
                            occurrences.Add(at + entry.Frames - entry.Loop);
                        if (occurrences.Count > 0 && occurrences.All(f => f < phaseTrace.Writes.Count && phaseTrace.Writes[f].Count(w => w[0] == 0x4003 + c * 4) >= 2))
                        {
                            kv.Value.HasPhaseReset = false;
                            removedAutomaticPhaseEffects++;
                        }
                    }
                }
            }
        }
        int patternsBeforeSharing = project.Songs.Sum(s => s.Channels.Sum(c => c.Patterns.Count));
        int notesBeforeSharing = NoteCount(project);
        foreach (var song in project.Songs)
            song.MergeIdenticalPatterns();
        int sharedPatternsRemoved = patternsBeforeSharing - project.Songs.Sum(s => s.Channels.Sum(c => c.Patterns.Count));
        sourceNotes = NoteCount(project);
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
                var loopPlayer = new TracePlayer(preview ? 600 : 0);
                loopPlayer.Render(restored.GetSong(entry.Song.Name), 2);
                Require(loopPlayer.Frames.Count == entry.Frames * 2 - entry.Loop, $"Loop playback length changed: {entry.Song.Name}.");
                for (int offset = 0; !structuredLoops && offset < entry.Frames - entry.Loop; offset++)
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
                if (structuredLoops)
                {
                    // Real source loops may carry echo tails and controller
                    // state from the previous pass. Compare both passes to the
                    // continuously executing ROM, not to each other.
                    player = loopPlayer;
                    loopFrames += entry.Frames - entry.Loop;
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
            notes = sourceNotes, first_pass_source_notes = firstPassSourceNotes, loop_variation_patterns = loopVariationPatterns, loop_stop_events = loopStopEvents, curve_entries = root.GetProperty("curves").GetArrayLength(), curve_unit = "absolute hardware period/register",
            notes_before_identical_pattern_sharing = notesBeforeSharing, identical_patterns_shared = sharedPatternsRemoved,
            phase_effects_before = phaseEffectsBefore, redundant_automatic_phase_effects_removed = removedAutomaticPhaseEffects,
            phase_reset_effects = project.Songs.Sum(s => s.Channels.Sum(c => c.Patterns.Sum(p => p.Notes.Values.Count(n => n.HasPhaseReset)))),
            duty_cycle_effects = project.Songs.Sum(s => s.Channels.Sum(c => c.Patterns.Sum(p => p.Notes.Values.Count(n => n.HasDutyCycle)))),
            envelope_decay_effects = project.Songs.Sum(s => s.Channels.Sum(c => c.Patterns.Sum(p => p.Notes.Values.Count(n => n.HasEnvelopeDecay)))),
            patterns_with_playback_delay = project.Songs.Sum(s => s.Channels.Sum(c => c.Patterns.Count(p => p.PlaybackDelay != 0))),
            fine_pitch_effects = project.Songs.Sum(s => s.Channels.Sum(c => c.Patterns.Sum(p => p.Notes.Values.Count(n => n.HasFinePitch)))),
            family_counts = project.Instruments.GroupBy(i => i.FolderName).ToDictionary(g => g.Key, g => g.Count()),
            binary_roundtrip_exact = true, text_structural_counts_match = true, text_inspection_only = !textEditable,
            text_playback_registers_exact = textEditable, text_file = Path.GetFileName(text),
            text_pitch_envelopes_that_reimport_would_clamp = textPitchLimitations, exact_dpcm_bytes = true, playback_frame_counts = true,
            repeated_loop_tracks = loopTracks, repeated_loop_frames = loopFrames, repeated_loop_audible_registers_and_phase_exact = !structuredLoops,
            source_structural_loop_boundaries = structuredLoops, loop_validation = structuredLoops ? "Both passes captured for independent continuous ROM comparison." : "First and second loop states compared.",
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
