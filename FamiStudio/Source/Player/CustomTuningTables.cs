using System;
using System.Collections.Generic;

namespace FamiStudio
{
    // Periods retain the engine's existing pitch/slide units. Fraction preserves the
    // two hardware bits that the legacy N163 and EPSM FM tables cannot represent.
    internal sealed class CustomTuningTables
    {
        public readonly ushort[] Periods = new ushort[97];
        public readonly byte[] Fraction = new byte[97];
        static readonly Dictionary<string, CustomTuningTables> cache = new();
        public static readonly byte[] ZeroFraction = new byte[97];

        static double Error(double actual, double ideal) => actual <= 0 ? double.PositiveInfinity : Math.Abs(Math.Log(actual / ideal));
        static int Linear(double ideal, int max)
        {
            int lo = (int)Math.Clamp(Math.Floor(ideal), 1, max), hi = Math.Min(max, lo + 1);
            return Error(lo, ideal) <= Error(hi, ideal) ? lo : hi;
        }
        static int Reciprocal(double divisor, double frequency, int subtract, int min, int max)
        {
            int lo = (int)Math.Clamp(Math.Floor(divisor / frequency - subtract), min, max), hi = Math.Min(max, lo + 1);
            double Distance(int p) => p + subtract <= 0 ? double.PositiveInfinity : Error(divisor / (p + subtract), frequency);
            return Distance(lo) <= Distance(hi) ? lo : hi;
        }
        static int Fm(double ideal, int bits, out int block)
        {
            int best = 1; block = 0; double error = double.PositiveInfinity;
            for (int octave = 0; octave < 8; octave++)
            {
                int value = Linear(ideal / (1 << octave), (1 << bits) - 1) << octave;
                double distance = Error(value, ideal);
                if (distance < error) { error = distance; best = value; block = octave; }
            }
            return best;
        }

        public static CustomTuningTables Get(int type, bool pal, int n163Channels, int referenceHz, int[] cents)
        {
            string key = $"{type}:{pal}:{n163Channels}:{referenceHz}:" + string.Join(",", cents);
            lock (cache)
            {
                if (cache.TryGetValue(key, out var table)) return table;
                table = new CustomTuningTables();
                double cpu = pal ? NesApu.FreqPal : NesApu.FreqNtsc;
                for (int note = 1; note <= 96; note++)
                {
                    double f = referenceHz * Math.Pow(2, (note - 46) / 12.0 + cents[(note - 1) % 12] / 1200.0);
                    int period;
                    if (type >= ChannelType.N163Wave1 && type <= ChannelType.N163Wave8)
                    {
                        int full = Linear(f * n163Channels * 983040 / (cpu / 16), 0x3ffff);
                        period = full >> 2;
                        table.Fraction[note] = (byte)(full & 3);
                    }
                    else if (type >= ChannelType.EPSMFm1 && type <= ChannelType.EPSMFm6)
                    {
                        int full = Fm(144 * f * 1048576 / 8000000, 11, out int block);
                        period = full >> 2;
                        table.Fraction[note] = (byte)((full >> block) & 3);
                    }
                    else if (type >= ChannelType.Vrc7Fm1 && type <= ChannelType.Vrc7Fm6)
                        period = Fm(f * 262144 / 49715, 9, out _);
                    else if (type == ChannelType.FdsWave)
                        period = Linear(f * 65536 / (cpu / 16), 0xfff);
                    else if (type == ChannelType.Vrc6Saw)
                        period = Reciprocal(cpu / 14, f, 1, 0, 0xfff);
                    else if (type >= ChannelType.EPSMSquare1 && type <= ChannelType.EPSMSquare3)
                        period = Reciprocal(NesApu.FreqEPSM / 32.0, f, 0, 1, 0xfff);
                    else
                    {
                        bool s5b = type >= ChannelType.S5BSquare1 && type <= ChannelType.S5BSquare3;
                        bool vrc6 = type == ChannelType.Vrc6Square1 || type == ChannelType.Vrc6Square2;
                        bool pulse = type == ChannelType.Square1 || type == ChannelType.Square2;
                        period = Reciprocal(cpu / 16, f, 1, pulse ? 8 : 0, s5b ? 0xffe : vrc6 ? 0xfff : 0x7ff);
                    }
                    table.Periods[note] = (ushort)period;
                }
                if (cache.Count >= 1024) cache.Clear();
                cache.Add(key, table);
                return table;
            }
        }
    }
}
