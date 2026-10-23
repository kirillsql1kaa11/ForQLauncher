using Avalonia.Controls;
using Avalonia.Interactivity;
using ForQ.Core;

namespace ForQ.App;

public partial class ModDependentsWindow : Window
{
    public ModDependentsWindow()
    {
        InitializeComponent();
    }

    public ModDependentsWindow(string title, IReadOnlyList<ModDependent> dependents) : this()
    {
        var enabled = dependents.Count(item => item.Enabled);
        SummaryText.Text = $"«{title}» · зависимых модов: {dependents.Count} · включено: {enabled}";
        DependentsList.ItemsSource = dependents;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnDeleteAnyway(object? sender, RoutedEventArgs e) => Close(true);
}
