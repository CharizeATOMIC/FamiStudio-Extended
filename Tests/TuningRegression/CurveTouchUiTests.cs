using System;
using System.Reflection;
using FamiStudio;

// Exercises the shared control's real touch dispatch on the desktop UI host.
// It does not emulate Android's gesture recognizer or require an Android SDK.
static class CurveTouchUiTests
{
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly FieldInfo CaptureField = typeof(FamiStudioWindow).GetField("captureControl", PrivateInstance);
    static readonly FieldInfo LastButtonField = typeof(FamiStudioWindow).GetField("lastButtonPress", PrivateInstance);

    static int Dimension(ChipTuningCurve curve, string name) =>
        (int)typeof(ChipTuningCurve).GetProperty(name, PrivateInstance).GetValue(curve);
    static int Coordinate(ChipTuningCurve curve, string method, object value) =>
        (int)(float)typeof(ChipTuningCurve).GetMethod(method, PrivateInstance).Invoke(curve, new[] { value });
    static PointerEventArgs Touch(int x, int y, float scale = 1) => new PointerEventArgs(x, y, scale);
    static bool CapturedBy(FamiStudioWindow window, Control control) => ReferenceEquals(CaptureField.GetValue(window), control);
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Curve touch UI: " + message);
        Console.WriteLine("PASS curve touch UI: " + message);
    }

    public static void Run(FamiStudioWindow window)
    {
        Check(CaptureField != null && LastButtonField != null, "desktop capture instrumentation is available");
        var parentDialog = window.TopDialog;
        var original = new ChipTuningSettings();
        original.SetValue(ExpansionType.None, 46, original.GetDefaultValue(ExpansionType.None, 46) + 3);
        original.SetValue(ExpansionType.S5B, 46, original.GetDefaultValue(ExpansionType.S5B, 46) + 7);
        var untouched = original.Clone();
        var editor = new ChipTuningDialog(window, original);
        int lastButton = (int)LastButtonField.GetValue(window);
        try
        {
            // Desktop capture normally receives this from GLFW button dispatch.
            // Set the same precondition while sending native touch-event objects.
            LastButtonField.SetValue(window, 0);
            editor.ShowDialogAsync(_ => { });
            var curve = editor.Curve;
            var settings = editor.Settings;
            curve.ZoomPercent = 400;
            curve.SelectedNote = 46;
            int graphTop = Dimension(curve, "GraphTop"), graphBottom = Dimension(curve, "GraphBottom");
            int keyTop = Dimension(curve, "KeyTop"), keyBottom = Dimension(curve, "KeyBottom");
            int normal = settings.GetDefaultValue(ExpansionType.None, 46);
            int valueChanges = 0, viewChanges = 0;
            curve.ValueChanged += () => valueChanges++;
            curve.ViewChanged += () => viewChanges++;

            var beforeSelection = settings.Clone();
            int x = Coordinate(curve, "NoteX", 37);
            var tap = Touch(x, keyBottom - 3);
            curve.SendTouchClick(tap);
            Check(tap.IsTouchEvent && tap.Handled && curve.SelectedNote == 37,
                "single-finger white-key tap selects its note");
            x = Coordinate(curve, "NoteX", 38);
            curve.SendTouchClick(Touch(x, keyTop + 5));
            Check(curve.SelectedNote == 38 && settings.ContentEquals(beforeSelection),
                "single-finger sharp-key selection leaves all period values unchanged");

            curve.SelectedNote = 46;
            x = Coordinate(curve, "NoteX", 46);
            int y = Coordinate(curve, "ValueY", (double)(normal + 3));
            var down = Touch(x, y);
            curve.SendPointerDown(down);
            Check(down.Handled && CapturedBy(window, curve) && settings.GetValue(0, 46) == normal + 3,
                "touching a value captures the pointer without changing its absolute value");
            curve.SendPointerMove(Touch(x, y - 10000));
            Check(settings.GetValue(0, 46) == normal + 50,
                "single-finger upward drag clamps at default plus fifty");
            curve.SendPointerUp(Touch(x, y - 10000));
            Check(CapturedBy(window, null), "single-finger release clears actual window pointer capture");
            var afterRelease = settings.Clone();
            curve.SendPointerMove(Touch(x, y + 10000));
            Check(settings.ContentEquals(afterRelease), "a released finger cannot continue editing values");
            curve.SendPointerDown(Touch(x, graphTop));
            curve.SendPointerMove(Touch(x, graphBottom + 10000));
            curve.SendPointerUp(Touch(x, graphBottom + 10000));
            Check(settings.GetValue(0, 46) == normal - 50 && CapturedBy(window, null),
                "single-finger downward drag clamps at default minus fifty and releases capture");

            // Long press must terminate the pending drag before opening input.
            curve.SendPointerDown(Touch(x, graphBottom));
            Check(CapturedBy(window, curve), "long-press gesture starts from a captured touch");
            var longPress = Touch(x, graphBottom);
            curve.SendTouchLongPress(longPress);
            Check(longPress.Handled && window.TopDialog is PropertyDialog && window.TopDialog != editor.Dialog && CapturedBy(window, null),
                "long press opens absolute-value input and releases the pending drag");
            var valueDialog = (PropertyDialog)window.TopDialog;
            var number = (NumericUpDown)NumericUiTests.ControlAt(valueDialog.Properties, 0);
            Check(number.Value == normal - 50, "long-press input displays the hardware period rather than its offset");
            NumericUiTests.SubmitPopup(window, "1000");
            curve.SendPointerUp(Touch(x, graphBottom));
            Check(settings.GetValue(0, 46) == 1000 && window.TopDialog == editor.Dialog && CapturedBy(window, null),
                "touch value entry accepts a valid absolute period outside the drag band");

            curve.SendPointerDown(Touch(x, graphTop));
            var doubleTap = Touch(x, graphTop);
            curve.SendTouchDoubleClick(doubleTap);
            Check(doubleTap.Handled && window.TopDialog != editor.Dialog && CapturedBy(window, null),
                "double tap opens absolute-value input and releases pointer capture");
            NumericUiTests.SubmitPopup(window, "1100", true);
            curve.SendPointerUp(Touch(x, graphTop));
            Check(settings.GetValue(0, 46) == 1000, "cancelling touch value input preserves the exact period");

            // Deliberately modify several cells before recognizing a second
            // finger. Rollback must restore both values and override presence.
            settings.SetValue(0, 43, settings.GetDefaultValue(0, 43) + 7);
            settings.ClearPoint(0, 44);
            settings.SetValue(0, 45, settings.GetDefaultValue(0, 45) - 12);
            curve.SelectedNote = 43;
            var beforePinch = settings.Clone();
            int startX = Coordinate(curve, "NoteX", 43), endX = Coordinate(curve, "NoteX", 45);
            curve.SendPointerDown(Touch(startX, (graphTop + graphBottom) / 2));
            curve.SendPointerMove(Touch(endX, graphTop + 3));
            Check(!settings.ContentEquals(beforePinch) && settings.HasOverride(0, 44),
                "first finger can tentatively paint intermediate cells before a pinch is recognized");
            var begin = Touch(endX, graphTop + 3);
            curve.SendTouchScaleBegin(begin);
            Check(begin.Handled && CapturedBy(window, curve) && settings.ContentEquals(beforePinch) && !settings.HasOverride(0, 44),
                "second finger restores every tentative cell edit and original override presence");
            var zoom = Touch(endX, graphTop + 3, 2);
            curve.SendTouchScale(zoom);
            Check(zoom.Handled && curve.ZoomPercent == 800 && viewChanges > 0 && settings.ContentEquals(beforePinch),
                "two-finger pinch doubles viewport zoom without changing hardware values");
            curve.SendPointerMove(Touch(endX, graphBottom + 10000));
            curve.SendTouchLongPress(Touch(endX, graphTop + 3));
            Check(settings.ContentEquals(beforePinch) && window.TopDialog == editor.Dialog,
                "moves and long presses during a pinch cannot paint or open value input");
            foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
                curve.SendTouchScale(Touch(endX, graphTop + 3, invalid));
            Check(curve.ZoomPercent == 800 && settings.ContentEquals(beforePinch),
                "invalid pinch factors preserve the viewport and period data");
            var end = Touch(endX, graphTop + 3);
            curve.SendTouchScaleEnd(end);
            curve.SendPointerUp(Touch(endX, graphTop + 3));
            Check(end.Handled && CapturedBy(window, null), "ending a pinch releases actual pointer capture");
            curve.SendTouchScale(Touch(endX, graphTop + 3, .25f));
            Check(curve.ZoomPercent == 800, "scale updates after gesture end cannot change the viewport");

            curve.SelectedNote = 44;
            x = Coordinate(curve, "NoteX", 44);
            y = (graphTop + graphBottom) / 2;
            curve.SendPointerDown(Touch(x, y));
            curve.SendPointerMove(Touch(x, y - 15));
            curve.SendPointerUp(Touch(x, y - 15));
            Check(settings.GetValue(0, 44) > settings.GetDefaultValue(0, 44) && CapturedBy(window, null),
                "a new single-finger edit works normally after pinch completion");
            var committedTouch = settings.Clone();
            curve.SendTouchScaleBegin(Touch(x, y));
            curve.SendTouchScale(Touch(x, y, .25f));
            curve.SendTouchScaleEnd(Touch(x, y));
            Check(curve.ZoomPercent == 200 && settings.ContentEquals(committedTouch) && CapturedBy(window, null),
                "a later pinch preserves edits completed before that gesture");

            // Android cancellation can route PointerUp to the captured control
            // while the later ScaleEnd goes elsewhere. PointerUp must suffice.
            curve.SendTouchScaleBegin(Touch(x, y));
            curve.SendTouchScale(Touch(x, y, 2));
            int cancelledZoom = curve.ZoomPercent;
            curve.SendPointerUp(Touch(x, y));
            Check(CapturedBy(window, null), "pointer-up cancellation releases a pinch without waiting for scale-end");
            curve.SendTouchScale(Touch(x, y, 2));
            Check(curve.ZoomPercent == cancelledZoom && settings.ContentEquals(committedTouch),
                "a cancelled pinch ignores trailing scale updates");
            curve.SelectedNote = 44;
            x = Coordinate(curve, "NoteX", 44);
            int beforeNewDrag = settings.GetValue(0, 44);
            y = Coordinate(curve, "ValueY", (double)beforeNewDrag);
            curve.SendPointerDown(Touch(x, y));
            curve.SendPointerMove(Touch(x, y - 10));
            curve.SendPointerUp(Touch(x, y - 10));
            Check(settings.GetValue(0, 44) > beforeNewDrag && CapturedBy(window, null),
                "a fresh drag works after pointer-up cancelled the previous pinch");
            var afterCancellation = settings.Clone();
            curve.SendTouchScaleEnd(Touch(x, y));
            Check(settings.ContentEquals(afterCancellation) && CapturedBy(window, null),
                "a late scale-end is harmless after cancellation and a completed new drag");
            Check(valueChanges > 0 && settings.GetValue(ExpansionType.S5B, 46) == untouched.GetValue(ExpansionType.S5B, 46),
                "touch edits notify value listeners and leave the other chip independent");
            editor.Dialog.Close(DialogResult.Cancel);
            Check(original.ContentEquals(untouched), "cancelling the touch editor leaves its source settings unchanged");
        }
        finally
        {
            window.ReleasePointer();
            while (window.TopDialog != null && window.TopDialog != parentDialog)
                window.TopDialog.Close(DialogResult.Cancel);
            LastButtonField.SetValue(window, lastButton);
        }
    }
}
