using System;
using System.Linq;

namespace FamiStudio
{
    class NsfImportDialog
    {
        private PropertyDialog dialog;
        private string[] songNames;
        private int[] songDurations;
        private string filename;

        #region Localization
        LocalizedString NsfImportTitle;
        LocalizedString SongLabel;
        LocalizedString DurationLabel;
        LocalizedString PatternLength;
        LocalizedString StartFrameLabel;
        LocalizedString RemoveIntroSilenceLabel;
        LocalizedString ReverseDPCMBitsLabel;
        LocalizedString PreserveDPCMPaddingByte;
        LocalizedString ImportDMCValuesLabel;
        LocalizedString TuningLabel;
        LocalizedString SongsTooltip;
        LocalizedString ImportingLabel;
        #endregion

        public NsfImportDialog(FamiStudioWindow win, string file)
        {
            Localization.Localize(this);
            filename = file;
            songNames = NsfFile.GetSongNamesAndDurations(filename, out songDurations);
            if (songNames == null || songNames.Length == 0)
                return;

            var selections = new object[songNames.Length, 3];
            for (int i = 0; i < songNames.Length; i++)
            {
                selections[i, 0] = i == 0;
                selections[i, 1] = $"{i + 1}. {songNames[i]}";
                selections[i, 2] = songDurations[i] <= 0 ? 120 : Math.Min(songDurations[i], 600);
            }

            dialog = new PropertyDialog(win, NsfImportTitle, 500, scroll: 580);
            dialog.Properties.AddGrid(null, new[] {
                new ColumnDesc("", 0, ColumnType.CheckBox),
                new ColumnDesc(SongLabel, 0.6f) { Ellipsis = true },
                new ColumnDesc(DurationLabel, 0.4f, 1, 600)
            }, selections, Math.Min(songNames.Length + 1, 9), SongsTooltip);                          // 0
            dialog.Properties.AddNumericUpDown(PatternLength.Colon, 256, 4, Pattern.MaxLength, 1);     // 1
            dialog.Properties.AddNumericUpDown(StartFrameLabel.Colon, 0, 0, Pattern.MaxLength - 1, 1); // 2
            dialog.Properties.AddNumericUpDown(TuningLabel.Colon, 440, 300, 580, 1);                   // 3
            dialog.Properties.AddCheckBox(RemoveIntroSilenceLabel.Colon, true);                        // 4
            dialog.Properties.AddCheckBox(ReverseDPCMBitsLabel.Colon, false);                          // 5
            dialog.Properties.AddCheckBox(PreserveDPCMPaddingByte.Colon, false);                       // 6
            dialog.Properties.AddCheckBox(ImportDMCValuesLabel.Colon, true);                           // 7
            dialog.ValidateProperties += props => SelectedSongIndices().Length > 0;
#if !FAMISTUDIO_MOBILE
            // PropertyDialog's default Enter handler does not call ValidateProperties.
            dialog.DialogKeyDown += (_, e) =>
            {
                if ((e.Key == Keys.Enter || e.Key == Keys.KeypadEnter) && SelectedSongIndices().Length == 0)
                    e.Handled = true;
            };
#endif
            dialog.Properties.Build();
        }

        private int[] SelectedSongIndices() => Enumerable.Range(0, songNames.Length)
            .Where(i => dialog.Properties.GetPropertyValue<bool>(0, i, 0)).ToArray();

        public void ShowDialogAsync(FamiStudioWindow parent, Action<Project> action)
        {
            if (dialog == null)
            {
                action(null);
                return;
            }

            dialog.ShowDialogAsync(r =>
            {
                if (r != DialogResult.OK)
                {
                    action(null);
                    return;
                }

                var songIndices         = SelectedSongIndices();
                var durations           = songIndices.Select(i => dialog.Properties.GetPropertyValue<int>(0, i, 2)).ToArray();
                var patternLen          = dialog.Properties.GetPropertyValue<int>(1);
                var startFrame          = dialog.Properties.GetPropertyValue<int>(2);
                var tuning              = dialog.Properties.GetPropertyValue<int>(3);
                var removeIntro         = dialog.Properties.GetPropertyValue<bool>(4);
                var reverseDpcmBits     = dialog.Properties.GetPropertyValue<bool>(5);
                var preserveDpcmPadding = dialog.Properties.GetPropertyValue<bool>(6);
                var importDmcValues     = dialog.Properties.GetPropertyValue<bool>(7);

                Project project;
#if !FAMISTUDIO_MOBILE
                var progress = songIndices.Length > 0 ? new ImportProgress(parent, NsfImportTitle, ImportingLabel) : null;
                var previousLog = Log.LogOutput;
                if (progress != null) Log.LogOutput = progress;
                try
                {
#endif
                    project = new NsfFile().LoadSongs(filename, songIndices, durations, patternLen, startFrame, removeIntro, reverseDpcmBits, preserveDpcmPadding, importDmcValues, tuning);
#if !FAMISTUDIO_MOBILE
                }
                finally
                {
                    Log.LogOutput = previousLog;
                    progress?.Close();
                }
#endif
                action(project);
            });
        }

#if !FAMISTUDIO_MOBILE
        // Pump desktop events while capturing so large multi-track imports remain cancellable.
        private sealed class ImportProgress : ILogOutput
        {
            private readonly PropertyDialog progress;
            private double lastEventLoop;

            public ImportProgress(FamiStudioWindow parent, string title, string label)
            {
                progress = new PropertyDialog(parent, title, 440, false);
                progress.Properties.AddLabel(null, label, true);
                progress.Properties.AddProgressBar(null);
                progress.Properties.Build();
                progress.ShowDialogNonModal();
            }

            public bool AbortOperation => progress.DialogResult != DialogResult.None;
            public void LogMessage(string message) { }
            public void ReportProgress(float fraction)
            {
                if (AbortOperation) return;
                progress.Properties.SetPropertyValue(1, fraction);
                var now = Platform.TimeSeconds();
                if (now - lastEventLoop >= 1.0 / 30.0)
                {
                    progress.ParentWindow.RunEventLoop();
                    lastEventLoop = now;
                }
            }
            public void Close()
            {
                if (!AbortOperation) progress.Close(DialogResult.OK);
            }
        }
#endif
    }
}
