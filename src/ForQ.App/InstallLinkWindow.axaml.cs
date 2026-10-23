using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ForQ.App;

public partial class InstallLinkWindow : Window
{
    public InstallLinkWindow()
    {
        InitializeComponent();
        Opened += (_, _) => LinkBox.Focus();
    }

    private void OnInstall(object? sender, RoutedEventArgs e) => Submit();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        Submit();
    }

    private void Submit()
    {
        var url = LinkBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            ErrorText.Text = "Вставьте ссылку на проект или файл.";
            ErrorText.IsVisible = true;
            return;
        }

        Close(url);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
