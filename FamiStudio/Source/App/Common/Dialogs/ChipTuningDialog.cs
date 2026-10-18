using System;
using System.Linq;

namespace FamiStudio
{
    public sealed class ChipTuningDialog
    {
        readonly PropertyDialog dialog;
        readonly PropertyPage page;
        readonly ChipTuningCurve curve;
        readonly int chipIndex, zoomIndex, unitIndex, noteIndex, valueIndex, resetNoteIndex, resetChipIndex;
        bool updating;
        public ChipTuningSettings Settings { get; }
        public ChipTuningCurve Curve => curve;
        public PropertyDialog Dialog => dialog;

        public ChipTuningDialog(FamiStudioWindow window, ChipTuningSettings original)
        {
            Settings = original.Clone();
            dialog = new PropertyDialog(window, TuningStrings.CurveTitle, 740, true, true, 600);
            page = dialog.Properties;
            // LocalizedChipNames is a cached clone; use the current language's names.
            var chips = Enumerable.Range(0, ExpansionType.Count).Select(i => i == 0 ? "APU / 2A03" : ExpansionType.LocalizedNames[i].ToString()).ToArray();
            chipIndex = page.AddDropDownList(TuningStrings.ChipLabel, chips, chips[0]);
            zoomIndex = page.AddNumericUpDown(TuningStrings.CurveZoomLabel, 100, 100, 800, 25);
            unitIndex = page.AddLabel(TuningStrings.CurveUnitLabel, "", true);
            curve = new ChipTuningCurve(Settings) { ToolTip = TuningStrings.CurveTooltip };
            page.AddCustomControl(curve);
            var notes = Enumerable.Range(1, 96).Select(ChipTuningCurve.NoteName).ToArray();
            noteIndex = page.AddDropDownList(TuningStrings.NoteLabel, notes, notes[45]);
            valueIndex = page.AddNumericUpDown(TuningStrings.CurveValueLabel, Settings.GetValue(0, 46), Settings.GetHardwareMinimumValue(0, 46), Settings.GetHardwareMaximumValue(0, 46), 1);
            resetNoteIndex = page.AddButton(null, TuningStrings.CurveResetNote);
            resetChipIndex = page.AddButton(null, TuningStrings.CurveResetChip);
            page.AddLabel(null, TuningStrings.CurveHelp, true);
            page.PropertyChanged += PropertyChanged;
            page.PropertyClicked += PropertyClicked;
            curve.SelectionChanged += RefreshSelection;
            curve.ValueChanged += RefreshSelection;
            curve.ViewChanged += RefreshSelection;
            RefreshSelection();
            page.Build();
        }

        string UnitInformation()
        {
            int chip = curve.Chip, note = curve.SelectedNote;
            string unit = Settings.GetUnitKind(chip) switch
            {
                ChipTuningUnit.Timer => TuningStrings.CurveTimerUnit,
                ChipTuningUnit.Period => TuningStrings.CurvePeriodUnit,
                ChipTuningUnit.FrequencyWord => TuningStrings.CurveFrequencyUnit,
                _ => TuningStrings.CurveFmUnit + $" ({Settings.GetFmBlock(chip, note)})"
            };
            if (chip == ExpansionType.EPSM) unit = "PSG: " + unit;
            string basis = $"{Settings.BasisReferenceHz} Hz / {(Settings.BasisPal ? "PAL" : "NTSC")}";
            if (chip == ExpansionType.N163) basis += $" / N163 × {Settings.BasisN163Channels}";
            return $"{unit} — {basis}\n{TuningStrings.CurveDefaultLabel}: {Settings.GetDefaultValue(chip, note)}    {TuningStrings.CurveBoundsLabel}: {Settings.GetMinimumValue(chip, note)} – {Settings.GetMaximumValue(chip, note)}";
        }

        void RefreshSelection()
        {
            updating = true;
            page.SetPropertyValue(zoomIndex, curve.ZoomPercent);
            page.SetDropDownListIndex(noteIndex, curve.SelectedNote - 1);
            page.UpdateIntegerRange(valueIndex, Settings.GetValue(curve.Chip, curve.SelectedNote), Settings.GetHardwareMinimumValue(curve.Chip, curve.SelectedNote), Settings.GetHardwareMaximumValue(curve.Chip, curve.SelectedNote));
            page.SetLabelText(unitIndex, UnitInformation());
            updating = false;
            curve.RefreshValues();
        }
        void PropertyChanged(PropertyPage sender, int index, int row, int column, object value)
        {
            if (updating) return;
            if (index == chipIndex) curve.Chip = page.GetSelectedIndex(index);
            else if (index == zoomIndex) curve.ZoomPercent = page.GetPropertyValue<int>(index);
            else if (index == noteIndex) curve.SelectedNote = page.GetSelectedIndex(index) + 1;
            else if (index == valueIndex) Settings.SetValue(curve.Chip, curve.SelectedNote, page.GetPropertyValue<int>(index));
            RefreshSelection();
        }
        void PropertyClicked(PropertyPage sender, ClickType click, int index, int row, int column)
        {
            if (index == resetNoteIndex) Settings.ClearPoint(curve.Chip, curve.SelectedNote);
            else if (index == resetChipIndex) Settings.ResetChip(curve.Chip);
            else return;
            RefreshSelection();
        }
        public void ShowDialogAsync(Action<DialogResult> callback) => dialog.ShowDialogAsync(callback);
    }
}
