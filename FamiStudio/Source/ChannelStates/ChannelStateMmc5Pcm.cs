namespace FamiStudio
{
    public class ChannelStateMmc5Pcm : ChannelState
    {
        public ChannelStateMmc5Pcm(IPlayerInterface player, int apuIdx, int channelType, int tuning, bool pal)
            : base(player, apuIdx, channelType, tuning, pal) { }

        public override void UpdateAPU()
        {
            UpdatePcmSample(NesApu.PcmDestinationMmc5);
            base.UpdateAPU();
        }
    }
}
