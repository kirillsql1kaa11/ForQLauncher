using System.Text.Json;

namespace ForQ.Core;

public sealed class InstanceService
{
    private readonly string _root;

    public InstanceService(string root) => _root = root;

    public string Root => _root;
    public string InstancesDirectory => Path.Combine(_root, "instances");
    public string RuntimeDirectory => Path.Combine(_root, "runtime");
    public string SharedDirectory => Path.Combine(_root, "shared");
    public string SharedLibrariesDirectory => Path.Combine(SharedDirectory, "libraries");
    public string SharedAssetsDirectory => Path.Combine(SharedDirectory, "assets");
    public string SettingsPath => Path.Combine(_root, "settings.json");

    public async Task<List<Instance>> LoadAsync()
    {
        var result = new List<Instance>();
        if (!Directory.Exists(InstancesDirectory))
            return result;

        foreach (var directory in Directory.EnumerateDirectories(InstancesDirectory))
        {
            var file = Path.Combine(directory, "instance.json");
            if (!File.Exists(file))
                continue;
            try
            {
                var instance = JsonSerializer.Deserialize<Instance>(await File.ReadAllTextAsync(file), Json.Options);
                if (instance is not null)
                {
                    instance.RootPath = _root;
                    result.Add(instance);
                }
            }
            catch (JsonException)
            {
            }
        }

        return result.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task SaveAsync(Instance instance)
    {
        Directory.CreateDirectory(instance.Folder);
        await File.WriteAllTextAsync(
            Path.Combine(instance.Folder, "instance.json"),
            JsonSerializer.Serialize(instance, Json.Options));
    }

    public async Task<Instance> CreateAsync(string name, string minecraftVersion, LoaderKind loader, string loaderVersion)
    {
        var id = Slug(name) + "-" + Guid.NewGuid().ToString("N")[..6];
        var instance = new Instance
        {
            Id = id,
            Name = name,
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            ProfileId = loader == LoaderKind.Vanilla ? minecraftVersion : $"{loader.ToString().ToLowerInvariant()}-{loaderVersion}-{minecraftVersion}"
        };

        instance.RootPath = _root;
        PrepareFolders(instance);
        GameOptions.EnsureRussian(instance.OptionsPath);
        await SaveAsync(instance);
        return instance;
    }

    public void PrepareFolders(Instance instance)
    {
        foreach (var folder in new[] { "versions", "libraries", "assets", "natives", "mods", "resourcepacks", "shaderpacks", "config", "saves", "logs" })
            Directory.CreateDirectory(Path.Combine(instance.Folder, folder));
    }

    public void Delete(Instance instance)
    {
        if (Directory.Exists(instance.Folder))
            Directory.Delete(instance.Folder, true);
    }

    public AppSettings LoadSettings()
    {
        if (File.Exists(SettingsPath))
        {
            try
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json.Options) ?? new AppSettings();
            }
            catch (JsonException)
            {
            }
        }
        return new AppSettings();
    }

    public void SaveSettings(AppSettings settings)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Json.Options));
    }

    private static string Slug(string name)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(ch);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }
        var result = builder.ToString().Trim('-');
        return result.Length == 0 ? "instance" : result;
    }
}
