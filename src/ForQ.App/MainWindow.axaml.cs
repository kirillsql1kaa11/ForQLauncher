using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ForQ.Core;

namespace ForQ.App;

public partial class MainWindow : Window
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

    private const string CustomResolution = "Своя…";
    private const int SearchPageSize = 100;
    private const int SearchMaxItems = 500;

    private readonly AppServices _services = AppServices.Current;
    private readonly List<Instance> _instances = new();
    private readonly StringBuilder _log = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Progress<DownloadState> contentProgress;
    private readonly Dictionary<string, ContentUpdate> _updates = new(StringComparer.OrdinalIgnoreCase);
    private Instance? _current;
    private Process? _game;
    private ContentKind _kind = ContentKind.Mod;
    private bool _suppressMemoryEvent;
    private bool _suppressResolutionEvent;
    private int _searchGeneration;
    private int _modpackGeneration;
    private bool _resultsLoaded;
    private bool _modpacksLoaded;
    private string _resultsInstanceId = "";
    private ContentKind _resultsKind = ContentKind.Mod;
    private string _resultsQuery = "";
    private ObservableCollection<SearchItem> _resultItems = new();
    private ObservableCollection<SearchItem> _modpackItems = new();
    private CancellationTokenSource? _contentTranslationCts;
    private CancellationTokenSource? _modpackTranslationCts;
    private int _searchOffset;
    private int _searchTotal;
    private int _modpackOffset;
    private int _modpackTotal;
    private GitHubAsset? _pendingAsset;
    private DownloadState? _lastDownload;
    private bool _ready;
    private bool _stopRequested;

    public MainWindow()
    {
        InitializeComponent();
        ResultsList.AddHandler(KeyDownEvent, OnResultKey, RoutingStrategies.Tunnel);
        ModpackList.AddHandler(KeyDownEvent, OnResultKey, RoutingStrategies.Tunnel);
        contentProgress = new Progress<DownloadState>(state =>
        {
            _lastDownload = state;
            var text = FormatDownload(state);
            StatusText.Text = text;
            UpdateOverlayProgress(text);
            if (state.Total > 0)
                UpdateOverlayBar(true, state.Total, state.Done);
            else
                UpdateOverlayBar(false, 0);
        });
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _services.Settings.Username = string.IsNullOrWhiteSpace(_services.Settings.Username)
            ? "Player"
            : _services.Settings.Username;

        VersionText.Text = "Версия " + AppServices.Version;
        UsernameBox.Text = _services.Settings.Username;
        SettingsUsernameBox.Text = _services.Settings.Username;
        TelemetryCheck.IsChecked = _services.Settings.BlockTelemetry;
        JavaPathBox.Text = _services.Settings.JavaPath ?? "";
        RootPathBox.Text = _services.Root;
        ThreadsBox.Text = _services.Settings.DownloadThreads.ToString();
        SpeedLimitBox.Text = _services.Settings.DownloadSpeedLimitKbps.ToString();
        _services.Downloads.SpeedLimitBytesPerSecond = _services.Settings.DownloadSpeedLimitKbps * 1024L;
        AutoUpdateCheck.IsChecked = _services.Settings.AutoCheckUpdates;

        _suppressMemoryEvent = true;
        MemorySlider.Value = _services.Settings.MemoryMb;
        _suppressMemoryEvent = false;
        MemoryText.Text = _services.Settings.MemoryMb + " МБ";
        MemoryHint.Text = $"Всего ОЗУ: {SystemInfo.TotalMemoryMb / 1024.0:F0} ГБ · рекомендуется {SystemInfo.RecommendedMemoryMb} МБ";

        UpdateResolutionBox();

        _ready = true;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await LoadInstancesAsync();

        if (_services.Settings.AutoCheckUpdates)
            await CheckLauncherUpdateAsync(true);
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
        if (target.Id != _services.Settings.LastInstanceId)
        {
            _services.Settings.LastInstanceId = target.Id;
            _services.SaveSettings();
        }

        InstanceList.SelectedItem = target;
    }

    private void OnInstanceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (InstanceList.SelectedItem is not Instance instance)
            return;

        _current = instance;
        _services.Settings.LastInstanceId = instance.Id;
        _updates.Clear();
        _resultsLoaded = false;
        _resultItems.Clear();
        ResultsList.ItemsSource = null;
        UpdateBatchButton();

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
        UpdateResolutionBox();

        RefreshInstalled();

        if (Tabs.SelectedIndex == 1)
            _ = EnsureContentSearchAsync();
    }

    private async void OnNewInstance(object? sender, RoutedEventArgs e)
    {
        var dialog = new NewInstanceWindow();
        var draft = await dialog.ShowDialog<InstanceDraft>(this);
        if (draft is null)
            return;

        ShowDownloadOverlay("Создание профиля", "Подготовка...");

        try
        {
            var instance = await _services.Instances.CreateAsync(
                draft.Name, draft.MinecraftVersion, draft.Loader, draft.LoaderVersion);

            instance.MemoryMb = SystemInfo.RecommendedMemoryMb;
            await _services.Instances.SaveAsync(instance);

            UpdateOverlay("Скачивание игры...");
            await _services.Installer.InstallVanillaAsync(instance, UpdateOverlay, _cts.Token, contentProgress);

            if (draft.Loader != LoaderKind.Vanilla)
            {
                UpdateOverlay($"Установка {draft.Loader}...");
                await _services.Installer.InstallLoaderAsync(instance, UpdateOverlay, _cts.Token);
                await _services.Instances.SaveAsync(instance);
            }

            await LoadInstancesAsync();
            InstanceList.SelectedItem = _instances.FirstOrDefault(i => i.Id == instance.Id);
            AppendLog($"Профиль {instance.Name} готов, язык игры — русский.");
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
            var instance = _current;
            AppendLog($"Запуск {instance.Name}...");
            _stopRequested = false;
            var plan = await _services.Launcher.BuildPlanAsync(instance, _services.Settings, AppendLog, _cts.Token);
            var game = await _services.Launcher.StartAsync(plan, AppendLog, _cts.Token);
            _game = game;
            game.EnableRaisingEvents = true;
            game.Exited += (_, _) => Dispatcher.UIThread.Post(() => _ = HandleGameExitAsync(game, instance));
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
                _stopRequested = true;
                _game.Kill(true);
                AppendLog("Игра остановлена.");
            }
            catch (Exception ex)
            {
                AppendLog("Не удалось остановить: " + ex.Message);
            }
        }
    }

    private async Task HandleGameExitAsync(Process game, Instance instance)
    {
        AppendLog("Игра завершена.");
        PlayButton.IsEnabled = true;

        if (_stopRequested || game.ExitCode == 0)
            return;

        AppendLog($"Игра завершилась с кодом {game.ExitCode}. Разбираю логи...");
        await AnalyzeCrashAsync(true, instance);
    }

    private async void OnAnalyzeCrash(object? sender, RoutedEventArgs e) => await AnalyzeCrashAsync(false);

    private async Task AnalyzeCrashAsync(bool automatic, Instance? target = null)
    {
        var instance = target ?? _current;
        if (instance is null)
        {
            StatusText.Text = "Сначала выберите профиль";
            return;
        }

        StatusText.Text = automatic ? "Игра упала, разбираю лог..." : "Разбираю логи...";

        try
        {
            var report = await Task.Run(() => _services.Crashes.Analyze(instance, _cts.Token), _cts.Token);
            if (!automatic && _current != instance)
                return;

            if (!report.Found)
            {
                StatusText.Text = "Крашей не найдено. В логах нет ошибок.";
                if (!automatic)
                    await new CrashReportWindow(instance.Name, instance.Folder, report).ShowDialog(this);
                return;
            }

            StatusText.Text = string.IsNullOrWhiteSpace(report.Suspect)
                ? "Краш разобран: виновника определить не удалось"
                : $"Краш разобран: вероятный виновник — {report.Suspect}";
            await new CrashReportWindow(instance.Name, instance.Folder, report).ShowDialog(this);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось разобрать краш: " + ex.Message;
        }
    }

    private void OnClearLog(object? sender, RoutedEventArgs e)
    {
        _log.Clear();
        LogBox.Text = "";
    }

    private void UpdateResolutionBox()
    {
        var items = ResolutionPresets.Select(p => $"{p.Width} × {p.Height}").ToList();
        items.Add(CustomResolution);

        _suppressResolutionEvent = true;
        ResolutionBox.ItemsSource = items;

        var current = _current is null ? "1280 × 720" : $"{_current.Width} × {_current.Height}";
        if (items.Contains(current))
        {
            ResolutionBox.SelectedItem = current;
            CustomResolutionPanel.IsVisible = false;
        }
        else
        {
            ResolutionBox.SelectedItem = CustomResolution;
            CustomResolutionPanel.IsVisible = true;
            WidthBox.Text = _current?.Width.ToString() ?? "1280";
            HeightBox.Text = _current?.Height.ToString() ?? "720";
        }

        _suppressResolutionEvent = false;
        ResolutionText.Text = current;
    }

    private void OnResolutionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressResolutionEvent || ResolutionBox.SelectedItem is not string text)
            return;

        if (text == CustomResolution)
        {
            CustomResolutionPanel.IsVisible = true;
            WidthBox.Text = _current?.Width.ToString() ?? "1280";
            HeightBox.Text = _current?.Height.ToString() ?? "720";
            return;
        }

        CustomResolutionPanel.IsVisible = false;
        var parts = text.Split('×', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height))
            return;

        ApplyResolution(width, height);
    }

    private void OnCustomResolutionKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (int.TryParse(WidthBox.Text, out var width) && int.TryParse(HeightBox.Text, out var height))
            ApplyResolution(width, height);
    }

    private void ApplyResolution(int width, int height)
    {
        width = Math.Clamp(width, 640, 7680);
        height = Math.Clamp(height, 480, 4320);

        if (_current is not null)
        {
            _current.Width = width;
            _current.Height = height;
        }

        ResolutionText.Text = $"{width} × {height}";
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

    private void OnRecommendMemory(object? sender, RoutedEventArgs e)
    {
        var value = SystemInfo.RecommendedMemoryMb;

        _suppressMemoryEvent = true;
        MemorySlider.Value = value;
        _suppressMemoryEvent = false;

        MemoryText.Text = value + " МБ";
        if (_current is not null)
            _current.MemoryMb = value;

        StatusText.Text = $"Подобрано {value} МБ из {SystemInfo.TotalMemoryMb / 1024.0:F0} ГБ";
    }

    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
            return;

        if (Tabs.SelectedIndex == 1)
        {
            RefreshInstalled();
            _ = EnsureContentSearchAsync();
        }
        else if (Tabs.SelectedIndex == 2)
        {
            _ = EnsureModpackSearchAsync();
        }
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

        _resultsLoaded = false;
        BatchInstallButton.IsVisible = false;
        RefreshInstalled();
        _ = EnsureContentSearchAsync();
    }

    private static void SetActive(Button button, bool active)
    {
        if (active)
            button.Classes.Add("active");
        else
            button.Classes.Remove("active");
    }

    private async Task EnsureContentSearchAsync()
    {
        if (_current is null)
        {
            SetFilter("Сначала выберите профиль", false);
            return;
        }

        if (_resultsLoaded
            && _resultsInstanceId == _current.Id
            && _resultsKind == _kind
            && _resultsQuery == (SearchBox.Text ?? ""))
            return;

        await RunSearchAsync();
    }

    private async void OnSearch(object? sender, RoutedEventArgs e) => await RunSearchAsync();

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        _ = RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        if (_current is null)
        {
            SetFilter("Сначала выберите профиль", false);
            StatusText.Text = "Сначала выберите профиль";
            return;
        }

        _contentTranslationCts?.Cancel();
        var translationCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _contentTranslationCts = translationCts;

        var instance = _current;
        var kind = _kind;
        var query = SearchBox.Text ?? "";
        var generation = ++_searchGeneration;

        SearchMoreButton.IsVisible = false;

        if (kind == ContentKind.Mod && instance.Loader == LoaderKind.Vanilla)
        {
            ResultsList.ItemsSource = null;
            _resultItems = new ObservableCollection<SearchItem>();
            _searchOffset = 0;
            _searchTotal = 0;
            SearchCountText.Text = "";
            UpdateBatchButton();
            SetFilter("Для модов нужен профиль с загрузчиком: Fabric, Quilt или NeoForge", true);
            StatusText.Text = "Ванильный профиль: моды не загрузятся";
            return;
        }

        SetFilter($"Совместимо: {instance.MinecraftVersion} · {instance.LoaderName}", false);
        StatusText.Text = "Поиск...";

        try
        {
            var response = await _services.Modrinth.SearchAsync(
                query, TypeOf(kind), instance.MinecraftVersion, LoaderTag(), _cts.Token, SearchPageSize, 0);

            if (generation != _searchGeneration)
                return;

            var installedIds = _services.Content.InstalledProjectIds(instance, kind);
            _resultItems = new ObservableCollection<SearchItem>();
            foreach (var project in response.Hits)
            {
                var item = new SearchItem(project, TypeLabel(kind))
                {
                    Installed = installedIds.Contains(project.Key)
                };
                _resultItems.Add(item);
                _ = item.LoadIconAsync(_cts.Token);
            }

            ResultsList.ItemsSource = _resultItems;
            _searchOffset = response.Hits.Count;
            _searchTotal = (int)response.TotalHits;
            UpdateSearchMore();
            UpdateBatchButton();
            _ = TranslateDescriptionsAsync(_resultItems, () => generation == _searchGeneration, translationCts.Token);

            StatusText.Text = $"Найдено: {response.TotalHits}";

            _resultsLoaded = true;
            _resultsInstanceId = instance.Id;
            _resultsKind = kind;
            _resultsQuery = query;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation == _searchGeneration)
                StatusText.Text = "Ошибка поиска: " + ex.Message;
        }
    }

    private void UpdateSearchMore()
    {
        var shown = _resultItems.Count;
        var limit = Math.Min(_searchTotal, SearchMaxItems);
        SearchMoreButton.IsVisible = shown < limit;
        SearchMoreButton.Content = $"Показать ещё · {shown} из {limit}";

        if (_searchTotal > SearchMaxItems && shown >= SearchMaxItems)
            SearchCountText.Text = $"{_searchTotal} проектов · первые {SearchMaxItems}";
        else if (_searchTotal > shown)
            SearchCountText.Text = $"{_searchTotal} проектов · показано {shown}";
        else
            SearchCountText.Text = $"{_searchTotal} проектов";
    }

    private async void OnLoadMoreContent(object? sender, RoutedEventArgs e) => await LoadMoreContentAsync();

    private async Task LoadMoreContentAsync()
    {
        if (_current is null)
            return;

        var limit = Math.Min(_searchTotal, SearchMaxItems);
        if (_resultItems.Count >= limit)
        {
            UpdateSearchMore();
            return;
        }

        var instance = _current;
        var kind = _resultsKind;
        var generation = _searchGeneration;
        var translationToken = _contentTranslationCts?.Token ?? _cts.Token;
        SearchMoreButton.IsEnabled = false;
        StatusText.Text = "Загружаю ещё...";

        try
        {
            var response = await _services.Modrinth.SearchAsync(
                _resultsQuery, TypeOf(kind), instance.MinecraftVersion, LoaderTag(), _cts.Token,
                SearchPageSize, _searchOffset);

            if (generation != _searchGeneration)
                return;

            var installedIds = _services.Content.InstalledProjectIds(instance, kind);
            var added = new List<SearchItem>();
            foreach (var project in response.Hits)
            {
                if (_resultItems.Count >= SearchMaxItems)
                    break;
                if (_resultItems.Any(existing => existing.Project.Key == project.Key))
                    continue;

                var item = new SearchItem(project, TypeLabel(kind))
                {
                    Installed = installedIds.Contains(project.Key)
                };
                _resultItems.Add(item);
                added.Add(item);
                _ = item.LoadIconAsync(_cts.Token);
            }

            _ = TranslateDescriptionsAsync(added, () => generation == _searchGeneration, translationToken);
            _searchOffset += response.Hits.Count;
            _searchTotal = (int)response.TotalHits;
            UpdateSearchMore();
            UpdateBatchButton();
            StatusText.Text = $"Показано {_resultItems.Count} из {_searchTotal}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation == _searchGeneration)
                StatusText.Text = "Ошибка поиска: " + ex.Message;
        }
        finally
        {
            SearchMoreButton.IsEnabled = true;
        }
    }

    private async Task TranslateDescriptionsAsync(
        IReadOnlyList<SearchItem> items, Func<bool> isCurrent, CancellationToken ct)
    {
        if (items.Count == 0)
            return;

        try
        {
            var progress = new Progress<(int Index, string Text)>(entry =>
            {
                if (!isCurrent() || entry.Index < 0 || entry.Index >= items.Count)
                    return;
                items[entry.Index].SetDescription(entry.Text);
            });

            var descriptions = items.Select(item => item.Project.Description).ToList();
            var translated = await _services.Translation.ToRussianBatchAsync(descriptions, ct, progress);
            if (ct.IsCancellationRequested)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (!isCurrent())
                    return;

                for (var index = 0; index < items.Count && index < translated.Count; index++)
                    items[index].SetDescription(translated[index]);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private void SetFilter(string text, bool warn)
    {
        FilterText.Text = text;

        if (warn)
        {
            if (!FilterText.Classes.Contains("danger"))
                FilterText.Classes.Add("danger");
        }
        else
        {
            FilterText.Classes.Remove("danger");
        }
    }

    private void OnInstalledSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready)
            return;

        RefreshInstalled();
    }

    private async void OnResultClick(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;

        if (sender is not Control { DataContext: SearchItem item } || IsInsideButton(e.Source))
            return;

        e.Handled = true;
        await OpenDetailsAsync(item);
    }

    private async void OnResultKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (sender is not ListBox { SelectedItem: SearchItem item } || IsInsideButton(e.Source))
            return;

        e.Handled = true;
        await OpenDetailsAsync(item);
    }

    private async Task OpenDetailsAsync(SearchItem item)
    {
        var dialog = new ContentDetailsWindow(item.Project.Key, item.Project.ProjectType, _current);
        var installed = await dialog.ShowDialog<bool>(this);

        if (!installed)
            return;

        if (item.Project.ProjectType == "modpack")
        {
            await LoadInstancesAsync();
            StatusText.Text = "Сборка установлена";
            return;
        }

        item.Installed = true;
        UpdateSearchMarks();
        RefreshInstalled();
        StatusText.Text = $"Установлено: {item.Title}";

        foreach (var key in _updates.Where(pair => pair.Value.ProjectId == item.Project.Key).Select(pair => pair.Key).ToList())
            _updates.Remove(key);
    }

    private static bool IsInsideButton(object? source)
    {
        var current = source as Visual;
        while (current is not null)
        {
            if (current is Button or CheckBox)
                return true;
            current = current.GetVisualParent();
        }

        return false;
    }

    private void OnResultSelected(object? sender, RoutedEventArgs e) => UpdateBatchButton();

    private void UpdateBatchButton()
    {
        var count = _resultItems.Count(item => item.Selected && !item.Installed);
        BatchInstallButton.IsVisible = count > 0;
        BatchInstallButton.Content = count > 0 ? $"Установить выбранные ({count})" : "Установить выбранные";
    }

    private async void OnInstallContent(object? sender, RoutedEventArgs e)
    {
        if (_current is null || sender is not Button { DataContext: SearchItem item })
            return;

        var instance = _current;
        var kind = KindOf(item.Project.ProjectType);
        ShowDownloadOverlay("Установка", $"Установка «{item.Title}»...");

        try
        {
            var version = await _services.Modrinth.PickVersionAsync(
                item.Project.Key, instance.MinecraftVersion, LoaderTag(), item.Project.ProjectType, _cts.Token);

            if (version is null)
            {
                StatusText.Text = $"Нет версии под {instance.MinecraftVersion}";
                return;
            }

            var source = new ContentSource(item.Project.Key, item.Title, item.Project.IconUrl);
            await _services.Content.InstallWithDependenciesAsync(
                instance, version, kind, source, _services.Modrinth, _cts.Token, contentProgress);

            foreach (var key in _updates.Where(pair => pair.Value.ProjectId == item.Project.Key).Select(pair => pair.Key).ToList())
                _updates.Remove(key);

            item.Installed = true;
            item.Selected = false;
            UpdateSearchMarks();
            UpdateBatchButton();
            RefreshInstalled();
            StatusText.Text = $"Установлено: {item.Title} → {ContentService.FolderFor(kind)}"
                              + (_game is { HasExited: false } ? " (применится после перезапуска игры)" : "");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка установки: " + ex.Message;
        }
        finally
        {
            HideOverlay();
        }
    }

    private async void OnBatchInstall(object? sender, RoutedEventArgs e) => await BatchInstallAsync();

    private async Task BatchInstallAsync()
    {
        if (_current is null)
        {
            StatusText.Text = "Сначала выберите профиль";
            return;
        }

        var instance = _current;
        var selected = _resultItems.Where(item => item.Selected && !item.Installed).ToList();
        if (selected.Count == 0)
            return;

        ShowDownloadOverlay("Пакетная установка", $"Подготовка проектов: {selected.Count}");
        var installed = 0;
        var errors = new List<string>();

        try
        {
            foreach (var item in selected)
            {
                if (_cts.IsCancellationRequested)
                    break;

                UpdateOverlay($"Установка «{item.Title}»...");

                try
                {
                    var version = await _services.Modrinth.PickVersionAsync(
                        item.Project.Key, instance.MinecraftVersion, LoaderTag(), item.Project.ProjectType, _cts.Token);

                    if (version is null)
                    {
                        errors.Add($"{item.Title}: нет версии под {instance.MinecraftVersion}");
                        continue;
                    }

                    var kind = KindOf(item.Project.ProjectType);
                    var source = new ContentSource(item.Project.Key, item.Title, item.Project.IconUrl);
                    await _services.Content.InstallWithDependenciesAsync(
                        instance, version, kind, source, _services.Modrinth, _cts.Token, contentProgress);

                    item.Installed = true;
                    item.Selected = false;
                    installed++;

                    foreach (var key in _updates.Where(pair => pair.Value.ProjectId == item.Project.Key)
                                 .Select(pair => pair.Key).ToList())
                        _updates.Remove(key);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"{item.Title}: {ex.Message}");
                }
            }

            UpdateSearchMarks();
            UpdateBatchButton();
            RefreshInstalled();

            StatusText.Text = errors.Count == 0
                ? $"Пакетная установка завершена · проектов: {installed}"
                : $"Установлено: {installed}, ошибок: {errors.Count}";

            foreach (var error in errors)
                AppendLog("Пакетная установка: " + error);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            HideOverlay();
        }
    }

    private void OnInstalledToggle(object? sender, RoutedEventArgs e)
    {
        if (_current is null || sender is not CheckBox { DataContext: InstalledItem item } box)
            return;

        try
        {
            var enabled = box.IsChecked == true;
            _services.Content.SetEnabled(_current, item.Content, enabled);
            RefreshInstalled();
            StatusText.Text = enabled ? $"Включено: {item.Title}" : $"Отключено: {item.Title}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось применить: " + ex.Message;
            RefreshInstalled();
        }
    }

    private async void OnCheckUpdates(object? sender, RoutedEventArgs e) => await CheckContentUpdatesAsync();

    private async void OnCheckConflicts(object? sender, RoutedEventArgs e) => await CheckModConflictsAsync();

    private async Task CheckModConflictsAsync()
    {
        if (_current is null)
        {
            StatusText.Text = "Сначала выберите профиль";
            return;
        }

        var instance = _current;
        var previous = ConflictsButton.Content;
        ConflictsButton.IsEnabled = false;
        ConflictsButton.Content = "Проверяю...";
        StatusText.Text = "Проверяю конфликты модов...";

        try
        {
            var report = await Task.Run(() => _services.Conflicts.Check(instance, _cts.Token), _cts.Token);
            if (_current != instance)
                return;

            StatusText.Text = report.ModCount == 0
                ? "В профиле нет включённых модов"
                : report.HasConflicts
                    ? $"Найдено проблем: {report.Conflicts.Count}"
                    : $"Конфликтов не найдено · модов: {report.ModCount}";

            var dialog = new ModConflictsWindow(instance.Name, report);
            await dialog.ShowDialog(this);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка проверки конфликтов: " + ex.Message;
        }
        finally
        {
            ConflictsButton.Content = previous;
            ConflictsButton.IsEnabled = true;
        }
    }

    private async Task CheckContentUpdatesAsync()
    {
        if (_current is null)
        {
            StatusText.Text = "Сначала выберите профиль";
            return;
        }

        CheckUpdatesButton.IsEnabled = false;
        StatusText.Text = "Проверяю обновления...";
        ShowOverlay("Проверка обновлений", "Сверяю установленные файлы с Modrinth...");

        try
        {
            var updates = await _services.Content.CheckUpdatesAsync(_current, _services.Modrinth, _cts.Token);
            _updates.Clear();
            foreach (var update in updates)
                _updates[update.Content.FileName] = update;

            RefreshInstalled();
            StatusText.Text = updates.Count == 0
                ? "Обновлений нет"
                : $"Доступно обновлений: {updates.Count}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка проверки: " + ex.Message;
        }
        finally
        {
            HideOverlay();
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void OnUpdateOne(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: InstalledItem item })
            return;

        if (_updates.TryGetValue(item.FileName, out var update))
            await ApplyUpdateAsync(update);
    }

    private async void OnUpdateAll(object? sender, RoutedEventArgs e)
    {
        if (_current is null)
            return;

        var pending = _updates.Values.Where(update => update.Content.Kind == _kind).ToList();
        foreach (var update in pending)
        {
            await ApplyUpdateAsync(update);
            if (_cts.IsCancellationRequested)
                break;
        }
    }

    private async Task ApplyUpdateAsync(ContentUpdate update)
    {
        if (_current is null)
            return;

        var title = string.IsNullOrWhiteSpace(update.Title) ? update.Content.DisplayName : update.Title;
        ShowDownloadOverlay("Обновление", $"{title} → {update.Latest.VersionNumber}");

        try
        {
            await _services.Content.UpdateAsync(_current, update, _cts.Token, contentProgress);
            _updates.Remove(update.Content.FileName);
            RefreshInstalled();
            StatusText.Text = $"Обновлено: {title} → {update.Latest.VersionNumber}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка обновления: " + ex.Message;
        }
        finally
        {
            HideOverlay();
        }
    }

    private void UpdateSearchMarks()
    {
        if (_current is null)
            return;

        var ids = _services.Content.InstalledProjectIds(_current, _resultsKind);
        foreach (var item in _resultItems)
            item.Installed = ids.Contains(item.Project.Key);
    }

    private async void OnDeleteInstalled(object? sender, RoutedEventArgs e)
    {
        if (_current is null || sender is not Button { DataContext: InstalledItem item })
            return;

        if (item.ConfirmDelete)
        {
            DeleteInstalled(item);
            return;
        }

        if (item.Content.Kind == ContentKind.Mod)
        {
            try
            {
                var instance = _current;
                var dependents = await Task.Run(
                    () => _services.Conflicts.FindDependents(instance, item.Content), _cts.Token);

                if (_current != instance)
                    return;

                if (dependents.Count > 0)
                {
                    var dialog = new ModDependentsWindow(item.Title, dependents);
                    if (await dialog.ShowDialog<bool>(this))
                        DeleteInstalled(item);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Не удалось проверить зависимости: " + ex.Message;
            }
        }

        item.ConfirmDelete = true;
        StatusText.Text = $"Удалить «{item.Title}»? Нажмите «Точно?»";
    }

    private void DeleteInstalled(InstalledItem item)
    {
        if (_current is null)
            return;

        try
        {
            _services.Content.Delete(_current, item.Content);
            _updates.Remove(item.FileName);
            RefreshInstalled();
            UpdateSearchMarks();
            StatusText.Text = $"Удалено: {item.Title}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось удалить: " + ex.Message;
        }
    }

    private void RefreshInstalled()
    {
        var kindName = _kind switch
        {
            ContentKind.Mod => "МОДЫ",
            ContentKind.ResourcePack => "РЕСУРСПАКИ",
            _ => "ШЕЙДЕРЫ"
        };

        InstalledTitle.Text = $"УСТАНОВЛЕНО · {kindName}";
        ConflictsButton.IsVisible = _kind == ContentKind.Mod;

        if (_current is null)
        {
            InstalledList.ItemsSource = null;
            UpdateAllButton.IsVisible = false;
            InstalledEmptyText.Text = "Выберите профиль";
            InstalledEmptyText.IsVisible = true;
            return;
        }

        var contents = _services.Content.ListInstalled(_current)
            .Where(item => item.Kind == _kind)
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tokens = (InstalledSearchBox.Text ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var view = new List<InstalledItem>();
        foreach (var content in contents)
        {
            var searchable = $"{content.DisplayName}\n{content.FileName}\n{content.VersionNumber}\n{content.KindName}";
            if (tokens.Length > 0 && !tokens.All(token => searchable.Contains(token, StringComparison.OrdinalIgnoreCase)))
                continue;

            var item = new InstalledItem(content);
            if (_updates.TryGetValue(content.FileName, out var update))
            {
                item.UpdateAvailable = true;
                var from = string.IsNullOrEmpty(content.VersionNumber) ? content.KindName : content.VersionNumber;
                item.SetVersion($"{from} → {update.Latest.VersionNumber}");
            }

            view.Add(item);
        }

        InstalledList.ItemsSource = view;
        InstalledEmptyText.Text = contents.Count == 0 ? "Пока ничего не установлено" : "Ничего не найдено";
        InstalledEmptyText.IsVisible = view.Count == 0;
        UpdateAllButton.IsVisible = _updates.Values.Any(update => update.Content.Kind == _kind);

        foreach (var item in view)
            _ = item.LoadIconAsync(_cts.Token);
    }

    private async Task EnsureModpackSearchAsync()
    {
        if (_modpacksLoaded)
            return;

        await RunModpackSearchAsync();
    }

    private async void OnSearchModpacks(object? sender, RoutedEventArgs e) => await RunModpackSearchAsync();

    private void OnModpackSearchKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        _ = RunModpackSearchAsync();
    }

    private async Task RunModpackSearchAsync()
    {
        _modpackTranslationCts?.Cancel();
        var translationCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _modpackTranslationCts = translationCts;

        var generation = ++_modpackGeneration;
        ModpackMoreButton.IsVisible = false;
        StatusText.Text = "Поиск сборок...";

        try
        {
            var response = await _services.Modrinth.SearchAsync(
                ModpackSearchBox.Text ?? "", "modpack", "", null, _cts.Token, SearchPageSize, 0);

            if (generation != _modpackGeneration)
                return;

            _modpackItems = new ObservableCollection<SearchItem>();
            foreach (var hit in response.Hits)
            {
                var item = new SearchItem(hit, "Модпак");
                _modpackItems.Add(item);
                _ = item.LoadIconAsync(_cts.Token);
            }

            ModpackList.ItemsSource = _modpackItems;
            _modpackOffset = response.Hits.Count;
            _modpackTotal = (int)response.TotalHits;
            UpdateModpackMore();
            _modpacksLoaded = true;
            _ = TranslateDescriptionsAsync(_modpackItems, () => generation == _modpackGeneration, translationCts.Token);
            StatusText.Text = "Готово";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation == _modpackGeneration)
                StatusText.Text = "Ошибка поиска: " + ex.Message;
        }
    }

    private void UpdateModpackMore()
    {
        var shown = _modpackItems.Count;
        var limit = Math.Min(_modpackTotal, SearchMaxItems);
        ModpackMoreButton.IsVisible = shown < limit;
        ModpackMoreButton.Content = $"Показать ещё · {shown} из {limit}";

        if (_modpackTotal > SearchMaxItems && shown >= SearchMaxItems)
            ModpackCountText.Text = $"{_modpackTotal} сборок · первые {SearchMaxItems}";
        else if (_modpackTotal > shown)
            ModpackCountText.Text = $"{_modpackTotal} сборок · показано {shown}";
        else
            ModpackCountText.Text = $"{_modpackTotal} сборок";
    }

    private async void OnLoadMoreModpacks(object? sender, RoutedEventArgs e) => await LoadMoreModpacksAsync();

    private async Task LoadMoreModpacksAsync()
    {
        var limit = Math.Min(_modpackTotal, SearchMaxItems);
        if (_modpackItems.Count >= limit)
        {
            UpdateModpackMore();
            return;
        }

        var generation = _modpackGeneration;
        var translationToken = _modpackTranslationCts?.Token ?? _cts.Token;
        ModpackMoreButton.IsEnabled = false;
        StatusText.Text = "Загружаю ещё...";

        try
        {
            var response = await _services.Modrinth.SearchAsync(
                ModpackSearchBox.Text ?? "", "modpack", "", null, _cts.Token, SearchPageSize, _modpackOffset);

            if (generation != _modpackGeneration)
                return;

            var added = new List<SearchItem>();
            foreach (var hit in response.Hits)
            {
                if (_modpackItems.Count >= SearchMaxItems)
                    break;
                if (_modpackItems.Any(existing => existing.Project.Key == hit.Key))
                    continue;

                var item = new SearchItem(hit, "Модпак");
                _modpackItems.Add(item);
                added.Add(item);
                _ = item.LoadIconAsync(_cts.Token);
            }

            _ = TranslateDescriptionsAsync(added, () => generation == _modpackGeneration, translationToken);
            _modpackOffset += response.Hits.Count;
            _modpackTotal = (int)response.TotalHits;
            UpdateModpackMore();
            StatusText.Text = $"Показано {_modpackItems.Count} из {_modpackTotal}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation == _modpackGeneration)
                StatusText.Text = "Ошибка поиска: " + ex.Message;
        }
        finally
        {
            ModpackMoreButton.IsEnabled = true;
        }
    }

    private async void OnInstallModpack(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SearchItem item })
            return;

        try
        {
            var versions = await _services.Modrinth.GetVersionsAsync(item.Project.Key, _cts.Token);
            var version = versions
                .Where(candidate => candidate.Files.Count > 0)
                .OrderByDescending(candidate => candidate.VersionType == "release")
                .ThenByDescending(candidate => candidate.DatePublished)
                .FirstOrDefault();

            if (version is null)
            {
                StatusText.Text = "У этой сборки нет файлов";
                return;
            }

            ShowDownloadOverlay("Установка сборки", $"Скачивание «{item.Title}»...");
            UpdateOverlayBar(false, 0);

            var instance = await _services.Modpacks.InstallAsync(
                version, item.Title, item.Project.IconUrl, item.Project.Key,
                UpdateOverlay, contentProgress, _cts.Token);

            await LoadInstancesAsync();
            InstanceList.SelectedItem = _instances.FirstOrDefault(candidate => candidate.Id == instance.Id);
            AppendLog($"Сборка «{item.Title}» установлена в профиль {instance.Name}.");
            StatusText.Text = $"Сборка установлена: {instance.Name}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка установки сборки: " + ex.Message;
            AppendLog("Ошибка установки сборки: " + ex.Message);
        }
        finally
        {
            HideOverlay();
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()
            ?.Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToList();

        if (files is null || files.Count == 0)
            return;

        e.Handled = true;
        await ImportDroppedFilesAsync(files);
    }

    private async Task ImportDroppedFilesAsync(IReadOnlyList<string> files)
    {
        var modpacks = files.Where(path => IsExtension(path, ".mrpack")).ToList();
        if (modpacks.Count > 0)
            ShowDownloadOverlay("Установка из файла", "Подготовка сборки...");

        var imported = 0;
        var errors = new List<string>();

        try
        {
            foreach (var path in files)
            {
                if (_cts.IsCancellationRequested)
                    break;

                try
                {
                    if (IsExtension(path, ".mrpack"))
                    {
                        var instance = await _services.Modpacks.InstallArchiveAsync(
                            path, Path.GetFileNameWithoutExtension(path), null, null,
                            UpdateOverlay, contentProgress, _cts.Token);
                        await LoadInstancesAsync();
                        InstanceList.SelectedItem = _instances.FirstOrDefault(item => item.Id == instance.Id);
                        imported++;
                        continue;
                    }

                    if (!IsExtension(path, ".jar") && !IsExtension(path, ".zip"))
                    {
                        errors.Add($"{Path.GetFileName(path)}: поддерживаются .jar, .zip и .mrpack");
                        continue;
                    }

                    if (_current is null)
                    {
                        errors.Add($"{Path.GetFileName(path)}: сначала выберите профиль");
                        continue;
                    }

                    var content = await _services.Content.ImportAsync(_current, path, _cts.Token);
                    imported++;
                    AppendLog($"Импортирован файл {content.FileName} в {ContentService.FolderFor(content.Kind)}.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }

            RefreshInstalled();
            UpdateSearchMarks();
            UpdateBatchButton();
            StatusText.Text = errors.Count == 0
                ? $"Импорт завершён · файлов: {imported}"
                : $"Импортировано: {imported}, ошибок: {errors.Count}";

            foreach (var error in errors)
                AppendLog("Импорт: " + error);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            HideOverlay();
        }
    }

    private async void OnInstallLink(object? sender, RoutedEventArgs e)
    {
        var dialog = new InstallLinkWindow();
        var url = await dialog.ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(url))
            return;

        await InstallFromUrlAsync(url.Trim());
    }

    private async Task InstallFromUrlAsync(string url)
    {
        try
        {
            if (TryParseModrinthUrl(url, out var slug))
            {
                await InstallModrinthProjectAsync(slug);
                return;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                StatusText.Text = "Ссылка должна начинаться с http:// или https://";
                return;
            }

            await InstallFileUrlAsync(uri);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            HideOverlay();
            StatusText.Text = "Ошибка установки по ссылке: " + ex.Message;
        }
    }

    private async Task InstallModrinthProjectAsync(string slug)
    {
        var details = await _services.Modrinth.GetProjectAsync(slug, _cts.Token);

        if (details.ProjectType == "modpack")
        {
            var versions = await _services.Modrinth.GetVersionsAsync(details.Id, _cts.Token);
            var version = versions
                .Where(candidate => candidate.Files.Count > 0)
                .OrderByDescending(candidate => candidate.VersionType == "release")
                .ThenByDescending(candidate => candidate.DatePublished)
                .FirstOrDefault();

            if (version is null)
            {
                StatusText.Text = "У этой сборки нет файлов";
                return;
            }

            ShowDownloadOverlay("Установка сборки", $"Скачивание «{details.Title}»...");
            UpdateOverlayBar(false, 0);

            try
            {
                var instance = await _services.Modpacks.InstallAsync(
                    version, details.Title, details.IconUrl, details.Id,
                    UpdateOverlay, contentProgress, _cts.Token);

                await LoadInstancesAsync();
                InstanceList.SelectedItem = _instances.FirstOrDefault(item => item.Id == instance.Id);
                AppendLog($"Сборка «{details.Title}» установлена в профиль {instance.Name}.");
                StatusText.Text = $"Сборка установлена: {instance.Name}";
            }
            finally
            {
                HideOverlay();
            }

            return;
        }

        if (_current is null)
        {
            StatusText.Text = "Сначала выберите профиль";
            return;
        }

        var kind = KindOf(details.ProjectType);
        var selectedVersion = await _services.Modrinth.PickVersionAsync(
            details.Id, _current.MinecraftVersion, LoaderTag(), details.ProjectType, _cts.Token);

        if (selectedVersion is null)
        {
            StatusText.Text = $"Нет версии под {_current.MinecraftVersion}";
            return;
        }

        var instanceBefore = _current;
        ShowDownloadOverlay("Установка по ссылке", $"Установка «{details.Title}»...");

        try
        {
            var source = new ContentSource(details.Id, details.Title, details.IconUrl);
            await _services.Content.InstallWithDependenciesAsync(
                instanceBefore, selectedVersion, kind, source, _services.Modrinth, _cts.Token, contentProgress);

            RefreshInstalled();
            UpdateSearchMarks();
            UpdateBatchButton();
            StatusText.Text = $"Установлено: {details.Title}";
        }
        finally
        {
            HideOverlay();
        }
    }

    private async Task InstallFileUrlAsync(Uri uri)
    {
        var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName is "." or ".."
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("В ссылке нет корректного имени файла");

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".jar" or ".zip" or ".mrpack"))
            throw new InvalidOperationException("Поддерживаются прямые ссылки на .jar, .zip и .mrpack");

        if (extension != ".mrpack" && _current is null)
        {
            StatusText.Text = "Сначала выберите профиль";
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "ForQLauncher", "import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, fileName);

        ShowDownloadOverlay("Загрузка файла", fileName);

        try
        {
            await _services.Downloads.RunAsync(
                new[] { new DownloadItem(uri.ToString(), tempPath, 0, "", fileName) },
                1,
                contentProgress,
                _cts.Token);

            if (extension == ".mrpack")
            {
                var instance = await _services.Modpacks.InstallArchiveAsync(
                    tempPath, Path.GetFileNameWithoutExtension(fileName), null, null,
                    UpdateOverlay, contentProgress, _cts.Token);
                await LoadInstancesAsync();
                InstanceList.SelectedItem = _instances.FirstOrDefault(item => item.Id == instance.Id);
                StatusText.Text = $"Сборка установлена: {instance.Name}";
            }
            else
            {
                if (_current is null)
                    return;

                var content = await _services.Content.ImportAsync(_current, tempPath, _cts.Token);
                RefreshInstalled();
                UpdateSearchMarks();
                UpdateBatchButton();
                StatusText.Text = $"Установлено: {content.FileName}";
            }
        }
        finally
        {
            HideOverlay();
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool TryParseModrinthUrl(string url, out string slug)
    {
        slug = "";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        if (host != "modrinth.com" && host != "www.modrinth.com")
            return false;

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return false;

        if (segments[0] is not ("mod" or "modpack" or "resourcepack" or "shader"))
            return false;

        slug = Uri.UnescapeDataString(segments[1]);
        return slug.Length > 0;
    }

    private static bool IsExtension(string path, string extension)
        => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

    private async void OnCheckLauncherUpdate(object? sender, RoutedEventArgs e)
        => await CheckLauncherUpdateAsync(false);

    private async Task CheckLauncherUpdateAsync(bool silent)
    {
        StatusText.Text = silent ? "Готово" : "Проверяю обновления лаунчера...";

        var release = await _services.Updates.GetLatestAsync(_cts.Token);
        if (release is null)
        {
            if (!silent)
                StatusText.Text = "Не удалось проверить обновления. Проверьте подключение к интернету.";
            return;
        }

        if (!UpdateService.IsNewer(release.TagName, AppServices.Version))
        {
            if (!silent)
                StatusText.Text = $"Установлена последняя версия {AppServices.Version}";
            return;
        }

        var asset = UpdateService.PickAsset(release);
        if (asset is null)
        {
            if (!silent)
                StatusText.Text = $"В релизе {release.TagName} нет файла для Windows";
            return;
        }

        _pendingAsset = asset;
        UpdateTitle.Text = $"Доступно обновление {release.TagName.TrimStart('v', 'V')}";
        UpdateText.Text = $"У вас {AppServices.Version}. Скачать {asset.Size / 1048576.0:F1} МБ и перезапустить лаунчер?";
        UpdateBanner.IsVisible = true;
        StatusText.Text = $"Доступно обновление {release.TagName}";
    }

    private async void OnInstallUpdate(object? sender, RoutedEventArgs e)
    {
        if (_pendingAsset is null)
            return;

        ShowDownloadOverlay("Обновление лаунчера", "Скачивание новой версии...");
        UpdateOverlayBar(false, 0);

        try
        {
            var path = await _services.Updates.DownloadAsync(
                _pendingAsset, contentProgress, _cts.Token);

            HideOverlay();
            AppendLog("Лаунчер перезапустится с новой версией.");
            _services.Updates.ApplyAndRestart(path);
            Close();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось обновить: " + ex.Message;
        }
        finally
        {
            HideOverlay();
        }
    }

    private void OnDismissUpdate(object? sender, RoutedEventArgs e) => UpdateBanner.IsVisible = false;

    private void OnOpenReleasePage(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"https://github.com/{UpdateService.Repository}/releases/latest")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось открыть браузер: " + ex.Message;
        }
    }

    private void OnSaveSettings(object? sender, RoutedEventArgs e)
    {
        _services.Settings.Username = string.IsNullOrWhiteSpace(SettingsUsernameBox.Text)
            ? "Player"
            : SettingsUsernameBox.Text.Trim();
        _services.Settings.BlockTelemetry = TelemetryCheck.IsChecked ?? true;
        _services.Settings.JavaPath = string.IsNullOrWhiteSpace(JavaPathBox.Text) ? null : JavaPathBox.Text.Trim();
        _services.Settings.AutoCheckUpdates = AutoUpdateCheck.IsChecked ?? true;

        if (int.TryParse(ThreadsBox.Text, out var threads) && threads is >= 1 and <= 16)
            _services.Settings.DownloadThreads = threads;

        if (int.TryParse(SpeedLimitBox.Text, out var speedLimit))
            _services.Settings.DownloadSpeedLimitKbps = Math.Clamp(speedLimit, 0, 102400);

        _services.Downloads.SpeedLimitBytesPerSecond = _services.Settings.DownloadSpeedLimitKbps * 1024L;
        SpeedLimitBox.Text = _services.Settings.DownloadSpeedLimitKbps.ToString();

        _services.SaveSettings();
        UsernameBox.Text = _services.Settings.Username;
        StatusText.Text = "Настройки сохранены";
    }

    private string? LoaderTag() => _current is null ? null : ContentService.LoaderTag(_current);

    private static ContentKind KindOf(string projectType) => projectType switch
    {
        "mod" => ContentKind.Mod,
        "resourcepack" => ContentKind.ResourcePack,
        _ => ContentKind.Shader
    };

    private static string TypeOf(ContentKind kind) => kind switch
    {
        ContentKind.Mod => "mod",
        ContentKind.ResourcePack => "resourcepack",
        _ => "shader"
    };

    private static string TypeLabel(ContentKind kind) => kind switch
    {
        ContentKind.Mod => "Мод",
        ContentKind.ResourcePack => "Ресурспак",
        _ => "Шейдер"
    };

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
        OverlayProgress.Text = "";
        OverlayBar.IsIndeterminate = true;
        OverlayDownloadControls.IsVisible = false;
        Overlay.IsVisible = true;
    }

    private void ShowDownloadOverlay(string title, string status)
    {
        _lastDownload = null;
        ShowOverlay(title, status);
        _services.Downloads.Resume();
        OverlayDownloadControls.IsVisible = true;
        OverlayPauseButton.Content = "Пауза";
    }

    private void OnToggleDownloadPause(object? sender, RoutedEventArgs e)
    {
        var paused = !_services.Downloads.IsPaused;
        if (paused)
            _services.Downloads.Pause();
        else
            _services.Downloads.Resume();

        OverlayPauseButton.Content = paused ? "Продолжить" : "Пауза";
        OverlayStatus.Text = paused
            ? "Загрузка приостановлена. Нажмите «Продолжить»."
            : "Загрузка продолжается...";
        StatusText.Text = paused ? "Загрузка приостановлена" : "Загрузка продолжается...";

        if (_lastDownload is { } state)
            OverlayProgress.Text = FormatDownload(state with { Paused = paused, BytesPerSecond = 0 });
    }

    private static string FormatDownload(DownloadState state)
    {
        if (state.Completed)
            return "Готово";

        var done = state.Done / 1048576.0;
        var total = state.Total / 1048576.0;
        var parts = new List<string>();

        parts.Add(state.Paused ? "Пауза" : "Загрузка");
        if (!string.IsNullOrWhiteSpace(state.Name))
            parts.Add(state.Name);
        parts.Add(total > 0
            ? $"{done:F1} МБ из {total:F1} МБ"
            : $"{done:F1} МБ");

        if (!state.Paused && state.BytesPerSecond > 1)
            parts.Add(FormatRate(state.BytesPerSecond));

        if (!state.Paused && state.Eta is { } eta && eta > TimeSpan.Zero)
            parts.Add("осталось " + FormatDuration(eta));

        return string.Join(" · ", parts);
    }

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1048576)
            return $"{bytesPerSecond / 1048576.0:F1} МБ/с";
        if (bytesPerSecond >= 1024)
            return $"{bytesPerSecond / 1024.0:F0} КБ/с";
        return $"{Math.Max(1, (int)bytesPerSecond)} Б/с";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
            return $"{(int)value.TotalHours} ч {value.Minutes} мин";
        if (value.TotalMinutes >= 1)
            return $"{(int)value.TotalMinutes} мин {value.Seconds} с";
        return $"{Math.Max(1, (int)value.TotalSeconds)} с";
    }

    private void UpdateOverlay(string status) => Dispatcher.UIThread.Post(() => OverlayStatus.Text = status);

    private void UpdateOverlayProgress(string text) => Dispatcher.UIThread.Post(() => OverlayProgress.Text = text);

    private void UpdateOverlayBar(bool determinate, long total, long done = 0) => Dispatcher.UIThread.Post(() =>
    {
        OverlayBar.IsIndeterminate = !determinate;
        if (!determinate || total <= 0)
            return;

        OverlayBar.Maximum = total;
        OverlayBar.Value = Math.Min(done, total);
    });

    private void HideOverlay() => Dispatcher.UIThread.Post(() =>
    {
        _lastDownload = null;
        Overlay.IsVisible = false;
        OverlayProgress.Text = "";
        OverlayBar.IsIndeterminate = true;
        OverlayDownloadControls.IsVisible = false;
    });
}
