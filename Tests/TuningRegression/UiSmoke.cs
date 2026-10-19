using System;
using System.IO;
using System.Reflection;
using FamiStudio;
using static GLFWDotNet.GLFW;

static class UiSmoke
{
    static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
    static unsafe void Capture(FamiStudioWindow window, string path)
    {
        var container = (Container)Field(window, "container");
        var size = window.Size;
        window.Graphics.BeginDrawFrame(new Rectangle(Point.Empty, size), true, Theme.DarkGreyColor2);
        container.Render(window.Graphics);
        window.Graphics.EndDrawFrame();
        GL.Finish();
        var pixels = new byte[size.Width * size.Height * 4];
        fixed (byte* ptr = pixels) GL.ReadPixels(0, 0, size.Width, size.Height, GL.Rgba, GL.UnsignedByte, (IntPtr)ptr);
        using var stream = File.Create(path);
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{size.Width} {size.Height}\n255\n");stream.Write(header);
        for (int y = size.Height - 1; y >= 0; y--) for (int x = 0; x < size.Width; x++) stream.Write(pixels, (y * size.Width + x) * 4, 3);
    }
    public static void Run(string[] args)
    {
        Init.InitializeBaseSystems(false);
        Settings.CheckUpdates = false; Settings.ShowTutorial = false; Settings.OpenLastProjectOnStart = false;
        if (args.Length > 4) Settings.DpiScaling = int.Parse(args[4]);
        string language = args[1], output = Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
        var strings = new IniFile();
        if (!strings.Load(Path.Combine(AppContext.BaseDirectory, "Localization", $"FamiStudio.{language}.ini"))) throw new Exception("Missing localization: " + language);
        typeof(Localization).GetField("strings", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, strings);
        Localization.LanguageCode = language;
        foreach (var type in new[] { typeof(ChannelType), typeof(ExpansionType), typeof(TuningStrings) }) Localization.LocalizeStatic(type);
        glfwWindowHint(GLFW_VISIBLE, GLFW_FALSE);
        var app = new FamiStudio.FamiStudio();
        var window = FamiStudioWindow.CreateWindow(app);
        var native = (IntPtr)Field(window, "window");
        glfwHideWindow(native);
        glfwSetWindowSize(native, 1280, 900);
        app.Initialize(window, args.Length > 3 ? Path.GetFullPath(args[3]) : null);
        window.RunEventLoop();
        if (args.Length > 5 && args[5] == "--gimmick-final") GimmickFinalUiInspection.Run(window, output, Capture);
        if (args.Length > 3) LoopVariantUiTests.InspectExisting(window, language, output, Capture);
        bool initialStereo = app.SelectedSong.Channels[0].StereoEnabled;
        int initialPan = app.SelectedSong.Channels[0].Pan;
        Invoke(window.ProjectExplorer, "EditSongProperties", app.Project.Songs[0], false);
        Capture(window, Path.Combine(output, language + "-song.ppm"));
        var dialog = (PropertyDialog)window.TopDialog;
        var scrolling = (ScrollContainer)Field(dialog, "propertyContainer");
        scrolling.ScrollY = Math.Max(0, scrolling.VirtualSizeY - scrolling.Height);
        Capture(window, Path.Combine(output, language + "-song-bottom.ppm"));
        if (language == "zho")
        {
            var page = dialog.Properties;
            int index = page.PropertyCount - 1;
            var cells = (System.Collections.IList)Field(page, "properties");
            var mixGrid = (Grid)cells[index].GetType().GetField("control").GetValue(cells[index]);
            app.PlaySong();
            page.SetPropertyValue(index, 0, 0, true);
            Invoke(page, "Grid_ValueChanged", mixGrid, 0, 0, true);
            page.SetPropertyValue(index, 0, 2, 0);
            Invoke(page, "Grid_ValueChanged", mixGrid, 0, 2, 0);
            if (!app.IsPlaying || !app.SelectedSong.Channels[0].StereoEnabled || app.SelectedSong.Channels[0].Pan != 0)
                throw new Exception("Live mixer did not apply while playing.");
            mixGrid.UpdateSelectedRow(0);
            Invoke(mixGrid, "EnterSliderValue");
            NumericUiTests.SubmitPopup(window, "73");
            if (!app.IsPlaying || app.SelectedSong.Channels[0].Pan != 73)
                throw new Exception("Typed pan did not apply while playing.");
            Console.WriteLine("PASS numeric UI: typed pan applies during playback");
        }
        dialog.Close(DialogResult.Cancel);
        if (app.SelectedSong.Channels[0].StereoEnabled != initialStereo || app.SelectedSong.Channels[0].Pan != initialPan) throw new Exception("Cancel did not restore mixer.");
        app.StopSong();
        if (language == "zho") NumericUiTests.Run(window);
        Invoke(window.Sequencer, "EditPatternCustomSettings", Point.Empty, args.Length > 3 ? 2 : 0);
        Capture(window, Path.Combine(output, language + "-pattern.ppm"));
        window.TopDialog.Close(DialogResult.Cancel);
        var projectDialog = new ProjectPropertiesDialog(window, app.Project);
        projectDialog.EditProjectPropertiesAsync(_ => {});
        var multi = (MultiPropertyDialog)window.TopDialog;
        Invoke(multi, "SetSelectedTab", 1);
        var expansion = projectDialog.ExpansionPage;
        var properties = (System.Collections.IList)Field(expansion, "properties");
        var grid = (Grid)properties[0].GetType().GetField("control").GetValue(properties[0]);
        grid.UpdateData(0, 0, true); grid.UpdateData(0, 2, 2);
        if (language == "zho") NumericUiTests.Expansion(window, grid, expansion);
        Capture(window, Path.Combine(output, language + "-expansion.ppm"));
        multi.Close(DialogResult.Cancel);
        CurveUiTests.Run(window, language, output, Capture);
        if (language == "zho") CurveTouchUiTests.Run(window);
        NsfMultiImportRegression.Ui(window, Path.Combine(output, "nsf"), (ok, message) => { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }, Capture);
        if (language == "zho" || language == "eng") LoopVariantUiTests.Run(window, language, output, Capture);
        InstrumentArticulationRegression.Ui(window, language, output, Capture);
        DpcmMappingUiTests.Run(window, language, output, Capture);
        UpdateRateUiTests.Run(window, language, output, Capture);
        Invoke(app, "ShutdownAudioPlayers", true);
        Invoke(window, "DestroyWindow");
        Init.ShutdownBaseSystems();
        Console.WriteLine("PASS UI " + language + " scale=" + Settings.DpiScaling);
    }
}
