using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ForQ.Core;

namespace ForQ.App;

public sealed class SearchItem
{
    public SearchItem(ModrinthProject project) => Project = project;

    public ModrinthProject Project { get; }
    public string Title => Project.Title;
    public string Description => Project.Description;
    public string Author => Project.Author ?? "неизвестно";
    public string Downloads => Project.Downloads.ToString("N0");
}

public partial class MainWindow : Window
{
    private readonly AppServices _services = AppServices.Current;
    private readonly List<Instance> _instances = new();
    private readonly StringBuilder _log = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Progress<DownloadState> contentProgress;
    private Instance? _current;
    private Process? _game;
    private ContentKind _kind = ContentKind.Mod;
    private bool _suppressMemoryEvent;

    public MainWindow()
    {
        InitializeComponent();
        contentProgress = new Progress<DownloadState>(state =>
        {
            var done = state.Done / 1048576.0;
            var total = state.Total / 1048576.0;
            StatusText.Text = $"Загрузка {state.Name}: {done:F1} МБ из {total:F1} МБ";
        });
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _services.Settings.Username = string.IsNullOrWhiteSpace(_services.Settings.Username)
            ? "Player"
            : _services.Settings.Username;

        UsernameBox.Text = _services.Settings.Username;
        SettingsUsernameBox.Text = _services.Settings.Username;
        TelemetryCheck.IsChecked = _services.Settings.BlockTelemetry;
        JavaPathBox.Text = _services.Settings.JavaPath ?? "";
        RootPathBox.Text = _services.Root;
        ThreadsBox.Text = _services.Settings.DownloadThreads.ToString();

        _suppressMemoryEvent = true;
        MemorySlider.Value = _services.Settings.MemoryMb;
        _suppressMemoryEvent = false;
        MemoryText.Text = _services.Settings.MemoryMb + " МБ";

        _ = LoadInstancesAsync();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _cts.Cancel();
        if (_game is { HasExited: false })
        {
            try
            {
                _game.Kill(true);
            }
            catch (Exception)
            {
            }
        }

        _services.SaveSettings();
        _services.Dispose();
    }

    private async Task LoadInstancesAsync()
    {
        var loaded = await _services.Instances.LoadAsync();
        _instances.Clear();
        _instances.AddRange(loaded);
        InstanceList.ItemsSource = null;
        InstanceList.ItemsSource = _instances;

        if (_instances.Count == 0)
        {
            _current = null;
            TitleText.Text = "Нет профиля";
            SubtitleText.Text = "Создайте профиль, чтобы начать";
            ProfileInfoText.Text = "—";
            PlayButton.IsEnabled = false;
            return;
        }

        var target = _instances.FirstOrDefault(i => i.Id == _services.Settings.LastInstanceId) ?? _instances[0];
        InstanceList.SelectedItem = target;
    }

    private void OnInstanceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (InstanceList.SelectedItem is not Instance instance)
            return;

        _current = instance;
        _services.Settings.LastInstanceId = instance.Id;

        TitleText.Text = instance.Name;
        SubtitleText.Text = $"{instance.MinecraftVersion} · {instance.LoaderName}"
                            + (string.IsNullOrEmpty(instance.LoaderVersion) ? "" : $" {instance.LoaderVersion}");
        ProfileInfoText.Text = instance.ProfileId;
        PlayButton.IsEnabled = true;

        UsernameBox.Text = instance.Username;
        _suppressMemoryEvent = true;
        MemorySlider.Value = instance.MemoryMb;
        _suppressMemoryEvent = false;
        MemoryText.Text = instance.MemoryMb + " МБ";
        ResolutionText.Text = $"{instance.Width} × {instance.Height}";

