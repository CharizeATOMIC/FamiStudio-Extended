using System;
using System.Linq;

namespace FamiStudio
{
    public enum ChipTuningUnit { Timer, Period, FrequencyWord, FmFNumber }

    // Absolute integer hardware values with a persisted reference clock/tuning.
    // IDs 1..96 retain the editor's C0..B7 labels and channel octave conventions.
    // A stored value of -1 means use the unchanged FamiStudio default at this note.
    public sealed class ChipTuningSettings
    {
        public const int NoteCount = 96;
        public const int AdjustmentLimit = 50;
        readonly int[][] values = Enumerable.Range(0, ExpansionType.Count).Select(_ => Enumerable.Repeat(-1, NoteCount).ToArray()).ToArray();
        readonly int[] overrideCounts = new int[ExpansionType.Count];
        readonly string[] cacheKeys = new string[ExpansionType.Count];
        int referenceHz = 440, n163Channels = 1;
        bool pal;

        public int BasisReferenceHz => referenceHz;
        public bool BasisPal => pal;
        public int BasisN163Channels => n163Channels;
        public bool HasAnyOffsets => overrideCounts.Any(n => n != 0);
        public bool HasOffsets(int chip) { CheckChip(chip); return overrideCounts[chip] != 0; }
        public bool HasOverride(int chip, int note) { CheckChip(chip); CheckNote(note); return values[chip][note - 1] >= 0; }

        static void CheckChip(int chip) { if (chip < 0 || chip >= ExpansionType.Count) throw new ArgumentOutOfRangeException(nameof(chip)); }
        static void CheckNote(int note) { if (note < 1 || note > NoteCount) throw new ArgumentOutOfRangeException(nameof(note)); }
        static void CheckBasis(int hz, int count)
        {
            if (hz < 300 || hz > 580 || count < 1 || count > 8) throw new ArgumentOutOfRangeException("Invalid chip curve basis.");
        }

        public void ConfigureBasis(int hz, bool isPal, int count)
        {
            CheckBasis(hz, count);
            if (hz == referenceHz && isPal == pal && count == n163Channels) return;
            if (HasAnyOffsets) throw new InvalidOperationException("Reset curve overrides before changing their reference basis.");
            referenceHz = hz; pal = isPal; n163Channels = count;
            Array.Clear(cacheKeys);
        }

        public ChipTuningUnit GetUnitKind(int chip)
        {
            CheckChip(chip);
            return chip == ExpansionType.Vrc7 ? ChipTuningUnit.FmFNumber :
                chip == ExpansionType.Fds || chip == ExpansionType.N163 ? ChipTuningUnit.FrequencyWord :
                chip == ExpansionType.S5B || chip == ExpansionType.EPSM ? ChipTuningUnit.Period : ChipTuningUnit.Timer;
        }

        static int ChannelForChip(int chip) => chip switch
        {
            ExpansionType.Vrc6 => ChannelType.Vrc6Square1,
            ExpansionType.Vrc7 => ChannelType.Vrc7Fm1,
            ExpansionType.Fds => ChannelType.FdsWave,
            ExpansionType.Mmc5 => ChannelType.Mmc5Square1,
            ExpansionType.N163 => ChannelType.N163Wave1,
            ExpansionType.S5B => ChannelType.S5BSquare1,
            ExpansionType.EPSM => ChannelType.EPSMSquare1,
            _ => ChannelType.Square1
        };

        int RawDefault(int chip, int note) => NesApu.GetNoteTableForChannelType(ChannelForChip(chip), pal, n163Channels, referenceHz)[note];
        public int GetFmBlock(int chip, int note)
        {
            CheckChip(chip); CheckNote(note);
            if (chip != ExpansionType.Vrc7) return 0;
            int value = RawDefault(chip, note), block = 0;
            while (value >= 512 && block < 7) { value >>= 1; block++; }
            return block;
        }

        static int HardwareMinimum(int chip) => chip == ExpansionType.None ? 8 :
            chip == ExpansionType.S5B || chip == ExpansionType.EPSM || chip == ExpansionType.Vrc7 || chip == ExpansionType.Fds || chip == ExpansionType.N163 ? 1 : 0;
        static int HardwareMaximum(int chip) => chip == ExpansionType.None || chip == ExpansionType.Mmc5 ? 0x7ff :
            chip == ExpansionType.Vrc7 ? 0x1ff : chip == ExpansionType.N163 ? 0x3ffff : 0xfff;

        public int GetDefaultValue(int chip, int note)
        {
            CheckChip(chip); CheckNote(note);
            int raw = RawDefault(chip, note);
            if (chip == ExpansionType.S5B) raw++;
            else if (chip == ExpansionType.N163) raw <<= 2;
            else if (chip == ExpansionType.Vrc7) raw >>= GetFmBlock(chip, note);
            return Math.Clamp(raw, HardwareMinimum(chip), HardwareMaximum(chip));
        }
        public int GetValue(int chip, int note) => HasOverride(chip, note) ? values[chip][note - 1] : GetDefaultValue(chip, note);
        public int GetHardwareMinimumValue(int chip, int note)
        {
            CheckChip(chip); CheckNote(note);
            return chip == ExpansionType.Vrc7 && GetFmBlock(chip, note) > 0 ? 256 : HardwareMinimum(chip);
        }
        public int GetHardwareMaximumValue(int chip, int note) { CheckChip(chip); CheckNote(note); return HardwareMaximum(chip); }
        // The graph offers a comfortable +/-50 window; manual entry uses the full
        // representable hardware range and may deliberately exceed this drag band.
        public int GetMinimumValue(int chip, int note) => Math.Max(GetHardwareMinimumValue(chip, note), GetDefaultValue(chip, note) - AdjustmentLimit);
        public int GetMaximumValue(int chip, int note) => Math.Min(GetHardwareMaximumValue(chip, note), GetDefaultValue(chip, note) + AdjustmentLimit);

