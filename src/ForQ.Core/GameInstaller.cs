using System.Text.Json;

namespace ForQ.Core;

public sealed class GameInstaller
{
    private readonly MojangService _mojang;
    private readonly AssetService _assets;
    private readonly RuntimeService _runtime;
    private readonly LoaderService _loaders;
    private readonly DownloadManager _downloads;
    private readonly HttpApi _http;

    public GameInstaller(
        MojangService mojang,
        AssetService assets,
        RuntimeService runtime,
        LoaderService loaders,
        DownloadManager downloads,
        HttpApi http)
    {
        _mojang = mojang;
        _assets = assets;
        _runtime = runtime;
        _loaders = loaders;
        _downloads = downloads;
        _http = http;
    }

    public async Task InstallVanillaAsync(Instance instance, Action<string>? log, CancellationToken ct)
    {
        var versionId = instance.MinecraftVersion;
        var version = await _mojang.GetVersionAsync(versionId, ct);
        var raw = await _mojang.GetVersionRawAsync(versionId, ct);

        var versionDirectory = Path.Combine(instance.Folder, "versions", versionId);
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllTextAsync(Path.Combine(versionDirectory, versionId + ".json"), raw, ct);

        var librariesDirectory = Path.Combine(instance.Folder, "libraries");
        var items = new List<DownloadItem>();

        if (version.Downloads.Client is { } client)
            items.Add(new DownloadItem(client.Url, Path.Combine(versionDirectory, versionId + ".jar"),
                client.Size, client.Sha1, "Клиент"));

        foreach (var library in LibraryResolver.Resolve(version.Libraries))
        {
            if (library.ClasspathFile is not null)
            {
                var info = library.Library.Downloads?.Classpath ?? library.Library.Downloads?.Artifact;
                if (info is not null)
                    items.Add(new DownloadItem(info.Url, Path.Combine(librariesDirectory, library.ClasspathFile),
                        info.Size, info.Sha1, library.Library.Name));
            }

            if (library.NativeDownload is not null && library.NativeFile is not null)
                items.Add(new DownloadItem(library.NativeDownload.Url, Path.Combine(librariesDirectory, library.NativeFile),
                    library.NativeDownload.Size, library.NativeDownload.Sha1, library.Library.Name));
        }

        log?.Invoke($"Скачиваю файлы игры ({items.Count} шт.)...");
        await _downloads.RunAsync(items, 8, null, ct);

        await InstallAssetsAsync(instance, version, log, ct);
    }

    public async Task InstallAssetsAsync(Instance instance, GameVersion version, Action<string>? log, CancellationToken ct)
    {
        if (version.AssetIndex is null)
            return;

        log?.Invoke("Скачиваю ресурсы и звуки...");
        await _assets.InstallAsync(Path.Combine(instance.Folder, "assets"), version.AssetIndex, _downloads, null, ct);
    }

    public async Task InstallLoaderAsync(Instance instance, Action<string>? log, CancellationToken ct)
    {
        switch (instance.Loader)
        {
            case LoaderKind.Fabric:
            case LoaderKind.Quilt:
                await InstallLoaderProfileAsync(instance, log, ct);
                break;
            case LoaderKind.NeoForge:
                await InstallNeoForgeAsync(instance, log, ct);
                break;
        }
    }

    private async Task InstallLoaderProfileAsync(Instance instance, Action<string>? log, CancellationToken ct)
    {
        var profile = await _loaders.BuildProfileAsync(instance.Loader, instance.MinecraftVersion, instance.LoaderVersion, ct);
        var versionDirectory = Path.Combine(instance.Folder, "versions", profile.Id);
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllTextAsync(Path.Combine(versionDirectory, profile.Id + ".json"),
            JsonSerializer.Serialize(profile, Json.Options), ct);

        var librariesDirectory = Path.Combine(instance.Folder, "libraries");
        var items = new List<DownloadItem>();

        foreach (var library in profile.Libraries)
        {
            var info = library.Downloads?.Artifact;
            if (info is null)
                continue;
            var fileName = LibraryResolver.FileNameFromUrl(info.Url);
            items.Add(new DownloadItem(info.Url, Path.Combine(librariesDirectory, LibraryResolver.PathFor(library.Name, fileName)),
                info.Size, info.Sha1, library.Name));
        }

        log?.Invoke($"Скачиваю {instance.Loader} {instance.LoaderVersion}...");
        await _downloads.RunAsync(items, 8, null, ct);

        instance.ProfileId = profile.Id;
    }

