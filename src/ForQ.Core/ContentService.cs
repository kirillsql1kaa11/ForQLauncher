using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForQ.Core;

public sealed class ContentService
{
    private readonly DownloadManager _downloads;

    public ContentService(DownloadManager downloads) => _downloads = downloads;

    public static string FolderFor(ContentKind kind) => kind switch
    {
        ContentKind.Mod => "mods",
        ContentKind.ResourcePack => "resourcepacks",
        _ => "shaderpacks"
    };

    private static string LibraryPath(Instance instance) => Path.Combine(instance.Folder, "content.json");

    public ContentLibrary LoadLibrary(Instance instance)
    {
        var path = LibraryPath(instance);
        if (!File.Exists(path))
            return new ContentLibrary();

        try
        {
            return JsonSerializer.Deserialize<ContentLibrary>(File.ReadAllText(path), Json.Options)
                   ?? new ContentLibrary();
        }
        catch (JsonException)
        {
            return new ContentLibrary();
        }
    }

    public void SaveLibrary(Instance instance, ContentLibrary library)
    {
        Directory.CreateDirectory(instance.Folder);
        File.WriteAllText(LibraryPath(instance), JsonSerializer.Serialize(library, Json.Options));
    }

    public async Task<string> InstallAsync(Instance instance, ModrinthVersion version, ContentKind kind, ContentSource source,
        CancellationToken ct, IProgress<DownloadState>? progress = null, string versionChannel = "release")
    {
        var file = ModrinthService.PrimaryFile(version);
        var directory = Path.Combine(instance.Folder, FolderFor(kind));
        Directory.CreateDirectory(directory);

        var library = LoadLibrary(instance);
        foreach (var stale in library.Items
                     .Where(r => r.ProjectId == source.ProjectId && r.Kind == kind && r.FileName != file.Filename)
                     .ToList())
        {
            var stalePath = Path.Combine(directory, stale.FileName);
            if (File.Exists(stalePath))
                File.Delete(stalePath);
            library.Items.Remove(stale);
        }

        var destination = Path.Combine(directory, file.Filename);
        await _downloads.RunAsync(
            new[] { new DownloadItem(file.Url, destination, file.Size, file.Hashes.GetValueOrDefault("sha1", ""), file.Filename) },
            1,
            progress,
            ct);

        if (kind == ContentKind.ResourcePack)
            GameOptions.SetPackEnabled(instance.OptionsPath, file.Filename, true);
        else if (kind == ContentKind.Shader)
            GameOptions.SetShader(instance.OptionsPath, file.Filename);

        var record = new ContentRecord
        {
            FileName = file.Filename,
            Kind = kind,
            ProjectId = source.ProjectId,
            VersionId = version.Id,
            VersionNumber = version.VersionNumber,
            Title = source.Title,
            IconUrl = source.IconUrl,
            Sha1 = file.Hashes.GetValueOrDefault("sha1", ""),
            VersionChannel = NormalizeChannel(versionChannel),
            InstalledAt = DateTimeOffset.Now
        };

        library.Items.RemoveAll(r => string.Equals(r.FileName, file.Filename, StringComparison.OrdinalIgnoreCase) && r.Kind == kind);
        library.Items.Add(record);
        SaveLibrary(instance, library);

        return destination;
    }

    public async Task InstallWithDependenciesAsync(
        Instance instance,
        ModrinthVersion version,
        ContentKind kind,
        ContentSource source,
        ModrinthService modrinth,
        CancellationToken ct,
        IProgress<DownloadState>? progress = null,
        string versionChannel = "release")
    {
        await InstallAsync(instance, version, kind, source, ct, progress, versionChannel);

        var projectType = TypeOf(kind);
        var dependencies = version.Dependencies
            .Where(d => d.DependencyType == "required" && !string.IsNullOrEmpty(d.ProjectId))
            .ToList();

        if (dependencies.Count == 0)
            return;

        var projects = await modrinth.GetProjectsAsync(dependencies.Select(d => d.ProjectId!), ct);

        foreach (var dependency in dependencies)
        {
            var project = projects.FirstOrDefault(p => p.Id == dependency.ProjectId);
            var dependencyType = string.IsNullOrWhiteSpace(project?.ProjectType)
                ? projectType
                : project.ProjectType;
            var dependencyVersion = await modrinth.PickVersionAsync(
                dependency.ProjectId!, instance.MinecraftVersion, LoaderTag(instance), dependencyType, ct);
            if (dependencyVersion is null)
                continue;

            var dependencySource = new ContentSource(
                dependency.ProjectId!,
                project?.Title ?? dependencyVersion.Name,
                project?.IconUrl);

            await InstallAsync(instance, dependencyVersion, KindOfProject(dependencyType), dependencySource, ct, progress);
        }
    }

