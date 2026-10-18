using System;
using System.Collections.Generic;
using System.Linq;

namespace FamiStudio
{
    public partial class FamitoneMusicFile
    {
        readonly Dictionary<Song, Dictionary<int, (int[] Groove, int Padding)>> compiledPlaybackTempos = new();
        sealed class PlaybackCommand
        {
            public int Frame;
            public long Row, Owner;
            public bool End;
            public Note Note;
        }

        // These are event streams, not register recordings. Instrument programs,
        // articulations and effects remain ordinary sound-engine data.
        HashSet<Song> CompilePlaybackPatterns()
        {
            compiledPlaybackTempos.Clear();
            var compiled = new HashSet<Song>();
            foreach (var song in project.Songs)
            {
                bool hasDelay = song.Channels.Any(c => c.Patterns.Any(p => p.PlaybackDelay != 0));
                bool hasVariant = song.Channels.Any(c => c.Patterns.Any(p => p.LoopVariant != null));
                if (!hasDelay && !hasVariant)
                    continue;
                if (kernel != FamiToneKernel.FamiStudio)
                    throw new InvalidOperationException("Pattern playback delay and loop variants require the FamiStudio sound engine.");
                if (song.UsesFamiTrackerTempo)
                {
                    if (hasDelay)
                        throw new InvalidOperationException($"Song '{song.Name}': sound-engine export of pattern playback delay currently requires FamiStudio tempo.");
                    CompileTrackerLoopVariants(song);
                }
                else
                {
                    CompileStudioPlayback(song);
                    compiled.Add(song); // Durations have already become explicit owner-aware commands.
                }
            }
            return compiled;
        }

        void CompileTrackerLoopVariants(Song song)
        {
            if (song.LoopPoint < 0)
                return;
            int oldLength = song.Length, oldLoop = song.LoopPoint;
            int newLength = oldLength + oldLength - oldLoop;
            if (newLength > Song.MaxLength)
                throw new InvalidOperationException($"Song '{song.Name}': the first-pass and loop-variant arrangement exceeds the sound-engine pattern limit.");
            var settings = Enumerable.Range(0, oldLength).Select(p => song.GetPatternCustomSettings(p).Clone()).ToArray();
            var instances = song.Channels.Select(c => c.PatternInstances.Take(oldLength).ToArray()).ToArray();
            song.SetLength(newLength);
            for (int p = oldLength; p < newLength; p++)
            {
                int source = oldLoop + p - oldLength;
                if (settings[source].useCustomSettings)
                    song.SetPatternCustomSettings(p, settings[source].patternLength, settings[source].beatLength);
                song.GetPatternCustomSettings(p).Tuning = settings[source].Tuning.Clone();
                for (int c = 0; c < song.Channels.Length; c++)
                {
                    var pattern = instances[c][source]?.GetPlaybackPattern(true);
                    if (pattern != null)
                    {
                        var copy = song.Channels[c].CreatePattern();
                        foreach (var note in pattern.Notes)
                            copy.Notes.Add(note.Key, note.Value.Clone());
                        song.Channels[c].PatternInstances[p] = copy;
                    }
                }
            }
            foreach (var channel in song.Channels)
                foreach (var pattern in channel.Patterns)
                    pattern.LoopVariant = null;
            song.SetLoopPoint(oldLength);
        }

        static Note MergePlaybackCommands(Note previous, Note next)
        {
            if (previous == null)
                return next.Clone();
            var result = next.IsValid ? next.Clone() : previous.Clone();
            if (next.Instrument != null)
                result.Instrument = next.Instrument;
            else if (result.Instrument == null)
                result.Instrument = previous.Instrument;
            for (int effect = 0; effect < Note.EffectCount; effect++)
            {
                int bit = 1 << effect;
                if ((next.EffectMask & bit) != 0)
                    result.SetEffectValue(effect, next.GetEffectValue(effect));
                else if ((previous.EffectMask & bit) != 0)
                    result.SetEffectValue(effect, previous.GetEffectValue(effect));
            }
            return result;
        }

        static bool SamePlaybackCommand(Note a, Note b)
        {
            if (a == null || b == null)
                return a == b;
            if (a.Value != b.Value || a.Flags != b.Flags || a.Instrument != b.Instrument ||
                a.Arpeggio != b.Arpeggio || a.EffectMask != b.EffectMask || a.SlideNoteTarget != b.SlideNoteTarget)
                return false;
            for (int effect = 0; effect < Note.EffectCount; effect++)
                if ((a.EffectMask & (1 << effect)) != 0 && a.GetEffectValue(effect) != b.GetEffectValue(effect))
                    return false;
            return true;
        }

