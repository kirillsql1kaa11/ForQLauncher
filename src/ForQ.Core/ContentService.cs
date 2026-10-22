using System.Text.Json;

namespace ForQ.Core;

public sealed class ContentService
{
    private readonly HttpApi _http;

    public ContentService(HttpApi http) => _http = http;

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
        CancellationToken ct, IProgress<DownloadState>? progress = null)
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
        var downloaded = 0L;
        await _http.DownloadFileAsync(file.Url, destination, file.Size, file.Hashes.GetValueOrDefault("sha1", ""), ct, bytes =>
        {
            var current = Interlocked.Add(ref downloaded, bytes);
            progress?.Report(new DownloadState(file.Filename, current, file.Size, false));
        });

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
        IProgress<DownloadState>? progress = null)
    {
        await InstallAsync(instance, version, kind, source, ct, progress);

        var projectType = TypeOf(kind);
        var dependencies = version.Dependencies
            .Where(d => d.DependencyType == "required" && !string.IsNullOrEmpty(d.ProjectId))
            .ToList();

        if (dependencies.Count == 0)
            return;

        var projects = await modrinth.GetProjectsAsync(dependencies.Select(d => d.ProjectId!), ct);

        foreach (var dependency in dependencies)
        {
            var dependencyVersion = await modrinth.PickVersionAsync(
                dependency.ProjectId!, instance.MinecraftVersion, LoaderTag(instance), projectType, ct);
            if (dependencyVersion is null)
                continue;

            var project = projects.FirstOrDefault(p => p.Id == dependency.ProjectId);
            var dependencySource = new ContentSource(
                dependency.ProjectId!,
                project?.Title ?? dependencyVersion.Name,
                project?.IconUrl);

            await InstallAsync(instance, dependencyVersion, kind, dependencySource, ct);
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
                    IconUrl = record?.IconUrl
                });
            }
        }

        return result;
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

                if (!latest.TryGetValue(pair.Key, out var latestVersion))
                    continue;

                if (latestVersion.Id == currentVersion.Id)
                    continue;

                if (latestVersion.DatePublished < currentVersion.DatePublished)
                    continue;

                var projectId = latestVersion.ProjectId;
                var record = library.Items.FirstOrDefault(r =>
                    r.Kind == pair.Value.Kind && string.Equals(r.FileName, pair.Value.FileName, StringComparison.OrdinalIgnoreCase));

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

        var downloaded = 0L;
        await _http.DownloadFileAsync(file.Url, destination, file.Size, file.Hashes.GetValueOrDefault("sha1", ""), ct, bytes =>
        {
            var current = Interlocked.Add(ref downloaded, bytes);
            progress?.Report(new DownloadState(file.Filename, current, file.Size, false));
        });

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
            InstalledAt = DateTimeOffset.Now
        });

        SaveLibrary(instance, library);
    }

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
