using Avalonia.Controls;
using Avalonia.Interactivity;
using ForQ.Core;

namespace ForQ.App;

public partial class ModConflictsWindow : Window
{
    public ModConflictsWindow()
    {
        InitializeComponent();
    }

    public ModConflictsWindow(string profileName, ModConflictReport report) : this()
    {
        SummaryText.Text = report.ModCount == 0
            ? $"Профиль «{profileName}» · включённых модов нет"
            : report.HasConflicts
                ? $"Профиль «{profileName}» · проверено модов: {report.ModCount} · найдено проблем: {report.Conflicts.Count}"
                : $"Профиль «{profileName}» · проверено модов: {report.ModCount} · проблем не найдено";

        ConflictList.ItemsSource = report.Conflicts;
        EmptyText.IsVisible = !report.HasConflicts;
        EmptyText.Text = report.ModCount == 0
            ? "Включённых модов нет — проверять нечего."
            : "Несовместимостей и дубликатов не найдено.";
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