        RefreshInstalled();
    }

    private async void OnNewInstance(object? sender, RoutedEventArgs e)
    {
        var dialog = new NewInstanceWindow();
        var draft = await dialog.ShowDialog<InstanceDraft>(this);
        if (draft is null)
            return;

        ShowOverlay("Создание профиля", "Подготовка...");

        var progress = new Progress<DownloadState>(state =>
        {
            var done = state.Done / 1048576.0;
            var total = state.Total / 1048576.0;
            UpdateOverlayProgress(total > 0 ? $"{done:F0} МБ из {total:F0} МБ" : $"{done:F0} МБ");
        });

        try
        {
            var instance = await _services.Instances.CreateAsync(
                draft.Name, draft.MinecraftVersion, draft.Loader, draft.LoaderVersion);

            UpdateOverlay("Скачивание игры...");
            await _services.Installer.InstallVanillaAsync(instance, UpdateOverlay, _cts.Token, progress);

            if (draft.Loader != LoaderKind.Vanilla)
            {
                UpdateOverlay($"Установка {draft.Loader}...");
                await _services.Installer.InstallLoaderAsync(instance, UpdateOverlay, _cts.Token);
                await _services.Instances.SaveAsync(instance);
            }

            await LoadInstancesAsync();
            InstanceList.SelectedItem = _instances.FirstOrDefault(i => i.Id == instance.Id);
            AppendLog($"Профиль {instance.Name} готов.");
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка установки: " + ex.Message);
        }
        finally
        {
            HideOverlay();
        }
    }

    private async void OnPlay(object? sender, RoutedEventArgs e)
    {
        if (_current is null)
            return;
        if (_game is { HasExited: false })
            return;

        _current.Username = string.IsNullOrWhiteSpace(UsernameBox.Text) ? "Player" : UsernameBox.Text.Trim();
        _current.MemoryMb = (int)MemorySlider.Value;
        await _services.Instances.SaveAsync(_current);

        try
        {
            AppendLog($"Запуск {_current.Name}...");
            var plan = await _services.Launcher.BuildPlanAsync(_current, _services.Settings, AppendLog, _cts.Token);
            _game = await _services.Launcher.StartAsync(plan, AppendLog, _cts.Token);
            _game.EnableRaisingEvents = true;
            _game.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                AppendLog("Игра завершена.");
                PlayButton.IsEnabled = true;
            });
            PlayButton.IsEnabled = false;
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка запуска: " + ex.Message);
            PlayButton.IsEnabled = true;
        }
    }

    private void OnStopGame(object? sender, RoutedEventArgs e)
    {
        if (_game is { HasExited: false })
        {
            try
            {
                _game.Kill(true);
                AppendLog("Игра остановлена.");
            }
            catch (Exception ex)
            {
                AppendLog("Не удалось остановить: " + ex.Message);
            }
        }
    }

    private void OnClearLog(object? sender, RoutedEventArgs e)
    {
        _log.Clear();
        LogBox.Text = "";
    }

    private void OnMemoryChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressMemoryEvent)
            return;

        var value = (int)e.NewValue;
        MemoryText.Text = value + " МБ";
        if (_current is not null)
            _current.MemoryMb = value;
    }

    private void OnKindChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        _kind = button == SegMods ? ContentKind.Mod
            : button == SegPacks ? ContentKind.ResourcePack
            : ContentKind.Shader;

        SetActive(SegMods, button == SegMods);
        SetActive(SegPacks, button == SegPacks);
        SetActive(SegShaders, button == SegShaders);
    }

    private static void SetActive(Button button, bool active)
    {
        if (active)
            button.Classes.Add("active");
        else
            button.Classes.Remove("active");
    }

    private async void OnSearch(object? sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            StatusText.Text = "Сначала выберите профиль";
            return;
        }

        ResultsList.ItemsSource = null;

        if (_kind == ContentKind.Mod && _current.Loader == LoaderKind.Vanilla)
        {
            StatusText.Text = "Для модов нужен профиль с загрузчиком: Fabric, Quilt или NeoForge";
            return;
        }

        StatusText.Text = "Поиск...";

        try
        {
            var type = _kind switch
            {
                ContentKind.Mod => "mod",
                ContentKind.ResourcePack => "resourcepack",
                _ => "shader"
            };

            var response = await _services.Modrinth.SearchAsync(
                SearchBox.Text ?? "", type, _current.MinecraftVersion, LoaderTag(), _cts.Token);

            ResultsList.ItemsSource = response.Hits.Select(p => new SearchItem(p)).ToList();
            StatusText.Text = $"Найдено проектов: {response.TotalHits}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка поиска: " + ex.Message;
        }
    }

    private async void OnInstallContent(object? sender, RoutedEventArgs e)
    {
        if (_current is null || sender is not Button { DataContext: SearchItem item })
            return;

        StatusText.Text = $"Установка {item.Title}...";

        try
        {
            var version = await _services.Modrinth.PickVersionAsync(
                item.Project.Key, _current.MinecraftVersion, LoaderTag(), item.Project.ProjectType, _cts.Token);

            if (version is null)
            {
                StatusText.Text = $"Нет версии под {_current.MinecraftVersion}";
                return;
            }

            await _services.Content.InstallWithDependenciesAsync(
                _current, version, _kind, _services.Modrinth, _cts.Token, contentProgress);

            RefreshInstalled();
            StatusText.Text = $"Установлено: {item.Title}"
                              + (_game is { HasExited: false } ? " (применится после перезапуска игры)" : "");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка установки: " + ex.Message;
        }
    }

    private void OnInstalledToggle(object? sender, RoutedEventArgs e)
    {
        if (_current is null || sender is not CheckBox { DataContext: InstalledContent content })
            return;

        try
        {
            _services.Content.SetEnabled(_current, content, content.Enabled);
            RefreshInstalled();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось применить: " + ex.Message;
        }
    }

    private void RefreshInstalled()
    {
        if (_current is null)
        {
            InstalledList.ItemsSource = null;
            return;
        }

        var items = _services.Content.ListInstalled(_current)
            .OrderBy(i => i.Kind)
            .ThenBy(i => i.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        InstalledList.ItemsSource = items;
    }

    private string? LoaderTag() => _current?.Loader switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        LoaderKind.NeoForge => "neoforge",
        _ => null
    };

    private void OnSaveSettings(object? sender, RoutedEventArgs e)
    {
        _services.Settings.Username = string.IsNullOrWhiteSpace(SettingsUsernameBox.Text)
            ? "Player"
            : SettingsUsernameBox.Text.Trim();
        _services.Settings.BlockTelemetry = TelemetryCheck.IsChecked ?? true;
        _services.Settings.JavaPath = string.IsNullOrWhiteSpace(JavaPathBox.Text) ? null : JavaPathBox.Text.Trim();

        if (int.TryParse(ThreadsBox.Text, out var threads) && threads is >= 1 and <= 16)
            _services.Settings.DownloadThreads = threads;

        _services.SaveSettings();
        UsernameBox.Text = _services.Settings.Username;
        StatusText.Text = "Настройки сохранены";
    }

    private void AppendLog(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _log.AppendLine(text);
            if (_log.Length > 300_000)
                _log.Remove(0, 150_000);

            LogBox.Text = _log.ToString();
            LogBox.CaretIndex = LogBox.Text.Length;
        });
    }

    private void ShowOverlay(string title, string status)
    {
        OverlayTitle.Text = title;
        OverlayStatus.Text = status;
        Overlay.IsVisible = true;
    }

    private void UpdateOverlay(string status) => Dispatcher.UIThread.Post(() => OverlayStatus.Text = status);

    private void UpdateOverlayProgress(string text) => Dispatcher.UIThread.Post(() => OverlayProgress.Text = text);

    private void HideOverlay() => Dispatcher.UIThread.Post(() =>
    {
        Overlay.IsVisible = false;
        OverlayProgress.Text = "";
    });
}