    private async Task InstallNeoForgeAsync(Instance instance, Action<string>? log, CancellationToken ct)
    {
        var version = instance.LoaderVersion;
        var installerUrl = LoaderService.NeoForgeInstallerUrl(version);
        var installerPath = Path.Combine(instance.Folder, "natives", $"neoforge-{version}-installer.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(installerPath)!);

        log?.Invoke($"Скачиваю установщик NeoForge {version}...");
        await _http.DownloadFileAsync(installerUrl, installerPath, 0, "", ct);

        WriteLauncherProfile(instance);

        var java = await EnsureJavaAsync(instance, log, ct);
        var versionsDirectory = Path.Combine(instance.Folder, "versions");
        var before = Directory.Exists(versionsDirectory)
            ? Directory.GetDirectories(versionsDirectory).ToHashSet()
            : new HashSet<string>();

        log?.Invoke("Запускаю установщик NeoForge...");
        var result = await ProcessRunner.RunAsync(
            java,
            new[] { "-Xmx2G", "-jar", installerPath, "--install-client", instance.Folder },
            instance.Folder,
            ct,
            log);

        if (!result.Success)
            throw new InvalidOperationException("Установщик NeoForge завершился с ошибкой:\n" + result.Output);

        var created = Directory.GetDirectories(versionsDirectory)
            .Where(d => !before.Contains(d))
            .ToList();

        if (created.Count == 0)
            throw new InvalidOperationException("Установщик NeoForge не создал версию:\n" + result.Output);

        instance.ProfileId = Path.GetFileName(created[0]);
        log?.Invoke($"NeoForge установлен: {instance.ProfileId}");

        var profile = await LoadVersionAsync(instance, instance.ProfileId, ct);
        var parent = profile?.InheritsFrom;
        if (!string.IsNullOrEmpty(parent) && parent != instance.MinecraftVersion)
        {
            log?.Invoke($"Сборка рассчитана на Minecraft {parent}, обновляю инстанс...");
            instance.MinecraftVersion = parent;
            var vanilla = await LoadVersionAsync(instance, parent, ct);
            if (vanilla is not null)
                await InstallAssetsAsync(instance, vanilla, log, ct);
        }
    }

    private async Task<GameVersion?> LoadVersionAsync(Instance instance, string versionId, CancellationToken ct)
    {
        var path = Path.Combine(instance.Folder, "versions", versionId, versionId + ".json");
        if (File.Exists(path))
            return JsonSerializer.Deserialize<GameVersion>(await File.ReadAllTextAsync(path, ct), Json.Options);

        try
        {
            return await _mojang.GetVersionAsync(versionId, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static void WriteLauncherProfile(Instance instance)
    {
        var versionId = instance.MinecraftVersion;
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var profile = new
        {
            profiles = new Dictionary<string, object>
            {
                [versionId] = new
                {
                    name = versionId,
                    type = "custom",
                    created = now,
                    lastUsed = now,
                    lastVersionId = versionId,
                    gameDir = "."
                }
            },
            selectedProfile = versionId,
            clientToken = "00000000000000000000000000000000",
            authenticationDatabase = new Dictionary<string, object>(),
            launcherVersion = new { id = versionId, name = versionId, type = "release" }
        };

        File.WriteAllText(
            Path.Combine(instance.Folder, "launcher_profiles.json"),
            JsonSerializer.Serialize(profile, Json.Options));
    }

    public async Task<string> EnsureJavaAsync(Instance instance, Action<string>? log, CancellationToken ct)
    {
        var version = await _mojang.GetVersionAsync(instance.MinecraftVersion, ct);
        var component = version.JavaVersion?.Component ?? "jre-legacy";
        log?.Invoke($"Готовлю Java ({component})...");
        return await _runtime.EnsureAsync(component, instance.RootPath, _downloads, null, ct);
    }
}
