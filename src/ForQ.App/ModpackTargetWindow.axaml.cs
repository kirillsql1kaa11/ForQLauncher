using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ForQ.App;

public partial class ModpackTargetWindow : Window
{
    public ModpackTargetWindow() : this("Сборка", "Профиль")
    {
    }

    public ModpackTargetWindow(string packName, string instanceName)
    {
        InitializeComponent();
        PackText.Text = packName;
        InstanceText.Text = instanceName;
    }

    private void OnExisting(object? sender, RoutedEventArgs e) => Close(true);

    private void OnNew(object? sender, RoutedEventArgs e) => Close(false);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
