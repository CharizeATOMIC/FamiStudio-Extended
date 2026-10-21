namespace FamiStudio
{
    static class EnvelopeClockConversion
    {
        // Validate the complete conversion before mutating an editable resource.
        public static bool TryConvert(Envelope envelope, int targetRate, bool waveformRepeat, bool pulse = false)
        {
            if (targetRate != 1 && targetRate != 2 && targetRate != 4) return false;
            int sourceRate = envelope.TickRateMultiplier;
            if (sourceRate == targetRate) return true;
            // An absolute, permanently held value has no timed transition.
            // In particular, new high-rate instruments use a one-value volume
            // envelope which remains exact at every supported output clock.
            if (targetRate < sourceRate && envelope.Length == 1 && envelope.Release < 0 && !envelope.Relative && !waveformRepeat && !pulse)
            {
                envelope.TickRateMultiplier = targetRate;
                return true;
            }
            int length = envelope.Length, loop = envelope.Loop, release = envelope.Release;
            var converted = new sbyte[envelope.Values.Length];
            if (waveformRepeat)
            {
                for (int i = 0; i < length; i++)
                {
                    int numerator = envelope.Values[i] * targetRate;
                    if (numerator % sourceRate != 0) return false;
                    int value = numerator / sourceRate;
                    if (value < 1 || value > 15) return false;
                    converted[i] = (sbyte)value;
                }
            }
            else if (targetRate > sourceRate)
            {
                int factor = targetRate / sourceRate;
                if (length * factor > converted.Length) return false;
                for (int i = 0; i < length; i++)
                    for (int j = 0; j < factor; j++)
                        converted[i * factor + j] = (envelope.Relative || pulse) && j != 0 ? (sbyte)0 : envelope.Values[i];
                length *= factor;
                if (loop >= 0) loop *= factor;
                if (release >= 0) release *= factor;
            }
            else
            {
                int factor = sourceRate / targetRate;
                if (length % factor != 0 || loop >= 0 && loop % factor != 0 || release >= 0 && release % factor != 0) return false;
                for (int i = 0; i < length; i += factor)
                {
                    var value = envelope.Values[i];
                    for (int j = 1; j < factor; j++)
                        if (envelope.Values[i + j] != (envelope.Relative || pulse ? 0 : value)) return false;
                    converted[i / factor] = value;
                }
                length /= factor;
                if (loop >= 0) loop /= factor;
                if (release >= 0) release /= factor;
            }
            envelope.Release = -1; envelope.Loop = -1; envelope.Length = length;
            System.Array.Copy(converted, envelope.Values, converted.Length);
            envelope.Loop = loop; envelope.Release = release; envelope.TickRateMultiplier = targetRate;
            return true;
        }
    }

    sealed class TuningProperties
    {
        readonly PropertyPage props;
        readonly int enabled, reference, offsets;

        public TuningProperties(PropertyPage page, TuningSettings settings, int inheritedHz, int[] inheritedCents = null)
        {
            props = page;
            enabled = props.AddCheckBox(TuningStrings.OverrideLabel, settings.Override);
            reference = props.AddNumericUpDown(TuningStrings.ReferenceLabel, settings.Override ? settings.ReferenceHz : inheritedHz, 300, 580, 1);
            var data = new object[12, 2];
            for (int i = 0; i < 12; i++) { data[i, 0] = TuningSettings.PitchNames[i]; data[i, 1] = !settings.Override && inheritedCents != null ? inheritedCents[i] : settings.Cents[i]; }
            offsets = props.AddGrid(TuningStrings.TemperamentLabel, new[] { new ColumnDesc(TuningStrings.NoteLabel, .4f), new ColumnDesc(TuningStrings.CentsLabel, .6f, -1200, 1200) }, data, 6,
                TuningStrings.TemperamentTooltip);
            props.PropertyChanged += (p, idx, row, col, value) => { if (idx == enabled) UpdateEnabled(); };
            UpdateEnabled();
        }

        void UpdateEnabled()
        {
            var on = props.GetPropertyValue<bool>(enabled);
            props.SetPropertyEnabled(reference, on);
            props.SetPropertyEnabled(offsets, on);
        }

        public TuningSettings GetSettings()
        {
            var setting = new TuningSettings { Override = props.GetPropertyValue<bool>(enabled), ReferenceHz = props.GetPropertyValue<int>(reference) };
            for (int i = 0; i < 12; i++) setting.Cents[i] = props.GetPropertyValue<int>(offsets, i, 1);
            setting.Validate();
            return setting;
        }
    }
}

