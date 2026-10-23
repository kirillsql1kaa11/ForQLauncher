using System.IO.Compression;
using System.Text.Json;

namespace ForQ.Core;

public sealed class ModpackService
{
    private readonly InstanceService _instances;
    private readonly GameInstaller _installer;
    private readonly DownloadManager _downloads;

    public ModpackService(InstanceService instances, GameInstaller installer, DownloadManager downloads)
    {
        _instances = instances;
        _installer = installer;
        _downloads = downloads;
    }

    public async Task<Instance> InstallAsync(
        ModrinthVersion version,
        string packTitle,
        string? packIcon,
        string? projectId,
        Action<string>? log,
        IProgress<DownloadState>? progress,
        CancellationToken ct)
    {
        var file = ModrinthService.PrimaryFile(version);
        var tempDirectory = Path.Combine(Path.GetTempPath(), "ForQLauncher", "packs");
        Directory.CreateDirectory(tempDirectory);
        var archivePath = Path.Combine(tempDirectory, file.Filename);

        try
        {
            log?.Invoke($"Скачивание сборки {packTitle}...");
            await _downloads.RunAsync(
                new[] { new DownloadItem(file.Url, archivePath, file.Size, file.Hashes.GetValueOrDefault("sha1", ""), file.Filename) },
                1,
                progress,
                ct);

            return await InstallArchiveAsync(archivePath, packTitle, packIcon, projectId, log, progress, ct);
        }
        finally
        {
            TryDelete(archivePath);
        }
    }

    public async Task<Instance> InstallArchiveAsync(
        string archivePath,
        string packTitle,
        string? packIcon,
        string? projectId,
        Action<string>? log,
        IProgress<DownloadState>? progress,
        CancellationToken ct)
    {
        var index = ReadIndex(archivePath);
        var (loader, loaderVersion, minecraftVersion) = ReadLoader(index);

        if (string.IsNullOrEmpty(minecraftVersion))
            throw new InvalidDataException("В сборке не указана версия Minecraft");

        var name = string.IsNullOrWhiteSpace(index.Name) ? packTitle : index.Name.Trim();
        if (name.Length > 60)
            name = name[..60].Trim();

        Instance? instance = null;
        try
        {
            log?.Invoke($"Создание профиля «{name}»...");
            instance = await _instances.CreateAsync(name, minecraftVersion, loader, loaderVersion);

            log?.Invoke("Установка игры...");
            await _installer.InstallVanillaAsync(instance, log ?? (_ => { }), ct, progress);

            if (loader != LoaderKind.Vanilla)
            {
                log?.Invoke($"Установка {instance.LoaderName}...");
                await _installer.InstallLoaderAsync(instance, log ?? (_ => { }), ct);
                await _instances.SaveAsync(instance);
            }

            var files = index.Files
                .Where(file => !string.Equals(file.Env?.Client, "unsupported", StringComparison.OrdinalIgnoreCase))
                .Where(file => file.Downloads.Count > 0)
                .ToList();

            var downloadItems = new List<DownloadItem>();
            foreach (var packFile in files)
            {
                var destination = SafePath(instance.Folder, packFile.Path);
                if (destination is null)
                    continue;

                downloadItems.Add(new DownloadItem(
                    packFile.Downloads[0],
                    destination,
                    packFile.FileSize,
                    packFile.Hashes.GetValueOrDefault("sha1", ""),
                    Path.GetFileName(packFile.Path)));
            }

            log?.Invoke($"Скачивание файлов сборки ({downloadItems.Count})...");
            await _downloads.RunAsync(downloadItems, 8, progress, ct);

            log?.Invoke("Распаковка настроек и конфигов...");
            ExtractOverrides(archivePath, instance.Folder, "overrides");
            ExtractOverrides(archivePath, instance.Folder, "client-overrides");

            GameOptions.EnsureRussian(instance.OptionsPath);
            await _instances.SaveAsync(instance);
            return instance;
        }
        catch
        {
            if (instance is not null)
                _instances.Delete(instance);
            throw;
        }
    }

    private static ModpackIndex ReadIndex(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, "modrinth.index.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("В файле сборки нет modrinth.index.json");

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<ModpackIndex>(json, Json.Options)
               ?? throw new InvalidDataException("Не удалось прочитать описание сборки");
    }

    private static (LoaderKind Loader, string LoaderVersion, string MinecraftVersion) ReadLoader(ModpackIndex index)
    {
        index.Dependencies.TryGetValue("minecraft", out var minecraft);
        minecraft ??= "";

        if (index.Dependencies.TryGetValue("fabric-loader", out var fabric))
            return (LoaderKind.Fabric, fabric, minecraft);

        if (index.Dependencies.TryGetValue("quilt-loader", out var quilt))
            return (LoaderKind.Quilt, quilt, minecraft);

        if (index.Dependencies.TryGetValue("neoforge", out var neoforge))
            return (LoaderKind.NeoForge, neoforge, minecraft);

        if (index.Dependencies.ContainsKey("forge"))
            throw new InvalidDataException("Сборки для Forge не поддерживаются: нужен Fabric, Quilt или NeoForge");

        return (LoaderKind.Vanilla, "", minecraft);
    }

    private static void ExtractOverrides(string archivePath, string targetDirectory, string prefix)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (!normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(entry.Name))
                continue;

            var relative = normalized[(prefix.Length + 1)..];
            var destination = SafePath(targetDirectory, relative);
            if (destination is null)
                continue;

            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            entry.ExtractToFile(destination, true);
        }
    }

    private static string? SafePath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var combined = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return combined.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? combined : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
