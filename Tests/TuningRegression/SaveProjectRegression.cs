using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using FamiStudio;

static class SaveProjectRegression
{
    static int checks;
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr window, StringBuilder text, int count);
    static string Title(FamiStudioWindow window)
    {
        var text = new StringBuilder(1024); GetWindowTextW(window.Handle, text, text.Capacity); return text.ToString();
    }
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
        Console.WriteLine("PASS project save: " + message);
    }
    static object Field(object target, string name) => target.GetType().GetField(name, Private).GetValue(target);
    static void Invoke(object target, string name, params object[] values) => target.GetType().GetMethod(name, Private).Invoke(target, values);
    static void NoTemporaryFiles(string directory) => Check(!Directory.GetFiles(directory, ".*.tmp").Any(), "completed/failed write leaves no temporary project behind");

    public static void Run(string output)
    {
        checks = 0;
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var project = new Project { Name = "Original save" };
        project.CreateSong("Original song");
        var io = new ProjectFile();
        string original = Path.Combine(output, "original.fms"), copy = Path.Combine(output, "另存为-copy.fms");
        Check(io.Save(project, original), "first save creates a readable project");
        byte[] originalBytes = File.ReadAllBytes(original);
        Check(io.Load(original).Name == "Original save", "first save contains the complete original model");
        project.Name = "Edited song project";
        Check(io.Save(project, copy) && project.Filename == copy, "Save As commits a Unicode destination and changes the current path");
        Check(File.ReadAllBytes(original).SequenceEqual(originalBytes), "Save As preserves the old file byte for byte");
        Check(io.Load(copy).Name == project.Name, "Save As contains the edited model");
        NoTemporaryFiles(output);

        var beforeFailure = File.ReadAllBytes(copy);
        project.Name = "Unsaved edit";
        using (var locked = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Check(!io.Save(project, copy), "locked existing destination refuses commit");
            Check(File.ReadAllBytes(copy).SequenceEqual(beforeFailure), "failed overwrite preserves the prior file byte for byte");
            Check(project.Name == "Unsaved edit" && project.Filename == copy, "failed overwrite preserves the in-memory edit and current filename");
        }
        NoTemporaryFiles(output);
        using (var locked = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Check(!io.Save(project, original), "Save As to a locked file fails");
            Check(project.Filename == copy && File.ReadAllBytes(original).SequenceEqual(originalBytes), "failed Save As never changes current path or the destination bytes");
        }
        NoTemporaryFiles(output);
        string missing = Path.Combine(output, "missing-parent", "project.fms");
        Check(!io.Save(project, missing) && !File.Exists(missing) && project.Filename == copy, "missing parent fails without a partial file or path switch");
        string directoryTarget = Path.Combine(output, "directory-target.fms"); Directory.CreateDirectory(directoryTarget);
        Check(!io.Save(project, directoryTarget) && Directory.Exists(directoryTarget) && project.Filename == copy, "directory collision preserves destination and current path");
        NoTemporaryFiles(output);
        Check(io.Save(project, copy) && io.Load(copy).Name == "Unsaved edit", "retry after failure replaces the complete project successfully");
        string longName = Path.Combine(output, new string('x', 245) + ".fms");
        Check(io.Save(project, longName) && io.Load(longName).Name == project.Name, "legal long filename is not lengthened by the temporary-file component");
        project.Name = "Long-name overwrite";
        Check(io.Save(project, longName) && io.Load(longName).Name == project.Name, "existing legal long filename is replaced atomically");
        var beforeReadOnly = File.ReadAllBytes(longName);
        File.SetAttributes(longName, File.GetAttributes(longName) | FileAttributes.ReadOnly);
        try
        {
            project.Name = "Do not replace a read-only project";
            Check(!io.Save(project, longName) && File.ReadAllBytes(longName).SequenceEqual(beforeReadOnly), "read-only destination is refused without changing its bytes");
            Check((File.GetAttributes(longName) & FileAttributes.ReadOnly) != 0, "failed write preserves read-only metadata");
        }
        finally { File.SetAttributes(longName, File.GetAttributes(longName) & ~FileAttributes.ReadOnly); }
        string link = Path.Combine(output, "linked-project.fms");
        bool linkCreated = false;
        try { File.CreateSymbolicLink(link, copy); linkCreated = true; }
        catch (Exception e) when (e is UnauthorizedAccessException || e is PlatformNotSupportedException || e is IOException)
        { Console.WriteLine("SKIP project save symbolic-link creation unavailable: " + e.GetType().Name); }
        if (linkCreated)
        {
            try
            {
                Check(io.Save(project, link) && project.Filename == link, "save through symbolic link retains the selected link path");
                Check(new FileInfo(link).LinkTarget != null && io.Load(copy).Name == project.Name, "atomic save preserves the symbolic link and updates its actual target");
            }
            finally { File.Delete(link); }
        }
        if (!OperatingSystem.IsWindows())
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(copy, mode);
            Check(io.Save(project, copy) && File.GetUnixFileMode(copy) == mode, "Unix atomic save preserves the destination permission bits");
        }
        NoTemporaryFiles(output);
        Console.WriteLine($"All {checks} project-save checks passed.");
    }

    public static void Ui(FamiStudioWindow window, string language, string output, Action<FamiStudioWindow, string> capture)
    {
        var app = window.PianoRoll.App; app.StopSong();
        var toolbar = window.ToolBar;
        var button = (Button)Field(toolbar, "buttonSaveAs");
        var save = (Button)Field(toolbar, "buttonSave");
        var help = (Button)Field(toolbar, "buttonHelp");
        Check(button.Visible && button.Image != null && button.ImageName == "SaveAs" && button.Width == save.Width && button.Left == save.Right, "Save As icon sits next to Save with the same hit area: " + language);
        Check(button.ToolTip.Contains(Settings.FileSaveAsShortcut.TooltipString), "visible Save As action advertises the existing keyboard shortcut");
        int oldWidth = toolbar.Width;
        foreach (int logicalWidth in new[] { 1024, 800 })
        {
            toolbar.Resize(Math.Min(oldWidth, DpiScaling.ScaleForWindow(logicalWidth)), toolbar.Height);
            Check(button.Visible && button.Right < help.Left, "Save As stays visible at narrow width " + logicalWidth);
            var buttons = (System.Collections.Generic.List<Button>)Field(toolbar, "allButtons");
            Check(buttons.Where(b => b.Visible && b != help).All(b => b.Right <= help.Left), "toolbar controls avoid overlapping Help at width " + logicalWidth);
        }
        capture(window, Path.Combine(output, language + "-save-as-toolbar.ppm"));
        toolbar.Resize(oldWidth, toolbar.Height);

        string directory = Path.Combine(output, "save-project", language); Directory.CreateDirectory(directory);
        string first = Path.Combine(directory, "first.fms"), second = Path.Combine(directory, "second.fms");
        var oldRecent = Settings.RecentFiles.ToArray(); var oldLast = Settings.LastProjectFile;
        bool oldClear = Settings.ClearUndoRedoOnSave;
        // Keep the real GUI/context alive, but use the existing command-line
        // preference-write guard around saves so this test never writes user INI.
        var glfwFlag = typeof(Platform).GetField("initializedGlfw", BindingFlags.Static | BindingFlags.NonPublic);
        void Complete(string filename, Action continuation)
        {
            bool savedFlag = (bool)glfwFlag.GetValue(null);
            try { glfwFlag.SetValue(null, false); Invoke(app, "CompleteProjectSave", filename, continuation); }
            finally { glfwFlag.SetValue(null, savedFlag); }
        }
        void EditName(string name)
        {
            app.UndoRedoManager.BeginTransaction(TransactionScope.ProjectNoDPCMSamples);
            app.Project.Name = name;
            app.UndoRedoManager.EndTransaction();
        }
        try
        {
            Settings.ClearUndoRedoOnSave = false;
            int continued = 0;
            Complete(first, () => continued++);
            Check(continued == 1 && app.Project.Filename == first && !app.UndoRedoManager.NeedsSaving, "successful save commits path and clears dirty state before continuing");
            byte[] savedBytes = File.ReadAllBytes(first);
            EditName("Unsaved UI edit");
            Check(app.UndoRedoManager.NeedsSaving, "new edit after save marks the project dirty");
            var project = app.Project; var song = app.SelectedSong; string title = Title(window);
            int recentCount = Settings.RecentFiles.Count;
            Complete(null, () => continued++);
            Complete("", () => continued++);
            Check(continued == 1 && app.Project.Filename == first && app.UndoRedoManager.NeedsSaving && Title(window) == title, "cancelled Save As preserves filename, title, dirty state and continuation");
            Check(Settings.RecentFiles.Count == recentCount && File.ReadAllBytes(first).SequenceEqual(savedBytes), "cancelled Save As leaves recent files and saved bytes unchanged");
            using (var locked = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read))
                Complete(first, () => continued++);
            Check(continued == 1 && ReferenceEquals(project, app.Project) && ReferenceEquals(song, app.SelectedSong) && app.UndoRedoManager.NeedsSaving,
                  "failed save blocks close/open continuation and preserves current project/song and dirty state");
            Check(File.ReadAllBytes(first).SequenceEqual(savedBytes) && app.Project.Filename == first && Title(window) == title,
                  "failed save preserves old file, filename and title");
            Complete(second, () => continued++);
            Check(continued == 2 && app.Project.Filename == second && !app.UndoRedoManager.NeedsSaving && Title(window).Contains("second.fms"),
                  "successful Save As switches current path/title and clears the saved dirty marker");
            Check(Settings.LastProjectFile == second && Settings.RecentFiles[0] == second && new ProjectFile().Load(second).Name == "Unsaved UI edit",
                  "successful Save As updates recent/current metadata only after a complete write");
            Check(File.ReadAllBytes(first).SequenceEqual(savedBytes), "successful Save As leaves the previous saved version intact");
            app.UndoRedoManager.Undo(); Check(app.UndoRedoManager.NeedsSaving && app.Project.Filename == second, "undo after Save As marks content dirty without reverting the chosen file path");
            app.UndoRedoManager.Redo(); Check(!app.UndoRedoManager.NeedsSaving, "redo back to the saved revision clears the dirty marker");
            EditName("Next save goes to the new path");
            bool savedFlag = (bool)glfwFlag.GetValue(null);
            try { glfwFlag.SetValue(null, false); app.SaveProjectAsync(false, () => continued++); }
            finally { glfwFlag.SetValue(null, savedFlag); }
            Check(continued == 3 && new ProjectFile().Load(second).Name == app.Project.Name && File.ReadAllBytes(first).SequenceEqual(savedBytes),
                  "subsequent ordinary Save writes the Save As destination");
            Settings.ClearUndoRedoOnSave = true; EditName("Clear undo on successful save");
            Complete(second, null);
            Check(!app.UndoRedoManager.NeedsSaving && app.UndoRedoManager.UndoScope == TransactionScope.Max, "clear-undo preference applies after a successful save");
            NoTemporaryFiles(directory);
        }
        finally
        {
            Settings.ClearUndoRedoOnSave = oldClear; Settings.LastProjectFile = oldLast;
            Settings.RecentFiles.Clear(); Settings.RecentFiles.AddRange(oldRecent);
        }
    }
}
