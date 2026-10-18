using System;
using System.Linq;

namespace FamiStudio
{
    // A setting is an entire tuning snapshot. Disabled snapshots inherit their parent.
    public sealed class TuningSettings
    {
        public bool Override;
        public int ReferenceHz = 440;
        public int[] Cents = new int[12];
        public static readonly string[] PitchNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        public TuningSettings Clone() => new TuningSettings { Override = Override, ReferenceHz = ReferenceHz, Cents = (int[])Cents.Clone() };

        public void Validate()
        {
            if (ReferenceHz < 300 || ReferenceHz > 580 || Cents == null || Cents.Length != 12 || Cents.Any(c => c < -1200 || c > 1200))
                throw new ArgumentException("Tuning requires A4 = 300..580 Hz and 12 offsets in -1200..1200 cents.");
        }

        public void Serialize(ProjectBuffer buffer)
        {
            buffer.Serialize(ref Override);
            buffer.Serialize(ref ReferenceHz);
            buffer.Serialize(ref Cents);
            if (buffer.IsReading) Validate();
        }

        // Note 1 is C1, note 46 is A4. An A offset also moves A4 itself.
        public double Frequency(int note) => ReferenceHz * Math.Pow(2.0, (note - 46) / 12.0 + Cents[(note - 1) % 12] / 1200.0);
    }
}
