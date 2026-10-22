using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ForQ.Core;

namespace ForQ.App;

public partial class ContentDetailsWindow : Window
{
    private static readonly HashSet<string> LoaderTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric", "quilt", "forge", "neoforge"
    };

    private readonly AppServices _services = AppServices.Current;
    private readonly string _projectId;
    private readonly string _projectType;
    private readonly Instance? _instance;
    private readonly CancellationTokenSource _cts = new();
    private ModrinthProjectDetails? _details;
    private ModrinthVersion? _version;
    private bool _installed;
    private bool _projectInstalled;

    public ContentDetailsWindow() : this(string.Empty, "mod", null)
    {
    }

    public ContentDetailsWindow(string projectId, string projectType, Instance? instance)
    {
        InitializeComponent();
        _projectId = projectId;
        _projectType = projectType;
        _instance = instance;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private string TypeLabel => _projectType switch
    {
        "mod" => "Мод",
        "resourcepack" => "Ресурспак",
        "shader" => "Шейдер",
        "modpack" => "Модпак",
        _ => "Проект"
    };

    private void OnOpened(object? sender, EventArgs e) => _ = LoadAsync();

    private void OnClosed(object? sender, EventArgs e) => _cts.Cancel();

    private async Task LoadAsync()
    {
        var ct = _cts.Token;
        try
        {
            var details = await _services.Modrinth.GetProjectAsync(_projectId, ct);
            _details = details;

            Title = details.Title;
            TitleText.Text = details.Title;
            MetaText.Text = BuildMeta(details);
            BadgesText.Text = BuildBadges(details);

            var heroUrl = details.Gallery.FirstOrDefault(g => g.Featured)?.Url
                          ?? details.Gallery.FirstOrDefault()?.Url;

            var iconTask = ImageCache.LoadAsync(details.IconUrl, ct);
            var heroTask = ImageCache.LoadAsync(heroUrl, ct);

            IconImage.Source = await iconTask;
            var hero = await heroTask;
            HeroImage.Source = hero ?? IconImage.Source;
            HeroImage.Stretch = hero is null ? Stretch.Uniform : Stretch.UniformToFill;

            await LoadVersionAsync();

            if (_projectType == "modpack")
            {
                DependenciesBlock.IsVisible = false;
                PackNote.IsVisible = true;
            }
            else
            {
                await LoadDependenciesAsync();
            }

            await LoadSimilarAsync(details);

            var description = details.Description;
            var body = MarkdownText.ToPlain(details.Body);
            if (body.Length > 3000)
                body = body[..3000].TrimEnd() + "…";

            DescriptionText.Text = await _services.Translation.ToRussianAsync(description, ct);
            BodyText.Text = string.IsNullOrWhiteSpace(body)
                ? "Автор не добавил подробное описание."
                : await _services.Translation.ToRussianAsync(body, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка загрузки: " + ex.Message;
        }
    }

    private async Task LoadVersionAsync()
    {
        if (_instance is null)
        {
            StatusText.Text = "Сначала выберите профиль";
            return;
        }

        if (_projectType == "modpack")
        {
            var versions = await _services.Modrinth.GetVersionsAsync(_projectId, _cts.Token);
            _version = versions
                .Where(v => v.Files.Count > 0)
                .OrderByDescending(v => v.VersionType == "release")
                .ThenByDescending(v => v.DatePublished)
                .FirstOrDefault();
        }
        else
        {
            _version = await _services.Modrinth.PickVersionAsync(
                _projectId, _instance.MinecraftVersion, LoaderTag(), _projectType, _cts.Token);
        }

        if (_version is null)
        {
            InstallButton.IsEnabled = false;
            StatusText.Text = $"Нет версии под {_instance.MinecraftVersion}";
            return;
        }

        _projectInstalled = IsProjectInstalled();
        InstallButton.IsEnabled = !_projectInstalled;
        InstallButton.Content = _projectType == "modpack"
            ? "Установить сборку"
            : _projectInstalled ? "Установлено" : "Установить";
        var size = ModrinthService.PrimaryFile(_version).Size;
        var versionLabel = _version.VersionNumber.Length > 0 ? _version.VersionNumber : _version.Name;
        StatusText.Text = _projectInstalled
            ? $"Уже установлено · {versionLabel} · {size / 1048576.0:F1} МБ"
            : $"Версия {versionLabel} · {size / 1048576.0:F1} МБ";
    }

    private bool IsProjectInstalled()
    {
        if (_instance is null || _projectType == "modpack")
            return false;
        return _services.Content.InstalledProjectIds(_instance, KindOf(_projectType)).Contains(_projectId);
    }

    private async Task LoadDependenciesAsync()
    {
        DependencyPanel.Children.Clear();

        if (_version is null)
            return;

        var dependencies = _version.Dependencies
            .Where(d => !string.IsNullOrEmpty(d.ProjectId))
            .ToList();

        if (dependencies.Count == 0)
        {
            AddDependencyLine("Нет обязательных зависимостей");
            return;
        }

        var projects = await _services.Modrinth.GetProjectsAsync(
            dependencies.Select(d => d.ProjectId!), _cts.Token);

        var installed = _instance is null
            ? new HashSet<string>()
            : _services.Content.ListInstalled(_instance)
                .Select(item => item.ProjectId)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dependency in dependencies)
        {
            var project = projects.FirstOrDefault(p => p.Id == dependency.ProjectId);
            var name = project?.Title ?? dependency.ProjectId!;
            var status = dependency.DependencyType switch
            {
                "required" => "обязательно",
                "optional" => "по желанию",
                _ => "несовместимо"
            };

            var installedMark = installed.Contains(dependency.ProjectId!) ? " · уже установлен" : "";
            AddDependencyLine($"• {name} — {status}{installedMark}");
        }
    }

    private void AddDependencyLine(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };
        block.Classes.Add("muted");
        DependencyPanel.Children.Add(block);
    }

    private async Task LoadSimilarAsync(ModrinthProjectDetails details)
    {
        var categories = details.Categories
            .Where(category => !LoaderTags.Contains(category))
            .Take(2)
            .ToList();

        if (categories.Count == 0)
        {
            SimilarBlock.IsVisible = false;
            return;
        }

        var gameVersion = _projectType == "modpack" ? "" : _instance?.MinecraftVersion ?? "";
        var response = await _services.Modrinth.SearchByCategoryAsync(
            _projectType, categories, gameVersion, LoaderTag(), _cts.Token, 8);

        var items = response.Hits
            .Where(hit => hit.Key != _projectId)
            .Take(6)
            .Select(hit => new SearchItem(hit, TypeLabel))
            .ToList();

        SimilarList.ItemsSource = items;
        SimilarBlock.IsVisible = items.Count > 0;

        foreach (var item in items)
            _ = item.LoadIconAsync(_cts.Token);
    }

    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        if (_version is null || _instance is null)
            return;

        InstallButton.IsEnabled = false;
        Progress.IsVisible = true;
        Progress.IsIndeterminate = true;

        var progress = new Progress<DownloadState>(state =>
        {
            if (state.Total > 0)
            {
                Progress.IsIndeterminate = false;
                Progress.Maximum = state.Total;
                Progress.Value = Math.Min(state.Done, state.Total);
            }

            StatusText.Text = $"{state.Name}: {state.Done / 1048576.0:F1} МБ";
        });

        try
        {
            if (_projectType == "modpack")
            {
                var instance = await _services.Modpacks.InstallAsync(
                    _version,
                    _details?.Title ?? _projectId,
                    _details?.IconUrl,
                    _projectId,
                    text => Dispatcher.UIThread.Post(() => StatusText.Text = text),
                    progress,
                    _cts.Token);

                _installed = true;
                StatusText.Text = $"Сборка установлена: профиль «{instance.Name}»";
            }
            else
            {
                var kind = KindOf(_projectType);
                var source = new ContentSource(_projectId, _details?.Title ?? _projectId, _details?.IconUrl);
                await _services.Content.InstallWithDependenciesAsync(
                    _instance, _version, kind, source, _services.Modrinth, _cts.Token, progress);

                _installed = true;
                StatusText.Text = $"Установлено в {ContentService.FolderFor(kind)}";
            }

            if (_projectType != "modpack")
                _projectInstalled = true;
            InstallButton.Content = _projectInstalled ? "Установлено" : "Установить сборку";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка установки: " + ex.Message;
        }
        finally
        {
            Progress.IsVisible = false;
            InstallButton.IsEnabled = _version is not null && !_projectInstalled;
        }
    }

    private async void OnOpenSimilar(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SearchItem item })
            return;

        var dialog = new ContentDetailsWindow(item.Project.Key, item.Project.ProjectType, _instance);
        var installed = await dialog.ShowDialog<bool>(this);
        if (installed)
        {
            _installed = true;
            StatusText.Text = "Установлено";
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(_installed);

    private string? LoaderTag() => _instance is null ? null : ContentService.LoaderTag(_instance);

    private static ContentKind KindOf(string projectType) => projectType switch
    {
        "mod" => ContentKind.Mod,
        "resourcepack" => ContentKind.ResourcePack,
        _ => ContentKind.Shader
    };

    private string BuildMeta(ModrinthProjectDetails details)
    {
        var parts = new List<string> { TypeLabel };

        if (details.Categories.Count > 0)
            parts.Add(string.Join(", ", details.Categories.Take(4)));

        if (details.Loaders.Count > 0)
            parts.Add(string.Join(", ", details.Loaders));

        return string.Join(" · ", parts);
    }

    private static string BuildBadges(ModrinthProjectDetails details)
    {
        var parts = new List<string>
        {
            $"{Format.Count(details.Downloads)} скачиваний",
            $"{Format.Count(details.Followers)} подписчиков"
        };

        if (details.License is { Name.Length: > 0 })
            parts.Add(details.License.Name);

        if (details.GameVersions.Count > 0)
            parts.Add("MC " + string.Join(", ", details.GameVersions.Take(6)));

        return string.Join("   ·   ", parts);
    }
}
