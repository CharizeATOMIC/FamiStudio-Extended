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
        public static LocalizedString HardwareSettingsLabel;
        public static LocalizedString HardwareSettingsActiveLabel;
        public static LocalizedString HardwareSettingsTooltip;
        public static LocalizedString NoiseSettingsLabel;
        public static LocalizedString NoiseSettingsTooltip;
        public static LocalizedString MacroStepsLabel;
        public static LocalizedString UnusedMacroLabel;
        public static LocalizedString ConvertLegacyPitchLabel;
        public static LocalizedString LegacyPitchHint;
        public static LocalizedString MigrationSharedHint;
        public static LocalizedString MigrationArpeggioHint;
        public static LocalizedString MigrationIndependentHint;
        public static LocalizedString MigrationFixedHint;
        public static LocalizedString MigrationAttackHint;
        public static LocalizedString MigrationSuccessHint;
        public static LocalizedString MigrationUnsupportedHint;

        static NoiseEditorStrings()
        {
            Localization.LocalizeStatic(typeof(NoiseEditorStrings));
        }

        public static string GetHint(Instrument instrument, int envelopeType, bool noiseChannel)
        {
            if (instrument == null || !instrument.IsRegular)
                return null;
            if (envelopeType == EnvelopeType.NoisePeriod)
                return LegacyPitchHint;
            if (envelopeType == EnvelopeType.NoiseEnvelope)
                return ControlHint;
            if (envelopeType == EnvelopeType.NoiseEnvelopeRestart)
                return RestartHint;
            if (envelopeType == EnvelopeType.Arpeggio && instrument.NoisePeriodEnvelope.Length > 0)
                return LegacyPitchHint;
            if (noiseChannel)
            {
                if (envelopeType == EnvelopeType.Volume && instrument.NoiseControlEnvelope.Length > 0)
                    return VolumeOverriddenHint;
                if (envelopeType == EnvelopeType.Pitch)
                    return PitchIgnoredHint;
            }
            return null;
        }
    }
}
