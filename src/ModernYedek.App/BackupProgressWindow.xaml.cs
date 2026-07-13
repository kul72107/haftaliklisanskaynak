using System.ComponentModel;
using System.Globalization;
using System.Windows;
using ModernYedek.Core.Models;

namespace ModernYedek.App;

public partial class BackupProgressWindow : Window
{
    private bool _allowClose;

    public BackupProgressWindow()
    {
        InitializeComponent();
    }

    public void UpdateProgress(BackupProgress progress)
    {
        StageText.Text = string.IsNullOrWhiteSpace(progress.Stage)
            ? "Yedekleme suruyor"
            : progress.Stage;
        DetailText.Text = string.IsNullOrWhiteSpace(progress.Message)
            ? StageText.Text
            : progress.Message;

        ProgressValueBar.IsIndeterminate = progress.IsIndeterminate;
        if (progress.IsIndeterminate)
        {
            PercentText.Text = "...";
        }
        else
        {
            var value = Math.Clamp(progress.Percent, 0, 100);
            ProgressValueBar.Value = value;
            PercentText.Text = value.ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        if (progress.TotalFiles > 0)
        {
            FileCountText.Visibility = Visibility.Visible;
            FileCountText.Text = $"{progress.FilesProcessed:N0} / {progress.TotalFiles:N0} dosya";
        }
        else
        {
            FileCountText.Visibility = Visibility.Collapsed;
            FileCountText.Text = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(progress.CurrentFile))
        {
            CurrentFileText.Visibility = Visibility.Visible;
            CurrentFileText.Text = progress.CurrentFile;
        }
        else if (!string.IsNullOrWhiteSpace(progress.TargetPath))
        {
            CurrentFileText.Visibility = Visibility.Visible;
            CurrentFileText.Text = progress.TargetPath;
        }
        else
        {
            CurrentFileText.Visibility = Visibility.Collapsed;
            CurrentFileText.Text = string.Empty;
        }
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }
}
