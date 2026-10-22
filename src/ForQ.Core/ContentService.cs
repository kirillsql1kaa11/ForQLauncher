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

    public async Task<string> InstallAsync(Instance instance, ModrinthVersion version, ContentKind kind, CancellationToken ct,
        IProgress<DownloadState>? progress = null)
    {
        var file = ModrinthService.PrimaryFile(version);
        var directory = Path.Combine(instance.Folder, FolderFor(kind));
        Directory.CreateDirectory(directory);

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

        return destination;
    }

    public async Task InstallWithDependenciesAsync(
        Instance instance,
        ModrinthVersion version,
        ContentKind kind,
        ModrinthService modrinth,
        CancellationToken ct,
        IProgress<DownloadState>? progress = null)
    {
        await InstallAsync(instance, version, kind, ct, progress);

        var projectType = kind switch
        {
            ContentKind.Mod => "mod",
            ContentKind.ResourcePack => "resourcepack",
            _ => "shader"
        };

        foreach (var dependency in version.Dependencies.Where(d => d.DependencyType == "required" && d.ProjectId is not null))
        {
            var dependencyVersion = await modrinth.PickVersionAsync(
                dependency.ProjectId!, instance.MinecraftVersion, LoaderTag(instance), projectType, ct);
            if (dependencyVersion is null)
                continue;

            var file = ModrinthService.PrimaryFile(dependencyVersion);
            var directory = Path.Combine(instance.Folder, FolderFor(kind));
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, file.Filename);

            if (File.Exists(destination))
                continue;

            await _http.DownloadFileAsync(file.Url, destination, file.Size, file.Hashes.GetValueOrDefault("sha1", ""), ct);
        }
    }
    private static string? LoaderTag(Instance instance) => instance.Loader switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        LoaderKind.NeoForge => "neoforge",
        _ => null
    };

    public List<InstalledContent> ListInstalled(Instance instance)
    {
        var result = new List<InstalledContent>();
        var optionsPath = instance.OptionsPath;
        var enabledPacks = GameOptions.GetEnabledPacks(optionsPath);
        var shader = GameOptions.GetShader(optionsPath);

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

                result.Add(new InstalledContent
                {
                    FileName = cleanName,
                    Path = file,
                    Kind = kind,
                    Enabled = enabled,
                    Size = new FileInfo(file).Length
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
                var target = content.Path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    ? content.Path[..^".disabled".Length]
                    : content.Path + ".disabled";
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
}