        public void SetValue(int chip, int note, int value)
        {
            CheckChip(chip); CheckNote(note);
            if (value < GetHardwareMinimumValue(chip, note) || value > GetHardwareMaximumValue(chip, note))
                throw new ArgumentOutOfRangeException(nameof(value), "Curve values must be within the chip's integer hardware range.");
            int stored = value == GetDefaultValue(chip, note) ? -1 : value;
            if (values[chip][note - 1] == stored) return;
            if (values[chip][note - 1] >= 0) overrideCounts[chip]--;
            values[chip][note - 1] = stored;
            if (stored >= 0) overrideCounts[chip]++;
            cacheKeys[chip] = null;
        }
        public void ClearPoint(int chip, int note)
        {
            CheckChip(chip); CheckNote(note);
            if (values[chip][note - 1] < 0) return;
            values[chip][note - 1] = -1; overrideCounts[chip]--; cacheKeys[chip] = null;
        }
        public void ResetChip(int chip)
        {
            CheckChip(chip); Array.Fill(values[chip], -1);
            overrideCounts[chip] = 0; cacheKeys[chip] = null;
        }

        // Other pitched families on the same chip (VRC6 saw, EPSM FM) use the
        // nearest representable value for the pitch derived from this canonical value.
        public double GetFrequency(int chip, int note)
        {
            int value = GetValue(chip, note);
            double cpu = pal ? NesApu.FreqPal : NesApu.FreqNtsc;
            return chip switch
            {
                ExpansionType.S5B => cpu / 16.0 / value,
                ExpansionType.EPSM => NesApu.FreqEPSM / 32.0 / value,
                ExpansionType.Vrc7 => (value << GetFmBlock(chip, note)) * 49715.0 / 262144,
                ExpansionType.Fds => value * (cpu / 16.0) / 65536,
                ExpansionType.N163 => value * (cpu / 16.0) / (n163Channels * 983040.0),
                _ => cpu / 16.0 / (value + 1)
            };
        }

        public ChipTuningSettings Clone()
        {
            var clone = new ChipTuningSettings(); clone.ConfigureBasis(referenceHz, pal, n163Channels);
            for (int chip = 0; chip < ExpansionType.Count; chip++)
            {
                Array.Copy(values[chip], clone.values[chip], NoteCount);
                clone.overrideCounts[chip] = overrideCounts[chip]; clone.cacheKeys[chip] = cacheKeys[chip];
            }
            return clone;
        }
        bool SameBasis(ChipTuningSettings other) => other != null && referenceHz == other.referenceHz && pal == other.pal && n163Channels == other.n163Channels;
        public bool ContentEquals(ChipTuningSettings other) => SameBasis(other) && Enumerable.Range(0, ExpansionType.Count).All(chip => values[chip].SequenceEqual(other.values[chip]));
        public bool ChipEquals(ChipTuningSettings other, int chip)
        {
            CheckChip(chip);
            return other != null && (!HasOffsets(chip) && !other.HasOffsets(chip) || SameBasis(other) && values[chip].SequenceEqual(other.values[chip]));
        }
        internal string GetCacheKey(int chip) => cacheKeys[chip] ??= $"{referenceHz}:{pal}:{n163Channels}:" + string.Join(",", values[chip]);
        public void Validate()
        {
            CheckBasis(referenceHz, n163Channels);
            for (int chip = 0; chip < ExpansionType.Count; chip++)
                for (int note = 1; note <= NoteCount; note++)
                    if (HasOverride(chip, note) && (GetValue(chip, note) < GetHardwareMinimumValue(chip, note) || GetValue(chip, note) > GetHardwareMaximumValue(chip, note)))
                        throw new ArgumentException("Invalid chip tuning value.");
        }
        public void Serialize(ProjectBuffer buffer)
        {
            buffer.Serialize(ref referenceHz); buffer.Serialize(ref pal); buffer.Serialize(ref n163Channels);
            if (buffer.IsReading) { CheckBasis(referenceHz, n163Channels); for (int chip = 0; chip < ExpansionType.Count; chip++) ResetChip(chip); }
            for (int chip = 0; chip < ExpansionType.Count; chip++)
                for (int note = 1; note <= NoteCount; note++)
                {
                    int value = values[chip][note - 1]; buffer.Serialize(ref value);
                    if (buffer.IsReading)
                    {
                        if (value < -1) throw new ArgumentException("Invalid chip tuning override.");
                        if (value >= 0) SetValue(chip, note, value);
                    }
                }
        }
    }
}
