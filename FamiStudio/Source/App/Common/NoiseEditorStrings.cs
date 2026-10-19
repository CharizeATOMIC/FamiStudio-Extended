namespace FamiStudio
{
    public static class NoiseEditorStrings
    {
        public static LocalizedString FollowNotesLabel;
        public static LocalizedString FollowNotesTooltip;
        public static LocalizedString FixedPeriodHint;
        public static LocalizedString FollowingPeriodHint;
        public static LocalizedString ArpeggioOverriddenHint;
        public static LocalizedString VolumeOverriddenHint;
        public static LocalizedString PitchIgnoredHint;
        public static LocalizedString ControlHint;
        public static LocalizedString RestartHint;
        public static LocalizedString UseSoftwareVolumeLabel;
        public static LocalizedString UseSoftwareVolumeTooltip;

        static NoiseEditorStrings()
        {
            Localization.LocalizeStatic(typeof(NoiseEditorStrings));
        }

        public static string GetHint(Instrument instrument, int envelopeType, bool noiseChannel)
        {
            if (instrument == null || !instrument.IsRegular)
                return null;
            if (envelopeType == EnvelopeType.NoisePeriod)
                return instrument.NoisePeriodFollowsNote ? FollowingPeriodHint : FixedPeriodHint;
            if (envelopeType == EnvelopeType.NoiseEnvelope)
                return ControlHint;
            if (envelopeType == EnvelopeType.NoiseEnvelopeRestart)
                return RestartHint;
            if (noiseChannel)
            {
                if (envelopeType == EnvelopeType.Volume && instrument.NoiseControlEnvelope.Length > 0)
                    return VolumeOverriddenHint;
                if (envelopeType == EnvelopeType.Arpeggio && instrument.NoisePeriodEnvelope.Length > 0 && !instrument.NoisePeriodFollowsNote)
                    return ArpeggioOverriddenHint;
                if (envelopeType == EnvelopeType.Pitch)
                    return PitchIgnoredHint;
            }
            return null;
        }
    }
}
