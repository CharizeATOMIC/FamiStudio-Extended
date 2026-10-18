using System;
using System.Globalization;

namespace FamiStudio
{
    // All 96 keys share the piano roll's geometry and palette. Hardware values
    // are edited as envelope cells; zoom and scrolling never modify their data.
    public sealed class ChipTuningCurve : Container
    {
        readonly ChipTuningSettings settings;
        static readonly bool[] black = { false, true, false, true, false, false, true, false, true, false, true, false };
        readonly ScrollBar scrollBar = new ScrollBar { Horizontal = true };
        int chip, selectedNote = 46, zoomPercent = 100, scrollPixels;
        bool dragging;
        bool pinching;
        ChipTuningSettings touchSnapshot;
        int dragNote, dragStartY;
        int dragStartValue;
        public event Action SelectionChanged;
        public event Action ValueChanged;
        public event Action ViewChanged;

        public ChipTuningCurve(ChipTuningSettings model)
        {
            settings = model;
            height = DpiScaling.ScaleForWindow(250);
            canFocus = true;
            supportsDoubleClick = true;
            supportsLongPress = true;
            scrollBar.Scrolled += (_, value) => { scrollPixels = value; MarkDirty(); ViewChanged?.Invoke(); };
        }
        protected override void OnAddedToContainer() { base.OnAddedToContainer(); AddControl(scrollBar); UpdateScrollBar(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateScrollBar(); EnsureSelectedVisible(); }
        public int Chip { get => chip; set { chip = Math.Clamp(value, 0, ExpansionType.Count - 1); MarkDirty(); } }
        public int SelectedNote
        {
            get => selectedNote;
            set
            {
                value = Math.Clamp(value, 1, 96);
                if (SetAndMarkDirty(ref selectedNote, value)) { EnsureSelectedVisible(); SelectionChanged?.Invoke(); }
            }
        }
        public int ZoomPercent
        {
            get => zoomPercent;
            set => ZoomAt(value, (int)Math.Clamp(NoteX(selectedNote), GraphLeft, GraphRight));
        }
        public int VisibleFirstNote => Math.Clamp((int)Math.Floor(scrollPixels / NoteWidth) + 1, 1, 96);
        public int VisibleLastNote => Math.Clamp((int)Math.Ceiling((scrollPixels + ViewWidth) / NoteWidth), 1, 96);
        int ViewWidth => Math.Max(1, GraphRight - GraphLeft);
        double NoteWidth => ViewWidth * zoomPercent / 9600.0;
        int ContentWidth => (int)Math.Round(ViewWidth * zoomPercent / 100.0);
        int GraphLeft => DpiScaling.ScaleForWindow(48);
        int GraphRight => width - DpiScaling.ScaleForWindow(8);
        int GraphTop => DpiScaling.ScaleForWindow(12);
        int GraphBottom => DpiScaling.ScaleForWindow(164);
        int KeyTop => DpiScaling.ScaleForWindow(176);
        int KeyBottom => height - scrollBar.ScrollBarThickness - DpiScaling.ScaleForWindow(4);
        float NoteX(int note) => (float)(GraphLeft + (note - .5) * NoteWidth - scrollPixels);
        void SetScroll(int value)
        {
            scrollPixels = Math.Clamp(value, 0, Math.Max(0, ContentWidth - ViewWidth));
            scrollBar.SetScroll(scrollPixels, false); MarkDirty();
        }
        void UpdateScrollBar()
        {
            scrollBar.Move(GraphLeft, height - scrollBar.ScrollBarThickness, ViewWidth, scrollBar.ScrollBarThickness);
            scrollBar.VirtualSize = Math.Max(ViewWidth, ContentWidth);
            SetScroll(scrollPixels);
        }
        void EnsureSelectedVisible()
        {
            float x = NoteX(selectedNote);
            if (x < GraphLeft + NoteWidth * .5) SetScroll((int)((selectedNote - 1) * NoteWidth));
            else if (x > GraphRight - NoteWidth * .5) SetScroll((int)Math.Ceiling(selectedNote * NoteWidth - ViewWidth));
        }
        void ZoomAt(int value, int x)
        {
            value = Math.Clamp(value, 100, 800);
            if (value == zoomPercent) return;
            double anchor = (Math.Clamp(x, GraphLeft, GraphRight) - GraphLeft + scrollPixels) / NoteWidth;
            zoomPercent = value; UpdateScrollBar();
            SetScroll((int)Math.Round(anchor * NoteWidth - (x - GraphLeft)));
            ViewChanged?.Invoke();
        }
        const int GraphMinimum = -ChipTuningSettings.AdjustmentLimit;
        const int GraphMaximum = ChipTuningSettings.AdjustmentLimit;
        public void RefreshValues() => MarkDirty();
        float OffsetY(double offset) => (float)((GraphTop + GraphBottom) * .5 - Math.Clamp(offset, GraphMinimum, GraphMaximum) * (GraphBottom - GraphTop) / (double)(GraphMaximum - GraphMinimum));
        float NoteValueY(int note, double value) => OffsetY(value - settings.GetDefaultValue(chip, note));
        float ValueY(double value) => NoteValueY(selectedNote, value);
        int NearestNote(int x)
        {
            return Math.Clamp((int)Math.Floor((x - GraphLeft + scrollPixels) / NoteWidth) + 1, 1, 96);
        }
        RectangleF KeyRect(int note)
        {
            var rect = PianoRoll.GetHorizontalKeyboardKey(note, (float)NoteWidth, KeyBottom - KeyTop);
            return new RectangleF(rect.X + GraphLeft - scrollPixels, rect.Y + KeyTop, rect.Width, rect.Height);
        }
        int KeyboardNote(int x, int y)
        {
            for (int pass = 1; pass >= 0; pass--)
                for (int note = Math.Max(1, VisibleFirstNote - 1); note <= Math.Min(96, VisibleLastNote + 1); note++)
                {
                    var key = KeyRect(note);
                    if (black[(note - 1) % 12] == (pass == 1) && x >= key.X && x < key.X + key.Width && y >= key.Y && y < key.Y + key.Height) return note;
                }
            return NearestNote(x);
        }
        public static string NoteName(int note) => Note.GetFriendlyName(note);
        void SetValue(int note, double value, bool dragRange = true)
        {
            int integer = Math.Clamp((int)Math.Round(value), dragRange ? settings.GetMinimumValue(chip, note) : settings.GetHardwareMinimumValue(chip, note), dragRange ? settings.GetMaximumValue(chip, note) : settings.GetHardwareMaximumValue(chip, note));
            if (settings.GetValue(chip, note) == integer) return;
            settings.SetValue(chip, note, integer);
            MarkDirty(); ValueChanged?.Invoke();
        }
        protected override void OnPointerDown(PointerEventArgs e)
        {
            if (!enabled || e.X < GraphLeft || e.X > GraphRight || e.Y >= KeyBottom) return;
            if (e.Left)
            {
                GrabDialogFocus();
                SelectedNote = e.Y >= KeyTop ? KeyboardNote(e.X, e.Y) : NearestNote(e.X);
                if (e.Y >= GraphTop && e.Y <= GraphBottom)
                {
                    // Begin at the existing value; selecting a point never jumps its pitch.
                    dragNote = selectedNote; dragStartY = e.Y;
                    dragStartValue = settings.GetValue(chip, dragNote);
                    touchSnapshot = e.IsTouchEvent ? settings.Clone() : null;
                    dragging = true; CapturePointer();
                }
                e.MarkHandled();
            }
            else if (e.Right)
            {
                SelectedNote = e.Y >= KeyTop ? KeyboardNote(e.X, e.Y) : NearestNote(e.X);
                EnterValue(); e.MarkHandled();
            }
        }
        protected override void OnPointerMove(PointerEventArgs e)
        {
            if (!dragging || pinching) return;
            int targetNote = NearestNote(e.X);
            if (targetNote != dragNote && !ModifierKeys.IsShiftDown)
            {
                double paintedOffset = GraphMinimum + (GraphBottom - e.Y) * (GraphMaximum - GraphMinimum) / (double)(GraphBottom - GraphTop);
                for (int note = Math.Min(dragNote, targetNote); note <= Math.Max(dragNote, targetNote); note++) SetValue(note, settings.GetDefaultValue(chip, note) + paintedOffset);
                selectedNote = targetNote; dragNote = targetNote; dragStartY = e.Y; dragStartValue = settings.GetValue(chip, dragNote);
                SelectionChanged?.Invoke(); e.MarkHandled(); return;
            }
            if (e.Y == dragStartY)
            {
                // A horizontal drag (or returning to the start) preserves the exact register value.
                settings.SetValue(chip, dragNote, dragStartValue);
                MarkDirty(); ValueChanged?.Invoke(); e.MarkHandled(); return;
            }
            double sensitivity = ModifierKeys.IsShiftDown ? .1 : 1;
            SetValue(dragNote, dragStartValue + (dragStartY - e.Y) * Math.Max(1, GraphMaximum - GraphMinimum) / (double)(GraphBottom - GraphTop) * sensitivity);
            e.MarkHandled();
        }
        protected override void OnPointerUp(PointerEventArgs e)
        {
            if (dragging) { dragging = false; ReleasePointer(); e.MarkHandled(); }
            // Android can deliver a cancelled gesture's pointer-up before scale-end.
            // Always leave the editor ready for the next single-finger edit.
            if (pinching) { pinching = false; ReleasePointer(); e.MarkHandled(); }
            touchSnapshot = null;
        }
        protected override void OnMouseDoubleClick(PointerEventArgs e)
        {
            if (e.Left && e.X >= GraphLeft && e.X <= GraphRight && e.Y < KeyBottom)
            {
                if (dragging) { dragging = false; ReleasePointer(); }
                SelectedNote = e.Y >= KeyTop ? KeyboardNote(e.X, e.Y) : NearestNote(e.X);
                EnterValue(); e.MarkHandled();
            }
        }
        void EnterValue()
        {
            int targetNote = selectedNote, targetChip = chip;
            int displayed = settings.GetValue(chip, targetNote);
            var input = new ValueInputDialog(ParentWindow, WindowPosition, NoteName(targetNote), displayed, settings.GetHardwareMinimumValue(chip, targetNote), settings.GetHardwareMaximumValue(chip, targetNote), false, 1);
            input.ShowDialogAsync(result =>
            {
                if (result == DialogResult.OK && (int)input.Value != displayed)
                {
                    settings.SetValue(targetChip, targetNote, (int)input.Value);
                    MarkDirty(); ValueChanged?.Invoke();
                }
            });
        }
        void TouchSelect(PointerEventArgs e, bool enter)
        {
            if (!enabled || pinching || e.X < GraphLeft || e.X > GraphRight || e.Y >= KeyBottom) return;
            if (dragging) { dragging = false; ReleasePointer(); }
            touchSnapshot = null;
            SelectedNote = e.Y >= KeyTop ? KeyboardNote(e.X, e.Y) : NearestNote(e.X);
            if (enter) EnterValue();
            e.MarkHandled();
        }
        protected override void OnTouchClick(PointerEventArgs e) => TouchSelect(e, false);
        protected override void OnTouchDoubleClick(PointerEventArgs e) => TouchSelect(e, true);
        protected override void OnTouchLongPress(PointerEventArgs e) => TouchSelect(e, true);
        protected override void OnTouchScaleBegin(PointerEventArgs e)
        {
            if (!enabled) return;
            if (dragging) { dragging = false; ReleasePointer(); }
            // Recognizing a second finger turns a tentative edit into a zoom.
            if (touchSnapshot != null)
            {
                for (int n = 1; n <= 96; n++)
                    if (touchSnapshot.HasOverride(chip, n)) settings.SetValue(chip, n, touchSnapshot.GetValue(chip, n));
                    else settings.ClearPoint(chip, n);
                touchSnapshot = null; ValueChanged?.Invoke();
            }
            pinching = true; CapturePointer(); e.MarkHandled();
        }
        protected override void OnTouchScale(PointerEventArgs e)
        {
            if (!enabled || !pinching || !float.IsFinite(e.TouchScale) || e.TouchScale <= 0) return;
            ZoomAt((int)Math.Clamp(zoomPercent * e.TouchScale, 100, 800), e.X); e.MarkHandled();
        }
        protected override void OnTouchScaleEnd(PointerEventArgs e) { if (pinching) ReleasePointer(); pinching = false; e.MarkHandled(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!enabled) return;
            if (e.Key == Keys.Left || e.Key == Keys.Right) SelectedNote += e.Key == Keys.Left ? -1 : 1;
            else if (e.Key == Keys.Up || e.Key == Keys.Down) SetValue(selectedNote, settings.GetValue(chip, selectedNote) + (e.Key == Keys.Up ? 1 : -1) * (e.Shift ? 10 : 1), false);
            else if (e.Key == Keys.Delete || e.Key == Keys.Home) SetValue(selectedNote, settings.GetDefaultValue(chip, selectedNote));
            else if (e.Key == Keys.F2 || e.Key == Keys.Enter || e.Key == Keys.KeypadEnter) EnterValue();
            else return;
            e.Handled = true;
        }
        protected override void OnMouseWheel(PointerEventArgs e)
        {
            if (ModifierKeys.IsControlDown) ZoomAt(e.ScrollY > 0 ? zoomPercent * 2 : zoomPercent / 2, e.X);
            else SetScroll(scrollPixels - (int)Math.Round((e.ScrollX != 0 ? e.ScrollX : e.ScrollY) * NoteWidth * 6));
            ViewChanged?.Invoke(); e.MarkHandled();
        }
        protected override void OnRender(Graphics g)
        {
            var c = g.GetCommandList();
            var accent = Color.FromArgb(255, 81, 190, 228);
            c.FillRectangle(0, 0, width - 1, height - 1, Theme.DarkGreyColor1);
            for (int row = 0; row <= 2; row++)
            {
                double value = GraphMinimum + (GraphMaximum - GraphMinimum) * row / 2.0;
                float y = OffsetY(value);
                c.DrawLine(GraphLeft, y, GraphRight, y, Theme.DarkGreyColor4);
                c.DrawText(value.ToString("+0;-0;0", CultureInfo.InvariantCulture), fonts.FontSmall, 0, y - DpiScaling.ScaleForWindow(8), Theme.LightGreyColor1, TextFlags.MiddleRight, GraphLeft - DpiScaling.ScaleForWindow(5), DpiScaling.ScaleForWindow(16));
            }
            c.PushClipRegion(GraphLeft, GraphTop, ViewWidth, KeyBottom - GraphTop);
            for (int note = VisibleFirstNote; note <= VisibleLastNote; note++)
            {
                int actual = settings.GetValue(chip, note), normal = settings.GetDefaultValue(chip, note);
                float x = NoteX(note), y = NoteValueY(note, actual), zeroY = OffsetY(0);
                float left = x - (float)NoteWidth * .5f, right = left + (float)NoteWidth;
                c.FillRectangle(left, GraphTop, right, GraphBottom, (note - 1) / 12 % 2 == 0 ? Theme.DarkGreyColor4 : Theme.DarkGreyColor5);
                // Match the arpeggio editor's discrete value cells, with no fill
                // between the value and zero. Larger hit targets remain invisible.
                float cellHeight = DpiScaling.ScaleForWindow(7);
                float top = Math.Clamp(y - cellHeight * .5f, GraphTop, GraphBottom - cellHeight), bottom = top + cellHeight;
                c.DrawLine(left, zeroY, right, zeroY, Theme.MediumGreyColor1);
                c.FillAndDrawRectangle(left, top, right, bottom, accent, note == selectedNote ? Theme.LightGreyColor1 : Theme.BlackColor, note == selectedNote ? 2 : 1);
                if (Math.Abs(actual - normal) > ChipTuningSettings.AdjustmentLimit)
                {
                    int sign = actual > normal ? 1 : -1;
                    float edge = sign > 0 ? GraphTop + DpiScaling.Window : GraphBottom - DpiScaling.Window;
                    c.DrawLine(x - DpiScaling.ScaleForWindow(3), edge + sign * DpiScaling.ScaleForWindow(4), x, edge, Theme.LightGreyColor1);
                    c.DrawLine(x + DpiScaling.ScaleForWindow(3), edge + sign * DpiScaling.ScaleForWindow(4), x, edge, Theme.LightGreyColor1);
                }
                string number = actual.ToString(CultureInfo.InvariantCulture);
                if (fonts.FontSmall.MeasureString(number, false) + DpiScaling.ScaleForWindow(4) < NoteWidth)
                {
                    float labelY = top - DpiScaling.ScaleForWindow(16);
                    if (labelY < GraphTop) labelY = bottom;
                    labelY = Math.Clamp(labelY, GraphTop, GraphBottom - DpiScaling.ScaleForWindow(16));
                    c.DrawText(number, fonts.FontSmall, left, labelY, labelY >= top && labelY < bottom ? Theme.BlackColor : Theme.LightGreyColor1, TextFlags.MiddleCenter, (float)NoteWidth, DpiScaling.ScaleForWindow(16));
                }
            }
            for (int pass = 0; pass < 2; pass++)
            {
                for (int note = Math.Max(1, VisibleFirstNote - 1); note <= Math.Min(96, VisibleLastNote + 1); note++)
                {
                    bool isBlack = black[(note - 1) % 12];
                    if (isBlack != (pass == 1)) continue;
                    var key = KeyRect(note);
                    float x = key.X, bottom = key.Y + key.Height, keyWidth = key.Width;
                    PianoRoll.FillKeyboardKey(c, x, KeyTop, x + keyWidth, bottom, isBlack, true);
                    if (note == selectedNote) c.FillRectangle(x, KeyTop, x + keyWidth, bottom, PianoRoll.GetKeyboardHighlight(isBlack));
                    var textColor = isBlack ? Theme.LightGreyColor1 : Theme.DarkGreyColor1;
                    if (!isBlack && ((note - 1) % 12 == 0 || (note - 1) % 12 == 5)) c.DrawLine(x, KeyTop, x, bottom, Theme.BlackColor);
                    string label = NoteName(note);
                    if (fonts.FontSmall.MeasureString(label, false) + 2 < keyWidth)
                        c.DrawText(label, fonts.FontSmall, x, bottom - DpiScaling.ScaleForWindow(20), textColor, TextFlags.MiddleCenter, keyWidth, DpiScaling.ScaleForWindow(20));
                }
            }
            for (int octave = 0; octave < 8; octave++)
            {
                float x = NoteX(octave * 12 + 1) - (float)NoteWidth * .5f;
                c.DrawText("C" + octave, fonts.FontVerySmall, x, GraphBottom, Theme.LightGreyColor1, TextFlags.MiddleCenter, (float)NoteWidth * 12, KeyTop - GraphBottom);
            }
            c.PopClipRegion();
            base.OnRender(g);
        }
    }
}
