using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ForQ.Core;

namespace ForQ.App;

public partial class InstanceSettingsWindow : Window
{
    private static readonly (int Width, int Height)[] ResolutionPresets =
    {
        (1920, 1080),
        (1600, 900),
        (1366, 768),
        (1280, 720),
        (1024, 768),
        (854, 480)
    };

    private static readonly Regex NickPattern = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);

    private const string CustomResolution = "Своя…";

    private readonly AppServices _services = AppServices.Current;
    private readonly Instance _instance;
    private bool _suppressResolution;
    private bool _suppressNick;
    private bool _initialized;

    public InstanceSettingsWindow() : this(new Instance())
    {
    }

    public InstanceSettingsWindow(Instance instance)
    {
        _instance = instance;
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        InstanceInfoText.Text = $"{_instance.MinecraftVersion} · {_instance.LoaderName}"
                                + (string.IsNullOrEmpty(_instance.LoaderVersion) ? "" : $" {_instance.LoaderVersion}");
        InstancePathText.Text = _instance.Folder;
        NameBox.Text = _instance.Name;

        if (_instance.Usernames.Count == 0)
            _instance.Usernames.Add(_instance.Username);
        if (!_instance.Usernames.Contains(_instance.Username, StringComparer.OrdinalIgnoreCase))
            _instance.Usernames.Add(_instance.Username);

        RefreshNicks(_instance.Username);

        var maxMemory = Math.Max(2048, Math.Min(32768, SystemInfo.TotalMemoryMb - 1024));
        MemorySlider.Maximum = maxMemory;
        MemorySlider.Value = Math.Clamp(_instance.MemoryMb, 1024, maxMemory);
        MemoryText.Text = (int)MemorySlider.Value + " МБ";
        MemoryHint.Text = $"Всего ОЗУ: {SystemInfo.TotalMemoryMb / 1024.0:F0} ГБ · рекомендуется {SystemInfo.RecommendedMemoryMb} МБ";

        BuildResolutionList();
        _initialized = true;
    }

    private void RefreshNicks(string? select)
    {
        _suppressNick = true;
        NickBox.ItemsSource = _instance.Usernames.ToList();
        NickBox.SelectedItem = select ?? _instance.Usernames.FirstOrDefault();
        _suppressNick = false;
    }

    private void OnNickSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!_suppressNick && NickBox.SelectedItem is string nick)
            HintText.Text = "";
    }

    private void OnNewNickKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        AddNick();
    }

    private void OnAddNick(object? sender, RoutedEventArgs e) => AddNick();

    private void AddNick()
    {
        var nick = (NewNickBox.Text ?? "").Trim();
        if (!NickPattern.IsMatch(nick))
        {
            HintText.Text = "Ник должен быть длиной 3–16 символов: латиница, цифры и подчёркивание.";
            return;
        }

        if (!_instance.Usernames.Contains(nick, StringComparer.OrdinalIgnoreCase))
            _instance.Usernames.Add(nick);

        NewNickBox.Text = "";
        HintText.Text = "";
        RefreshNicks(nick);
    }

    private void OnRemoveNick(object? sender, RoutedEventArgs e)
    {
        if (NickBox.SelectedItem is not string nick)
            return;

        if (_instance.Usernames.Count <= 1)
        {
            HintText.Text = "У профиля должен остаться хотя бы один ник.";
            return;
        }

        _instance.Usernames.RemoveAll(item => string.Equals(item, nick, StringComparison.OrdinalIgnoreCase));
        RefreshNicks(_instance.Usernames.FirstOrDefault());
        HintText.Text = "";
    }

    private void OnMemoryChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_initialized)
            return;

        MemoryText.Text = (int)e.NewValue + " МБ";
    }

    private void OnRecommendMemory(object? sender, RoutedEventArgs e)
    {
        var value = Math.Min(SystemInfo.RecommendedMemoryMb, (int)MemorySlider.Maximum);
        MemorySlider.Value = value;
        MemoryText.Text = value + " МБ";
    }

    private void BuildResolutionList()
    {
        var items = ResolutionPresets.Select(p => $"{p.Width} × {p.Height}").ToList();
        items.Add(CustomResolution);

        _suppressResolution = true;
        ResolutionBox.ItemsSource = items;

        var current = $"{_instance.Width} × {_instance.Height}";
        if (items.Contains(current))
        {
            ResolutionBox.SelectedItem = current;
            CustomResolutionPanel.IsVisible = false;
        }
        else
        {
            ResolutionBox.SelectedItem = CustomResolution;
            CustomResolutionPanel.IsVisible = true;
            WidthBox.Text = _instance.Width.ToString();
            HeightBox.Text = _instance.Height.ToString();
        }

        _suppressResolution = false;
        ResolutionText.Text = current;
    }

    private void OnResolutionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressResolution || ResolutionBox.SelectedItem is not string text)
            return;

        if (text == CustomResolution)
        {
            CustomResolutionPanel.IsVisible = true;
            WidthBox.Text = _instance.Width.ToString();
            HeightBox.Text = _instance.Height.ToString();
            return;
        }

        CustomResolutionPanel.IsVisible = false;
        var parts = text.Split('×', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height))
            ResolutionText.Text = $"{width} × {height}";
    }

    private void OnCustomResolutionKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (int.TryParse(WidthBox.Text, out var width) && int.TryParse(HeightBox.Text, out var height))
            ResolutionText.Text = $"{Math.Clamp(width, 640, 7680)} × {Math.Clamp(height, 480, 4320)}";
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        var nick = NickBox.SelectedItem as string ?? _instance.Username;
        if (!NickPattern.IsMatch(nick))
        {
            HintText.Text = "Выберите корректный ник.";
            return;
        }

        if (!_instance.Usernames.Contains(nick, StringComparer.OrdinalIgnoreCase))
            _instance.Usernames.Add(nick);

        _instance.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? _instance.Name : NameBox.Text.Trim();
        _instance.Username = nick;
        _instance.MemoryMb = (int)MemorySlider.Value;

        if (ResolutionBox.SelectedItem as string == CustomResolution)
        {
            if (int.TryParse(WidthBox.Text, out var width) && int.TryParse(HeightBox.Text, out var height))
            {
                _instance.Width = Math.Clamp(width, 640, 7680);
                _instance.Height = Math.Clamp(height, 480, 4320);
            }
        }
        else if (ResolutionBox.SelectedItem is string preset)
        {
            var parts = preset.Split('×', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height))
            {
                _instance.Width = width;
                _instance.Height = height;
            }
        }

        await _services.Instances.SaveAsync(_instance);
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