    public List<InstalledContent> ListInstalled(Instance instance)
    {
        var result = new List<InstalledContent>();
        var optionsPath = instance.OptionsPath;
        var enabledPacks = GameOptions.GetEnabledPacks(optionsPath);
        var shader = GameOptions.GetShader(optionsPath);
        var library = LoadLibrary(instance);

        foreach (var kind in new[] { ContentKind.Mod, ContentKind.ResourcePack, ContentKind.Shader })
        {
            var directory = Path.Combine(instance.Folder, FolderFor(kind));
            if (!Directory.Exists(directory))
                continue;

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                var disabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                var cleanName = disabled ? name[..^".disabled".Length] : name;

                var enabled = kind switch
                {
                    ContentKind.Mod => !disabled,
                    ContentKind.ResourcePack => enabledPacks.Contains("file/" + cleanName, StringComparer.OrdinalIgnoreCase),
                    _ => shader is not null && shader.Equals(cleanName, StringComparison.OrdinalIgnoreCase)
                };

                var record = library.Items.FirstOrDefault(r =>
                    r.Kind == kind && string.Equals(r.FileName, cleanName, StringComparison.OrdinalIgnoreCase));

                result.Add(new InstalledContent
                {
                    FileName = cleanName,
                    Path = file,
                    Kind = kind,
                    Enabled = enabled,
                    Size = new FileInfo(file).Length,
                    ProjectId = record?.ProjectId ?? "",
                    VersionId = record?.VersionId ?? "",
                    VersionNumber = record?.VersionNumber ?? "",
                    Title = record?.Title ?? "",
                    IconUrl = record?.IconUrl,
                    VersionChannel = record?.VersionChannel ?? "release",
                    Pinned = record?.Pinned ?? false
                });
            }
        }