namespace FamiStudio
{
    public static class TuningStrings
    {
        public static LocalizedString OverrideLabel, ReferenceLabel, TemperamentLabel, NoteLabel, CentsLabel, TemperamentTooltip;
        public static LocalizedString MixerLabel, ChannelLabel, StereoLabel, PanLabel, MixerTooltip;
        public static LocalizedString InstancesLabel, ChipLabel, CountLabel, InstancesTooltip, ChannelLimitError, HardwareExportError, EngineExportError, FormatExportError;
        public static LocalizedString ExtendedFormatExportError, ScheduledVgmExportError;
        public static LocalizedString UpdateRateLabel, UpdateRateTooltip, UpdateRateExportError, UpdateRateImportError, UpdateRateMergeError;
        public static LocalizedString UpdateRateIncompatibleError, UpdateRateRegionError, SongClockLabel, SongClockTooltip;
        public static LocalizedString EditingRateError, EditingRateAlignmentError, EditingRateRangeError, EditingRateTempoError, EditingRateEffectClockError, EditingRateEnvelopeError;
        public static LocalizedString EnvelopeClockLabel, EnvelopeClockTooltip, EnvelopeClockConversionError;
        public static LocalizedString CurveTitle, CurveZoomLabel, CurveUnitLabel, CurveValueLabel, CurveResetNote, CurveResetChip, CurveTooltip, CurveHelp, CurveMergeError;
        public static LocalizedString CurveTimerUnit, CurvePeriodUnit, CurveFrequencyUnit, CurveFmUnit, CurveDefaultLabel, CurveBoundsLabel;
        static TuningStrings() { Localization.LocalizeStatic(typeof(TuningStrings)); }
    }

    sealed class SongMixerProperties
    {
        readonly PropertyPage props;
        readonly Song song;
        readonly int grid;
        readonly bool[] originalStereo;
        readonly int[] originalPan;
        public event System.Action<bool> PreviewChanged;
        public SongMixerProperties(PropertyPage page, Song target)
        {
            props = page; song = target;
            originalStereo = new bool[song.Channels.Length];
            originalPan = new int[song.Channels.Length];
            var data = new object[song.Channels.Length, 3];
            for (int i = 0; i < song.Channels.Length; i++) { var c = song.Channels[i]; data[i, 0] = originalStereo[i] = c.StereoEnabled; data[i, 1] = c.NameWithExpansion; data[i, 2] = originalPan[i] = c.Pan; }
            grid = props.AddGrid(TuningStrings.MixerLabel, new[] { new ColumnDesc("", 0, ColumnType.CheckBox), new ColumnDesc(TuningStrings.ChannelLabel, .6f), new ColumnDesc(TuningStrings.PanLabel, .4f, 0, 100, n => $"{(int)n}%") }, data, 6, TuningStrings.MixerTooltip);
            props.PropertyChanged += (p, index, row, column, value) =>
            {
                if (index != grid) return;
                Apply();
                PreviewChanged?.Invoke(column == 0);
            };
        }
        public void RestorePreview()
        {
            bool rebuild = false;
            for (int i = 0; i < song.Channels.Length; i++)
            {
                rebuild |= song.Channels[i].StereoEnabled != originalStereo[i];
                song.Channels[i].StereoEnabled = originalStereo[i];
                song.Channels[i].Pan = originalPan[i];
            }
            PreviewChanged?.Invoke(rebuild);
        }
        public void Apply()
        {
            for (int i = 0; i < song.Channels.Length; i++) { song.Channels[i].StereoEnabled = props.GetPropertyValue<bool>(grid, i, 0); song.Channels[i].Pan = props.GetPropertyValue<int>(grid, i, 2); }
        }
    }
}