        void CompileStudioPlayback(Song song)
        {
            int originalLength = song.Length;
            int loopPattern = song.LoopPoint;
            bool looping = loopPattern >= 0;
            var sourcePatterns = song.Channels.Select(c => c.PatternInstances.Take(originalLength).ToArray()).ToArray();
            var sourceTunings = Enumerable.Range(0, originalLength).Select(p => song.GetPatternCustomSettings(p).Tuning.Clone()).ToArray();
            var sourceGrooves = Enumerable.Range(0, originalLength).Select(p => (Groove: song.GetPatternGroove(p).ToArray(), Padding: song.GetPatternGroovePaddingMode(p))).ToArray();
            var rows = new List<(int Pattern, int Note, int Frame)>();
            var patternFrames = new int[originalLength + 1];
            var patternRows = new int[originalLength + 1];
            int frame = 0;
            for (int p = 0; p < originalLength; p++)
            {
                patternFrames[p] = frame;
                patternRows[p] = rows.Count;
                var groove = song.GetPatternGroove(p);
                int padding = song.GetPatternGroovePaddingMode(p);
                var iterator = new GrooveIterator(groove, padding);
                for (int n = 0; n < song.GetPatternLength(p); n++)
                {
                    if (iterator.IsPadFrame)
                        iterator.Advance();
                    rows.Add((p, n, frame + iterator.FrameIndex));
                    iterator.Advance();
                }
                frame += FamiStudioTempoUtils.ComputeNumberOfFrameForGroove(song.GetPatternLength(p), groove, padding);
            }
            patternFrames[originalLength] = frame;
            patternRows[originalLength] = rows.Count;
            int firstEnd = frame;
            int loopFrame = looping ? patternFrames[loopPattern] : firstEnd;
            int loopRow = looping ? patternRows[loopPattern] : rows.Count;
            int period = firstEnd - loopFrame;
            int periodRows = rows.Count - loopRow;
            int maxDelay = song.Channels.SelectMany(c => c.Patterns).Select(p => p.PlaybackDelay).DefaultIfEmpty(0).Max();
            // The first pass can have different articulation. A common stream
            // boundary after pending first-pass events also supports overtaking
            // delays and different delays in variants without a runtime queue.
            int repeatStart = looping ? firstEnd + maxDelay : -1;
            int outputEnd = looping ? checked(repeatStart + period) : firstEnd;
            int verificationEnd = looping ? checked(outputEnd + period) : outputEnd;

            (int Pattern, int Note, int Frame, bool Variant) RowAt(long ordinal)
            {
                if (ordinal < rows.Count)
                {
                    var row = rows[(int)ordinal];
                    return (row.Pattern, row.Note, row.Frame, false);
                }
                if (!looping)
                    return (-1, 0, firstEnd, false);
                long offset = ordinal - rows.Count;
                var repeatRow = rows[loopRow + (int)(offset % periodRows)];
                long rowFrame = firstEnd + offset / periodRows * period + repeatRow.Frame - loopFrame;
                return (repeatRow.Pattern, repeatRow.Note, checked((int)rowFrame), true);
            }

            var timelines = new SortedDictionary<int, Note>[song.Channels.Length];
            for (int c = 0; c < song.Channels.Length; c++)
            {
                var commands = new List<PlaybackCommand>();
                long owner = 0;
                for (long ordinal = 0; ; ordinal++)
                {
                    var row = RowAt(ordinal);
                    if (row.Pattern < 0 || row.Frame >= verificationEnd)
                        break;
                    var pattern = sourcePatterns[c][row.Pattern]?.GetPlaybackPattern(row.Variant);
                    if (pattern == null || !pattern.Notes.TryGetValue(row.Note, out var note))
                        continue;
                    if (note.HasNoteDelay || note.HasCutDelay)
                        throw new InvalidOperationException($"Song '{song.Name}': combine pattern playback delay with FamiStudio note durations rather than FamiTracker delayed-note/cut effects.");
                    long noteOwner = note.IsMusicalOrStop ? ++owner : 0;
                    int delay = pattern.PlaybackDelay;
                    commands.Add(new PlaybackCommand { Frame = checked(row.Frame + delay), Row = ordinal, Owner = noteOwner, Note = note.Clone() });
                    if (note.IsMusical)
                    {
                        void AddEnd(int distance, int value)
                        {
                            long endRow = ordinal + distance;
                            var end = RowAt(endRow);
                            // The editor enables its owner-aware queue for every
                            // channel when any pattern uses delay or a variant.
                            // Thus even an undelayed voice owns its pending end
                            // across the song boundary in this compilation path.
                            if (end.Pattern >= 0)
                                commands.Add(new PlaybackCommand { Frame = checked(end.Frame + delay), Row = endRow, Owner = noteOwner, End = true, Note = new Note(value) });
                        }
                        AddEnd(note.Duration, Note.NoteStop);
                        if (note.HasRelease)
                            AddEnd(note.Release, Note.NoteRelease);
                    }
                }
                var timeline = new SortedDictionary<int, Note>();
                long activeOwner = 0;
                foreach (var command in commands.OrderBy(e => e.Frame).ThenBy(e => e.Row).ThenBy(e => e.End ? 0 : 1).ThenBy(e => e.End ? -e.Owner : e.Owner))
                {
                    if (command.Frame >= verificationEnd)
                        continue;
                    if (command.End && command.Owner != activeOwner)
                        continue;
                    if (!command.End && command.Owner != 0)
                        activeOwner = command.Owner;
                    timeline.TryGetValue(command.Frame, out var previous);
                    timeline[command.Frame] = MergePlaybackCommands(previous, command.Note);
                }
                if (looping)
                {
                    foreach (int tick in timeline.Keys.Where(t => t >= repeatStart).Select(t => repeatStart + (t - repeatStart) % period).Distinct())
                    {
                        timeline.TryGetValue(tick, out var first);
                        timeline.TryGetValue(tick + period, out var second);
                        if (!SamePlaybackCommand(first, second))
                            throw new InvalidOperationException($"Song '{song.Name}', {song.Channels[c].Name}: delayed events have not reached a repeatable loop at frame {tick}. Shorten an intro note that extends across multiple loops.");
                    }
                }
                timelines[c] = new SortedDictionary<int, Note>(timeline.Where(kv => kv.Key < outputEnd).ToDictionary(kv => kv.Key, kv => kv.Value));
            }

            // Split at the actual tuning changes as well as at the stream loop.
            var boundaries = new SortedSet<int> { 0, outputEnd };
            var tempoBoundaries = new Dictionary<int, (int[] Groove, int Padding)>();
            if (looping)
                boundaries.Add(repeatStart);
            foreach (int start in patternFrames.Where(t => t < outputEnd))
                boundaries.Add(start);
            for (int p = 0; p < originalLength; p++)
                tempoBoundaries[patternFrames[p]] = sourceGrooves[p];
            if (looping)
                for (int pass = 0; firstEnd + pass * period < outputEnd; pass++)
                    for (int p = loopPattern; p < originalLength; p++)
                    {
                        int start = firstEnd + pass * period + patternFrames[p] - loopFrame;
                        if (start < outputEnd)
                        {
                            boundaries.Add(start);
                            tempoBoundaries[start] = sourceGrooves[p];
                        }
                    }
            var initialBoundaries = boundaries.ToArray();
            for (int i = 0; i + 1 < initialBoundaries.Length; i++)
                for (int tick = initialBoundaries[i] + Pattern.MaxLength - 1; tick < initialBoundaries[i + 1]; tick += Pattern.MaxLength - 1)
                    boundaries.Add(tick);
            var starts = boundaries.ToArray();
            compiledPlaybackTempos[song] = tempoBoundaries.ToDictionary(kv => Array.IndexOf(starts, kv.Key), kv => kv.Value);
            if (starts.Length - 1 > Song.MaxLength)
                throw new InvalidOperationException($"Song '{song.Name}': compiled delayed playback exceeds the sound-engine pattern limit.");
            song.SetLength(starts.Length - 1);
            foreach (var channel in song.Channels)
            {
                Array.Clear(channel.PatternInstances, 0, channel.PatternInstances.Length);
                channel.Patterns.Clear();
            }
            for (int p = 0; p < song.Length; p++)
                song.ClearPatternCustomSettings(p);
            song.ChangeFamiStudioTempoGroove(new[] { 1 }, false);
            song.SetDefaultPatternLength(Pattern.MaxLength - 1);
            song.SetBeatLength(1);
            for (int p = 0; p < song.Length; p++)
            {
                song.SetPatternCustomSettings(p, starts[p + 1] - starts[p], 1, new[] { 1 });
                int sourceFrame = starts[p] < firstEnd ? starts[p] : loopFrame + (starts[p] - firstEnd) % period;
                int sourcePattern = Array.BinarySearch(patternFrames, sourceFrame);
                if (sourcePattern < 0)
                    sourcePattern = ~sourcePattern - 1;
                song.GetPatternCustomSettings(p).Tuning = sourceTunings[sourcePattern].Clone();
                for (int c = 0; c < song.Channels.Length; c++)
                    foreach (var entry in timelines[c].Where(kv => kv.Key >= starts[p] && kv.Key < starts[p + 1]))
                        song.Channels[c].GetOrCreatePattern(p).Notes.Add(entry.Key - starts[p], entry.Value);
            }
            song.SetLoopPoint(looping ? Array.IndexOf(starts, repeatStart) : -1);
        }
    }
}
