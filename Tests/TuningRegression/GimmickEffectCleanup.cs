using System;
using System.Collections.Generic;
using System.Linq;
using FamiStudio;

// Used only by the local source-event converter. Shared patterns are examined
// at every occurrence, including their editable variants and loop-entry state.
static class GimmickEffectCleanup
{
    public static Dictionary<string,int> Run(Project project)
    {
        int[] effects = { Note.EffectVolume, Note.EffectFinePitch, Note.EffectDutyCycle };
        int[] defaults = { 15, 0, 0 };
        string[] names = { "volume", "fine_pitch", "duty_cycle" };
        var observed = new HashSet<(Note Note, int Effect)>();
        var needed = new HashSet<(Note Note, int Effect)>();
        foreach (var song in project.Songs)
        foreach (var channel in song.Channels)
        {
            var current = (int[])defaults.Clone();
            // After one complete repeat, these simple assignment-only states
            // have reached their steady loop state. No slide/reset heuristics.
            for (int pass = 0; pass < (song.LoopPoint >= 0 ? 3 : 1); pass++)
            for (int p = pass == 0 ? 0 : song.LoopPoint; p < song.Length; p++)
            {
                var pattern = channel.PatternInstances[p]?.GetPlaybackPattern(pass > 0);
                if (pattern == null) continue;
                if (pattern.PlaybackDelay != 0) throw new InvalidOperationException("Effect cleanup requires visible, undelayed note positions.");
                foreach (var pair in pattern.Notes.OrderBy(n => n.Key))
                {
                    if (pair.Key >= song.GetPatternLength(p)) continue;
                    var note = pair.Value;
                    if (note.HasVolumeSlide) throw new InvalidOperationException("Effect cleanup does not support volume slides.");
                    for (int e = 0; e < effects.Length; e++)
                    {
                        if (!note.HasValidEffectValue(effects[e])) continue;
                        var key = (note, effects[e]);
                        int value = note.GetEffectValue(effects[e]);
                        observed.Add(key);
                        if (current[e] != value) needed.Add(key);
                        current[e] = value;
                    }
                }
            }
        }
        var removed = names.ToDictionary(n => n, n => 0);
        foreach (var key in observed.Except(needed))
        {
            key.Note.ClearEffectValue(key.Effect);
            removed[names[Array.IndexOf(effects, key.Effect)]]++;
        }
        foreach (var song in project.Songs)
        foreach (var channel in song.Channels)
        foreach (var pattern in channel.Patterns)
            pattern.DeleteEmptyNotes();
        return removed;
    }

    public static void Regression()
    {
        var project = new Project();
        var instrument = project.CreateInstrument(ExpansionType.None, "Synthetic effect cleanup");
        var song = project.CreateSong("Intro and repeat");
        song.ChangeFamiStudioTempoGroove(new[]{1}, false);
        song.SetDefaultPatternLength(8); song.SetLength(3); song.SetLoopPoint(1);
        var channel = song.Channels[0];
        var intro = channel.GetOrCreatePattern(0);
        var loop = channel.GetOrCreatePattern(1);
        var end = channel.GetOrCreatePattern(2);
        var initial = new Note(49) { Instrument = instrument, Duration = 2, Volume = 15, FinePitch = 0, DutyCycle = 0 };
        var restore = new Note(49) { Instrument = instrument, Duration = 2, Volume = 15, FinePitch = 0, DutyCycle = 0 };
        var change = new Note(49) { Instrument = instrument, Duration = 2, Volume = 7, FinePitch = 3, DutyCycle = 2 };
        var duplicate = new Note(49) { Instrument = instrument, Duration = 2, Volume = 7, FinePitch = 3, DutyCycle = 2 };
        intro.Notes.Add(0, initial); loop.Notes.Add(0, restore);
        end.Notes.Add(0, change); end.Notes.Add(4, duplicate);
        // Repeat uses a different tail; both variants must be inspected.
        var variant = channel.CreatePattern();
        var variantChange = new Note(49) { Instrument = instrument, Duration = 2, Volume = 5, FinePitch = -2, DutyCycle = 1 };
        variant.Notes.Add(0, variantChange); end.LoopVariant = variant;
        var removed = Run(project);
        if (removed.Values.Sum() != 6 || initial.EffectMask != 0 || duplicate.EffectMask != 0 ||
            !restore.HasVolume || !restore.HasFinePitch || !restore.HasDutyCycle ||
            !change.HasVolume || !variantChange.HasVolume)
            throw new Exception("Persistent effect cleanup lost a required loop restore or retained a no-op.");
        if (Run(project).Values.Sum() != 0) throw new Exception("Persistent effect cleanup is not idempotent.");
        Console.WriteLine("PASS converter effect cleanup: initial defaults, repeated assignments, first/repeat restores and idempotence");
    }
}