        return result;
    }

    public HashSet<string> InstalledProjectIds(Instance instance, ContentKind kind)
    {
        var directory = Path.Combine(instance.Folder, FolderFor(kind));
        return LoadLibrary(instance).Items
            .Where(record => record.Kind == kind && !string.IsNullOrWhiteSpace(record.ProjectId))
            .Where(record => File.Exists(Path.Combine(directory, record.FileName))
                             || File.Exists(Path.Combine(directory, record.FileName + ".disabled")))
            .Select(record => record.ProjectId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public void Delete(Instance instance, InstalledContent content)
    {
        if (File.Exists(content.Path))
            File.Delete(content.Path);

        if (content.Kind == ContentKind.ResourcePack)
            GameOptions.SetPackEnabled(instance.OptionsPath, content.FileName, false);
        else if (content.Kind == ContentKind.Shader
                 && string.Equals(GameOptions.GetShader(instance.OptionsPath), content.FileName, StringComparison.OrdinalIgnoreCase))
            GameOptions.SetShader(instance.OptionsPath, null);

        var library = LoadLibrary(instance);
        library.Items.RemoveAll(record => record.Kind == content.Kind
                                          && string.Equals(record.FileName, content.FileName, StringComparison.OrdinalIgnoreCase));
        SaveLibrary(instance, library);
    }

    public void SetEnabled(Instance instance, InstalledContent content, bool enabled)
    {
        switch (content.Kind)
        {
            case ContentKind.Mod:
                var isDisabled = content.Path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                var target = !enabled
                    ? (isDisabled ? content.Path : content.Path + ".disabled")
                    : (isDisabled ? content.Path[..^".disabled".Length] : content.Path);
                if (!string.Equals(content.Path, target, StringComparison.OrdinalIgnoreCase))
                    File.Move(content.Path, target, true);
                break;
            case ContentKind.ResourcePack:
                GameOptions.SetPackEnabled(instance.OptionsPath, content.FileName, enabled);
                break;
            case ContentKind.Shader:
                GameOptions.SetShader(instance.OptionsPath, enabled ? content.FileName : null);
                break;
        }
    }

    public ContentRecord? FindRecord(Instance instance, string projectId, ContentKind kind)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return null;

        return LoadLibrary(instance).Items.FirstOrDefault(record =>
            record.Kind == kind && string.Equals(record.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
    }

    public void SetChannel(Instance instance, string projectId, ContentKind kind, string channel)
    {
        var library = LoadLibrary(instance);
        var record = library.Items.FirstOrDefault(item =>
            item.Kind == kind && string.Equals(item.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
        if (record is null)
            return;

        record.VersionChannel = NormalizeChannel(channel);
        SaveLibrary(instance, library);
    }

    public void SetPinned(Instance instance, InstalledContent content, bool pinned)
    {
        var library = LoadLibrary(instance);
        var record = library.Items.FirstOrDefault(item =>
            item.Kind == content.Kind && string.Equals(item.FileName, content.FileName, StringComparison.OrdinalIgnoreCase));

        if (record is null)
        {
            record = new ContentRecord
            {
                FileName = content.FileName,
                Kind = content.Kind,
                ProjectId = content.ProjectId,
                VersionId = content.VersionId,
                VersionNumber = content.VersionNumber,
                Title = content.Title,
                IconUrl = content.IconUrl
            };
            library.Items.Add(record);
        }

        record.Pinned = pinned;
        SaveLibrary(instance, library);
    }

    public async Task<InstalledContent> ImportAsync(Instance instance, string sourcePath, CancellationToken ct)
    {
        var info = ReadArchiveInfo(sourcePath);
        var directory = Path.Combine(instance.Folder, FolderFor(info.Kind));
        Directory.CreateDirectory(directory);

        var fileName = UniqueFileName(directory, Path.GetFileName(sourcePath));
        var destination = Path.Combine(directory, fileName);

        await using (var input = File.OpenRead(sourcePath))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
            await input.CopyToAsync(output, ct);

        if (info.Kind == ContentKind.ResourcePack)
            GameOptions.SetPackEnabled(instance.OptionsPath, fileName, true);
        else if (info.Kind == ContentKind.Shader)
            GameOptions.SetShader(instance.OptionsPath, fileName);

        var library = LoadLibrary(instance);
        library.Items.RemoveAll(record => record.Kind == info.Kind
                                          && string.Equals(record.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        library.Items.Add(new ContentRecord
        {
            FileName = fileName,
            Kind = info.Kind,
            Title = info.Title,
            VersionNumber = info.Version,
            InstalledAt = DateTimeOffset.Now
        });
        SaveLibrary(instance, library);

        return new InstalledContent
        {
            FileName = fileName,
            Path = destination,
            Kind = info.Kind,
            Enabled = true,
            Size = new FileInfo(destination).Length,
            Title = info.Title,
            VersionNumber = info.Version
        };
    }

    public async Task<List<ContentUpdate>> CheckUpdatesAsync(Instance instance, ModrinthService modrinth, CancellationToken ct)
    {
        var installed = ListInstalled(instance);
        if (installed.Count == 0)
            return new List<ContentUpdate>();

        var library = LoadLibrary(instance);
        var updates = new List<ContentUpdate>();
        var identified = new List<(InstalledContent Content, ModrinthVersion Version)>();

        var groups = installed.GroupBy(item => item.Kind == ContentKind.Mod);
        foreach (var group in groups)
        {
            var hashes = new Dictionary<string, InstalledContent>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in group)
            {
                var hash = ModrinthService.Sha1Of(item.Path);
                hashes[hash] = item;
            }

            if (hashes.Count == 0)
                continue;

            var current = await modrinth.GetVersionsByHashesAsync(hashes.Keys, ct);
            var latest = await modrinth.GetLatestVersionsForHashesAsync(
                hashes.Keys, instance.MinecraftVersion, group.Key ? LoaderTag(instance) : null, ct);

            foreach (var pair in hashes)
            {
                if (!current.TryGetValue(pair.Key, out var currentVersion))
                    continue;

                identified.Add((pair.Value, currentVersion));

                var record = library.Items.FirstOrDefault(r =>
                    r.Kind == pair.Value.Kind && string.Equals(r.FileName, pair.Value.FileName, StringComparison.OrdinalIgnoreCase));

                if (record?.Pinned == true)
                    continue;

                var channel = record?.VersionChannel ?? "release";
                ModrinthVersion? latestVersion = null;

                if (latest.TryGetValue(pair.Key, out var bulkVersion)
                    && ModrinthService.AllowsVersionType(channel, bulkVersion.VersionType))
                {
                    latestVersion = bulkVersion;
                }

                latestVersion ??= await modrinth.PickVersionAsync(
                    currentVersion.ProjectId,
                    instance.MinecraftVersion,
                    pair.Value.Kind == ContentKind.Mod ? LoaderTag(instance) : null,
                    ct,
                    channel);

                if (latestVersion is null || latestVersion.Id == currentVersion.Id)
                    continue;

                if (latestVersion.DatePublished < currentVersion.DatePublished)
                    continue;

                var projectId = latestVersion.ProjectId;

                updates.Add(new ContentUpdate
                {
                    Content = pair.Value,
                    Latest = latestVersion,
                    ProjectId = string.IsNullOrEmpty(projectId) ? record?.ProjectId ?? "" : projectId,
                    Title = record?.Title ?? "",
                    IconUrl = record?.IconUrl
                });
            }
        }

        if (identified.Count == 0)
            return updates;

        var projects = await modrinth.GetProjectsAsync(
            identified.Select(i => i.Version.ProjectId).Where(id => !string.IsNullOrEmpty(id)), ct);
        var byId = projects.ToDictionary(p => p.Id, p => p);

        foreach (var (content, version) in identified)
        {
            var record = library.Items.FirstOrDefault(r =>
                r.Kind == content.Kind && string.Equals(r.FileName, content.FileName, StringComparison.OrdinalIgnoreCase));

            if (record is null)
            {
                record = new ContentRecord { FileName = content.FileName, Kind = content.Kind };
                library.Items.Add(record);
            }

            record.ProjectId = version.ProjectId;
            record.VersionId = version.Id;
            record.VersionNumber = version.VersionNumber;
            record.Sha1 = ModrinthService.Sha1Of(content.Path);

            if (byId.TryGetValue(version.ProjectId, out var project))
            {
                record.Title = project.Title;
                record.IconUrl = project.IconUrl;

                foreach (var update in updates.Where(u => string.Equals(u.Content.FileName, content.FileName, StringComparison.OrdinalIgnoreCase)))
                {
                    update.Title = project.Title;
                    update.IconUrl = project.IconUrl;
                }
            }
        }

        foreach (var update in updates)
        {
            var record = library.Items.FirstOrDefault(r =>
                r.Kind == update.Content.Kind && string.Equals(r.FileName, update.Content.FileName, StringComparison.OrdinalIgnoreCase));
            if (record is null || string.IsNullOrEmpty(update.ProjectId))
                continue;

            if (string.IsNullOrEmpty(record.ProjectId))
                record.ProjectId = update.ProjectId;
        }

        SaveLibrary(instance, library);
        return updates;
    }

    public async Task UpdateAsync(Instance instance, ContentUpdate update, CancellationToken ct,
        IProgress<DownloadState>? progress = null)
    {
        var file = ModrinthService.PrimaryFile(update.Latest);
        var directory = Path.Combine(instance.Folder, FolderFor(update.Content.Kind));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, file.Filename);
        var wasEnabled = update.Content.Enabled;

        await _downloads.RunAsync(
            new[] { new DownloadItem(file.Url, destination, file.Size, file.Hashes.GetValueOrDefault("sha1", ""), file.Filename) },
            1,
            progress,
            ct);

        if (!string.Equals(destination, update.Content.Path, StringComparison.OrdinalIgnoreCase) && File.Exists(update.Content.Path))
            File.Delete(update.Content.Path);

        var wasDisabled = update.Content.Path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        if (wasDisabled && File.Exists(destination))
        {
            var disabledPath = destination + ".disabled";
            File.Move(destination, disabledPath, true);
            destination = disabledPath;
        }

        if (update.Content.Kind == ContentKind.ResourcePack)
        {
            GameOptions.SetPackEnabled(instance.OptionsPath, update.Content.FileName, false);
            GameOptions.SetPackEnabled(instance.OptionsPath, file.Filename, wasEnabled);
        }
        else if (update.Content.Kind == ContentKind.Shader && wasEnabled)
        {
            GameOptions.SetShader(instance.OptionsPath, file.Filename);
        }

        var library = LoadLibrary(instance);
        var previous = library.Items.FirstOrDefault(r =>
            r.Kind == update.Content.Kind
            && (string.Equals(r.FileName, update.Content.FileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.FileName, file.Filename, StringComparison.OrdinalIgnoreCase)));

        library.Items.RemoveAll(r => r.Kind == update.Content.Kind
                                     && (string.Equals(r.FileName, update.Content.FileName, StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(r.FileName, file.Filename, StringComparison.OrdinalIgnoreCase)));

        library.Items.Add(new ContentRecord
        {
            FileName = file.Filename,
            Kind = update.Content.Kind,
            ProjectId = update.ProjectId,
            VersionId = update.Latest.Id,
            VersionNumber = update.Latest.VersionNumber,
            Title = string.IsNullOrWhiteSpace(update.Title) ? update.Content.Title : update.Title,
            IconUrl = update.IconUrl ?? update.Content.IconUrl,
            Sha1 = file.Hashes.GetValueOrDefault("sha1", ""),
            VersionChannel = previous?.VersionChannel ?? update.Content.VersionChannel,
            Pinned = previous?.Pinned ?? update.Content.Pinned,
            InstalledAt = DateTimeOffset.Now
        });

        SaveLibrary(instance, library);
    }

    private static string NormalizeChannel(string? channel)
        => channel is "beta" or "alpha" ? channel : "release";

    private static string UniqueFileName(string directory, string fileName)    {
        if (!File.Exists(Path.Combine(directory, fileName)))
            return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            var candidate = $"{stem}-{index}{extension}";
            if (!File.Exists(Path.Combine(directory, candidate)))
                return candidate;
        }
    }

    private static ArchiveInfo ReadArchiveInfo(string path)
    {
        using var archive = ZipFile.OpenRead(path);

        var quilt = FindEntry(archive, "quilt.mod.json");
        if (quilt is not null)
        {
            using var document = JsonDocument.Parse(ReadText(quilt));
            var root = document.RootElement;
            if (root.TryGetProperty("quilt_loader", out var loader) && loader.ValueKind == JsonValueKind.Object)
            {
                var name = loader.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
                    ? GetString(metadata, "name")
                    : "";
                return new ArchiveInfo(ContentKind.Mod, FallbackTitle(name, path), GetString(loader, "version"));
            }
        }

        var fabric = FindEntry(archive, "fabric.mod.json");
        if (fabric is not null)
        {
            using var document = JsonDocument.Parse(ReadText(fabric));
            var root = document.RootElement;
            return new ArchiveInfo(ContentKind.Mod, FallbackTitle(GetString(root, "name"), path), GetString(root, "version"));
        }

        var neoForge = FindEntry(archive, "META-INF/neoforge.mods.toml");
        var forge = FindEntry(archive, "META-INF/mods.toml");
        var forgeEntry = neoForge ?? forge;
        if (forgeEntry is not null)
            return ReadForgeInfo(forgeEntry, path);

        var mcmod = FindEntry(archive, "mcmod.info");
        if (mcmod is not null)
            return ReadMcmodInfo(mcmod, path);

        if (archive.Entries.Any(entry => entry.FullName.Replace('\\', '/')
                .StartsWith("shaders/", StringComparison.OrdinalIgnoreCase)))
            return new ArchiveInfo(ContentKind.Shader, Path.GetFileNameWithoutExtension(path), "");

        if (FindEntry(archive, "pack.mcmeta") is not null)
            return new ArchiveInfo(ContentKind.ResourcePack, Path.GetFileNameWithoutExtension(path), "");

        throw new InvalidDataException($"Не удалось определить тип файла: {Path.GetFileName(path)}");
    }

    private static ArchiveInfo ReadForgeInfo(ZipArchiveEntry entry, string path)
    {
        var text = ReadText(entry);
        var name = Regex.Match(text, @"(?m)^\s*displayName\s*=\s*[""'](?<value>.*?)[""']");
        var version = Regex.Match(text, @"(?m)^\s*version\s*=\s*[""'](?<value>.*?)[""']");
        return new ArchiveInfo(
            ContentKind.Mod,
            FallbackTitle(name.Success ? name.Groups["value"].Value : "", path),
            version.Success ? version.Groups["value"].Value : "");
    }

    private static ArchiveInfo ReadMcmodInfo(ZipArchiveEntry entry, string path)
    {
        using var document = JsonDocument.Parse(ReadText(entry));
        var root = document.RootElement;
        var items = root.ValueKind == JsonValueKind.Array
            ? root
            : root.ValueKind == JsonValueKind.Object
              && root.TryGetProperty("modList", out var list)
              && list.ValueKind == JsonValueKind.Array
                ? list
                : default;

        if (items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                return new ArchiveInfo(
                    ContentKind.Mod,
                    FallbackTitle(GetString(item, "name"), path),
                    GetString(item, "version"));
            }
        }

        return new ArchiveInfo(ContentKind.Mod, Path.GetFileNameWithoutExtension(path), "");
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
        return reader.ReadToEnd().TrimStart('\uFEFF');
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path)
        => archive.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase));

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string FallbackTitle(string title, string path)
        => string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title;

    private sealed record ArchiveInfo(ContentKind Kind, string Title, string Version);

    private static ContentKind KindOfProject(string projectType) => projectType switch
    {
        "resourcepack" => ContentKind.ResourcePack,
        "shader" => ContentKind.Shader,
        _ => ContentKind.Mod
    };

    private static string TypeOf(ContentKind kind) => kind switch
    {
        ContentKind.Mod => "mod",
        ContentKind.ResourcePack => "resourcepack",
        _ => "shader"
    };

    public static string? LoaderTag(Instance instance) => instance.Loader switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        LoaderKind.NeoForge => "neoforge",
        _ => null
    };
}
