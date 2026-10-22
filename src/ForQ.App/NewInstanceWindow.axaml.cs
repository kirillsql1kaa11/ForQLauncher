using Avalonia.Controls;
using Avalonia.Interactivity;
using ForQ.Core;

namespace ForQ.App;

public sealed record InstanceDraft(string Name, string MinecraftVersion, LoaderKind Loader, string LoaderVersion);

public partial class NewInstanceWindow : Window
{
    private static readonly string[] PreferredVersions = { "1.20.1", "1.16.5", "1.12.2" };

    private readonly AppServices _services;
    private bool _loading;

    public NewInstanceWindow()
    {
        _services = AppServices.Current;
        InitializeComponent();
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e) => await LoadVersionsAsync();

    private async Task LoadVersionsAsync()
    {
        try
        {
            var manifest = await _services.Mojang.GetManifestAsync(CancellationToken.None);
            var releases = manifest.Versions
                .Where(v => v.Type == "release")
                .OrderByDescending(v => v.ReleaseTime)
                .Select(v => v.Id)
                .ToList();

            var ordered = PreferredVersions
                .Where(releases.Contains)
                .Concat(releases.Where(r => !PreferredVersions.Contains(r)))
                .ToList();

            VersionBox.ItemsSource = ordered;
            VersionBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            HintText.Text = "Не удалось получить список версий: " + ex.Message;
        }
    }

    private void OnVersionChanged(object? sender, SelectionChangedEventArgs e) => _ = ReloadBuildsAsync();

    private void OnLoaderChanged(object? sender, SelectionChangedEventArgs e) => _ = ReloadBuildsAsync();

    private async Task ReloadBuildsAsync()
    {
        if (_loading)
            return;

        _loading = true;
        try
        {
            var minecraftVersion = VersionBox.SelectedItem as string;
            var loader = SelectedLoader();

            if (minecraftVersion is null || loader == LoaderKind.Vanilla)
            {
                LoaderVersionBox.ItemsSource = null;
                LoaderVersionBox.IsEnabled = false;
                HintText.Text = "Без загрузчика: только игра и ресурспаки.";
                return;
            }

            LoaderVersionBox.IsEnabled = false;
            HintText.Text = "Получаю список версий...";

            var builds = await _services.Loaders.GetBuildsAsync(loader, minecraftVersion, CancellationToken.None);
            LoaderVersionBox.ItemsSource = builds.Select(b => b.Version).ToList();
            LoaderVersionBox.IsEnabled = builds.Count > 0;
            LoaderVersionBox.SelectedIndex = 0;

            HintText.Text = builds.Count == 0
                ? $"Для Minecraft {minecraftVersion} нет сборок {loader}."
                : $"Доступно сборок: {builds.Count}";
        }
        catch (Exception ex)
        {
            HintText.Text = "Не удалось получить версии: " + ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private LoaderKind SelectedLoader() => LoaderBox.SelectedIndex switch
    {
        1 => LoaderKind.Fabric,
        2 => LoaderKind.Quilt,
        3 => LoaderKind.NeoForge,
        _ => LoaderKind.Vanilla
    };

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        var minecraftVersion = VersionBox.SelectedItem as string;
        if (minecraftVersion is null)
        {
            HintText.Text = "Выберите версию Minecraft";
            return;
        }

        var loader = SelectedLoader();
        var loaderVersion = loader == LoaderKind.Vanilla ? "" : LoaderVersionBox.SelectedItem as string ?? "";

        if (loader != LoaderKind.Vanilla && string.IsNullOrEmpty(loaderVersion))
        {
            HintText.Text = "Выберите версию загрузчика";
            return;
        }

        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? minecraftVersion : NameBox.Text.Trim();
        Close(new InstanceDraft(name, minecraftVersion, loader, loaderVersion));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
