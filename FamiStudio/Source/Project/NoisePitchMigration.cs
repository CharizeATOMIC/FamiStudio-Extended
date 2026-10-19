using System;
using System.Collections.Generic;
using System.Linq;

namespace FamiStudio
{
    public enum NoisePitchMigrationFailure
    {
        None,
        NoLegacyPeriod,
        SharedWithOtherChannels,
        NonzeroInstrumentArpeggio,
        IndependentArpeggio,
        FixedPeriodNeedsC4,
        NoAttackCompatibility,
        UnsupportedMacro
    }

    // Explicit, conservative migration of the legacy noise-period program to
    // the existing instrument arpeggio. Loading a project never invokes this.
    public static class NoisePitchMigration
    {
        static IEnumerable<Pattern> GetPatterns(Channel channel)
        {
            var seen = new HashSet<Pattern>();
            var pending = new Stack<Pattern>(channel.Patterns);
            foreach (var pattern in channel.PatternInstances)
                if (pattern != null) pending.Push(pattern);
            while (pending.Count > 0)
            {
                var pattern = pending.Pop();
                if (pattern == null || !seen.Add(pattern)) continue;
                yield return pattern;
                if (pattern.LoopVariant != null) pending.Push(pattern.LoopVariant);
            }
        }

        static Envelope ConvertedArpeggio(Envelope period)
        {
            var arp = new Envelope(EnvelopeType.Arpeggio);
            arp.Length = period.Length;
            arp.Loop = period.Loop;
            arp.TickRateMultiplier = period.TickRateMultiplier;
            for (int n = 0; n < period.Length; n++)
                arp.Values[n] = (sbyte)(14 - period.Values[n]);
            return arp;
        }

        static bool EnvelopesMatchAfter(Instrument first, Instrument second, Instrument migrating, Envelope arp, Envelope cleared)
        {
            for (int type = 0; type < EnvelopeType.Count; type++)
            {
                Envelope Effective(Instrument instrument) => instrument == migrating
                    ? type == EnvelopeType.Arpeggio ? arp : type == EnvelopeType.NoisePeriod ? cleared : instrument.Envelopes[type]
                    : instrument.Envelopes[type];
                var a = Effective(first); var b = Effective(second);
                if (a == null || b == null)
                { if (a != b) return false; }
                else if (!Envelope.AreIdentical(type, a, b)) return false;
            }
            return true;
        }

        public static NoisePitchMigrationFailure GetFailure(Project project, Instrument instrument)
        {
            var period = instrument?.NoisePeriodEnvelope;
            if (project == null || period == null || period.Length == 0)
                return NoisePitchMigrationFailure.NoLegacyPeriod;
            if (period.Relative || period.Release >= 0 || period.Values.Take(period.Length).Any(v => v < 0 || v > 15))
                return NoisePitchMigrationFailure.UnsupportedMacro;
            if (!instrument.ArpeggioEnvelope.AllValuesEqual(0))
                return NoisePitchMigrationFailure.NonzeroInstrumentArpeggio;

            var tiedInstruments = new HashSet<Instrument>();
            foreach (var song in project.Songs)
            foreach (var channel in song.Channels)
            {
                var notes = GetPatterns(channel).SelectMany(p => p.Notes.Values).ToArray();
                bool usesInstrument = notes.Any(n => n.Instrument == instrument);
                // Effect-only events can carry an arpeggio or inherit the active
                // instrument. Their effect on subsequent tied notes must not be
                // guessed across pattern delay/loop boundaries.
                if (usesInstrument && channel.Type == ChannelType.Noise && notes.Any(n => n.Arpeggio != null))
                    return NoisePitchMigrationFailure.IndependentArpeggio;
                foreach (var note in notes)
                {
                    if (channel.Type == ChannelType.Noise && note.IsMusical && !note.HasAttack)
                    {
                        if (note.Instrument != null) tiedInstruments.Add(note.Instrument);
                        else if (usesInstrument) tiedInstruments.Add(instrument);
                    }
                    bool mayInheritInstrument = usesInstrument && note.Instrument == null && channel.Type == ChannelType.Noise;
                    if (note.Instrument != instrument && !mayInheritInstrument) continue;
                    // Include unused patterns and notes outside current pattern length:
                    // a later edit must not reveal a previously altered melodic part.
                    if (channel.Type != ChannelType.Noise)
                        return NoisePitchMigrationFailure.SharedWithOtherChannels;
                    if (!note.IsMusical) continue;
                    if (!instrument.NoisePeriodFollowsNote && (note.Value != 49 || note.IsSlideNote))
                        return NoisePitchMigrationFailure.FixedPeriodNeedsC4;
                }
            }

            var converted = ConvertedArpeggio(period);
            var cleared = new Envelope(EnvelopeType.NoisePeriod) { TickRateMultiplier = period.TickRateMultiplier };
            foreach (var other in project.Instruments)
            {
                if (other == instrument || other.Expansion != instrument.Expansion) continue;
                if (!tiedInstruments.Contains(instrument) && !tiedInstruments.Contains(other)) continue;
                bool before = Instrument.AllEnvelopesAreIdentical(instrument, other);
                bool after = EnvelopesMatchAfter(instrument, other, instrument, converted, cleared);
                // A change can force or suppress an attack on a tied instrument
                // switch. Do not guess its predecessor across loops or delays.
                if (before != after)
                    return NoisePitchMigrationFailure.NoAttackCompatibility;
            }
            return NoisePitchMigrationFailure.None;
        }

        public static bool TryConvert(Project project, Instrument instrument, out NoisePitchMigrationFailure failure)
        {
            failure = GetFailure(project, instrument);
            if (failure != NoisePitchMigrationFailure.None) return false;
            var converted = ConvertedArpeggio(instrument.NoisePeriodEnvelope);
            var arp = instrument.ArpeggioEnvelope;
            arp.Length = converted.Length;
            arp.Loop = converted.Loop;
            arp.Release = -1;
            arp.Relative = false;
            arp.TickRateMultiplier = converted.TickRateMultiplier;
            Array.Copy(converted.Values, arp.Values, arp.Values.Length);
            instrument.NoisePeriodEnvelope.ResetToDefault(EnvelopeType.NoisePeriod);
            instrument.NoisePeriodFollowsNote = false;
            instrument.NotifyEnvelopeChanged(EnvelopeType.Arpeggio, true);
            instrument.NotifyEnvelopeChanged(EnvelopeType.NoisePeriod, true);
            return true;
        }
    }
}
