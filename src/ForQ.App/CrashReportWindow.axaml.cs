using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ForQ.Core;

namespace ForQ.App;

public partial class CrashReportWindow : Window
{
    private string? _logsFolder;

    public CrashReportWindow()
    {
        InitializeComponent();
    }

    public CrashReportWindow(string profileName, string instanceFolder, CrashAnalysis report) : this()
    {
        _logsFolder = Path.Combine(instanceFolder, "logs");

        SummaryText.Text = string.IsNullOrWhiteSpace(report.Summary)
            ? "Краш не найден"
            : report.Summary;
        SourceText.Text = string.IsNullOrWhiteSpace(report.Source)
            ? $"Профиль «{profileName}»"
            : $"Профиль «{profileName}» · источник: {report.Source}";

        SuspectPanel.IsVisible = !string.IsNullOrWhiteSpace(report.Suspect);
        SuspectText.Text = report.Suspect ?? "";
        SuspectFileText.Text = string.IsNullOrWhiteSpace(report.SuspectFile)
            ? ""
            : "Файл: " + report.SuspectFile;

        CauseText.Text = string.IsNullOrWhiteSpace(report.Cause)
            ? "Причина не определена."
            : report.Cause;
        RecommendationsList.ItemsSource = report.Recommendations;
        StackText.Text = string.IsNullOrWhiteSpace(report.StackTrace) ? "—" : report.StackTrace;
        DetailsText.Text = string.IsNullOrWhiteSpace(report.Details) ? "—" : report.Details;
    }

    private void OnOpenLogs(object? sender, RoutedEventArgs e)
    {
        var folder = _logsFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            StatusFallback();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
        }
    }

    private void StatusFallback() => SourceText.Text += " · папка логов не найдена";

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
