using System;
using System.Diagnostics;

namespace FamiStudio
{
    public class ChannelStateSquare : ChannelState
    {
        int regOffset = 0;
        int prevPeriodHi = 0x80;
        int prevPeriod = -1;
        int prevSweep = 0x08;

        public ChannelStateSquare(IPlayerInterface player, int apuIdx, int channelType, int tuning, bool pal) : base(player, apuIdx, channelType, tuning, pal)
        {
            regOffset = channelType * 4;
        }

        public override void UpdateAPU()
        {
            var duty = GetDuty();

            if (note.IsStop)
            {
                WriteRegister(NesApu.APU_PL1_VOL + regOffset, (duty << 6) | (0x30) | 0);
            }
            else if (note.IsMusical)
            {
                // Instrument articulation belongs to the attack. Invalidating the
                // cached high byte makes the normal write path reset phase once,
                // including when the timer's high byte changes on this frame.
                if (noteTriggered && note.Instrument?.ResetPhaseOnAttack == true)
                    prevPeriodHi = 0x80;

                var period = GetPeriod();
                var volume = GetVolume();

                var periodHi = (period >> 8) & 0x07;
                var periodLo = (period >> 0) & 0xff;
                int deltaHi  = periodHi - prevPeriodHi;

                var sweep = note.Instrument?.PulseSweepRegister ?? 0x08;
                var sweepEnabled = (sweep & 0x80) != 0 && (sweep & 7) != 0;
                var sweepChanged = sweep != prevSweep;

                // The hardware changes its timer independently of the last
                // software pitch. Rewriting the low byte on every frame would
                // erase those changes and turn a real sweep into a steady note.
                // A changed software pitch remains an intentional timer update.
                if (sweepEnabled)
                {
                    // Writing the sweep reload flag on every tick would prevent
                    // divider periods greater than one frame from progressing.
                    if (noteTriggered || sweepChanged)
                        WriteRegister(NesApu.APU_PL1_SWEEP + regOffset, sweep);
                    if (noteTriggered || sweepChanged || period != prevPeriod)
                    {
                        WriteRegister(NesApu.APU_PL1_LO + regOffset, periodLo);
                        // A sweep may have changed both hardware bytes even
                        // when only the software low byte changed this tick.
                        WriteRegister(NesApu.APU_PL1_HI + regOffset, periodHi);
                    }
                    prevPeriodHi = periodHi;
                    prevPeriod = period;
                    prevSweep = sweep;
                    WriteRegister(NesApu.APU_PL1_VOL + regOffset, (duty << 6) | 0x30 | volume);
                    base.UpdateAPU();
                    return;
                }

                if (sweepChanged)
                {
                    WriteRegister(NesApu.APU_PL1_SWEEP + regOffset, sweep);
                    // The former sweep may have changed the hardware high byte.
                    // Restore the full software timer even if its cached value
                    // happens to be unchanged when the sweep is disabled.
                    if ((prevSweep & 0x80) != 0 && (prevSweep & 7) != 0)
                        deltaHi = 0x80;
                }
                prevSweep = sweep;
                prevPeriod = period;

                // Write low first, this matches the NSF driver.
                WriteRegister(NesApu.APU_PL1_LO + regOffset, periodLo);

                if (deltaHi != 0) // Avoid resetting the sequence.
                {
                    // TODO : We should not reference settings from here.
                    var otherPulse = player.GetChannelByType(channelType == ChannelType.Square1 ? ChannelType.Square2 : ChannelType.Square1, apuIdx) as ChannelStateSquare;
                    bool otherPulseSweeps = otherPulse != null && otherPulse.note.IsMusical && otherPulse.note.Instrument?.PulseSweepEnabled == true;
                    if (sweep == 0x08 && !otherPulseSweeps && Settings.SquareSmoothVibrato && Math.Abs(deltaHi) == 1 && !IsSeeking)
                    {
                        // Blaarg's smooth vibrato technique using the sweep to avoid resetting the phase. Cool stuff.
                        // http://forums.nesdev.com/viewtopic.php?t=231

                        WriteRegister(NesApu.APU_FRAME_CNT, 0x40); // reset frame counter in case it was about to clock
                        WriteRegister(NesApu.APU_PL1_LO + regOffset, deltaHi < 0 ? 0x00 : 0xff); // be sure low 8 bits of timer period are $FF ($00 when negative)
                        WriteRegister(NesApu.APU_PL1_SWEEP + regOffset, deltaHi < 0 ? 0x8f : 0x87); // sweep enabled, shift = 7, set negative flag.
                        WriteRegister(NesApu.APU_FRAME_CNT, 0xc0); // clock sweep immediately
                        WriteRegister(NesApu.APU_PL1_SWEEP + regOffset, 0x08); // disable sweep
                        WriteRegister(NesApu.APU_PL1_LO + regOffset, periodLo); // Restore correct low.
                    }
                    else
                    {
                        WriteRegister(NesApu.APU_PL1_HI + regOffset, periodHi);
                    }

                    prevPeriodHi = periodHi;
                }

                WriteRegister(NesApu.APU_PL1_VOL + regOffset, (duty << 6) | (0x30) | volume);
            }
            
            base.UpdateAPU();
        }

        protected override void ResetPhase()
        {
            SkipCycles(4); // ldy
            WriteRegister(NesApu.APU_PL1_HI + regOffset, prevPeriodHi);
        }
    }
}
