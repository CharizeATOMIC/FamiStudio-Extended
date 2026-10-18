using System.Diagnostics;

namespace FamiStudio
{
    public class ChannelStateNoise : ChannelState
    {
        private bool previousHardwareControl;

        public ChannelStateNoise(IPlayerInterface player, int apuIdx, int channelIdx, int tuning, bool pal) : base(player, apuIdx, channelIdx, tuning, pal)
        {
        }

        public override void UpdateAPU()
        {
            if (note.IsStop)
            {
                WriteRegister(NesApu.APU_NOISE_VOL, 0xf0);
            }
            else if (note.IsMusical)
            {
                var slide  = slideShift < 0 ? (slidePitch >> -slideShift) : (slidePitch << slideShift); // Remove the fraction part.
                var volume = GetVolume();
                var duty   = GetDuty();
                var period = (int)(((note.Value + slide + envelopeValues[EnvelopeType.Arpeggio]) & 0x0f) ^ 0x0f) | ((duty << 7) & 0x80);

                bool periodEnabled = envelopes[EnvelopeType.NoisePeriod]?.Length > 0;
                bool controlEnabled = envelopes[EnvelopeType.NoiseEnvelope]?.Length > 0;
                bool restartEnabled = envelopes[EnvelopeType.NoiseEnvelopeRestart]?.Length > 0;
                if (periodEnabled)
                    period = (period & 0x80) | (envelopeValues[EnvelopeType.NoisePeriod] & 15);

                WriteRegister(NesApu.APU_NOISE_LO, period);
                // Hardware control retains the decay/loop/length semantics in
                // all six bits. Software volume would change the decay divider.
                WriteRegister(NesApu.APU_NOISE_VOL, controlEnabled ? envelopeValues[EnvelopeType.NoiseEnvelope] & 0x3f : 0x30 | volume);

                if (restartEnabled)
                {
                    int restart = envelopeValues[EnvelopeType.NoiseEnvelopeRestart];
                    if (restart > 0)
                        WriteRegister(NesApu.APU_NOISE_HI, (restart - 1) << 3);
                }
                else if ((controlEnabled && noteTriggered) || (previousHardwareControl && !controlEnabled))
                {
                    // Index 1 is the longest length, allowing slow one-shot
                    // decay to complete. Legacy playback never enters this path.
                    WriteRegister(NesApu.APU_NOISE_HI, 0x08);
                }
                previousHardwareControl = controlEnabled;
            }

            base.UpdateAPU();
        }
    }
}
