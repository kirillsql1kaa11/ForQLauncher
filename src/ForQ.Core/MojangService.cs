using System.Runtime.InteropServices;
using System.Text.Json;

namespace ForQ.Core;

public sealed class MojangService
{
    public const string ManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    private readonly HttpApi _http;
    private readonly Dictionary<string, GameVersion> _cache = new();
    private readonly Dictionary<string, string> _rawCache = new();
    private VersionManifest? _manifest;

    public MojangService(HttpApi http) => _http = http;

    public async Task<VersionManifest> GetManifestAsync(CancellationToken ct)
    {
        _manifest ??= await _http.GetJsonAsync<VersionManifest>(ManifestUrl, ct);
        return _manifest;
    }

    public async Task<string> GetVersionRawAsync(string id, CancellationToken ct)
    {
        if (_rawCache.TryGetValue(id, out var cached))
            return cached;

        var manifest = await GetManifestAsync(ct);
        var entry = manifest.Versions.FirstOrDefault(v => v.Id == id)
                    ?? throw new InvalidOperationException($"Версия {id} не найдена в манифесте");
        var raw = await _http.GetStringAsync(entry.Url, ct);
        _rawCache[id] = raw;
        return raw;
    }

    public async Task<GameVersion> GetVersionAsync(string id, CancellationToken ct)
    {
        if (_cache.TryGetValue(id, out var cached))
            return cached;

        var manifest = await GetManifestAsync(ct);
        var entry = manifest.Versions.FirstOrDefault(v => v.Id == id)
                    ?? throw new InvalidOperationException($"Версия {id} не найдена в манифесте");
        var version = await _http.GetJsonAsync<GameVersion>(entry.Url, ct);
        _cache[id] = version;
        return version;
    }

    public Task<GameVersion> GetVersionByUrlAsync(string url, CancellationToken ct)
        => _http.GetJsonAsync<GameVersion>(url, ct);

    public async Task<string> GetVersionTypeAsync(string id, CancellationToken ct)
    {
        var manifest = await GetManifestAsync(ct);
        return manifest.Versions.FirstOrDefault(v => v.Id == id)?.Type ?? "release";
    }
}

public sealed class AssetService
{
    private readonly HttpApi _http;

    public AssetService(HttpApi http) => _http = http;

    public async Task InstallAsync(string assetsRoot, AssetIndexRef index, DownloadManager downloads, IProgress<DownloadState>? progress, CancellationToken ct)
    {
        var indexPath = Path.Combine(assetsRoot, "indexes", index.Id + ".json");
        await _http.DownloadFileAsync(index.Url, indexPath, index.Size, index.Sha1, ct);

        var items = new List<DownloadItem>();
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath, ct));
        if (!document.RootElement.TryGetProperty("objects", out var objects))
            return;

        foreach (var entry in objects.EnumerateObject())
        {
            var hash = entry.Value.GetProperty("hash").GetString() ?? "";
            var size = entry.Value.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
            if (hash.Length < 2)
                continue;
            var path = Path.Combine(assetsRoot, "objects", hash[..2], hash);
            items.Add(new DownloadItem($"https://resources.download.minecraft.net/{hash[..2]}/{hash}", path, size, hash));
        }

        await downloads.RunAsync(items, 8, progress, ct);
    }
}

public sealed class RuntimeService
{
    private const string ProductsUrl = "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    private readonly HttpApi _http;

    public RuntimeService(HttpApi http) => _http = http;

    public static string PlatformKey()
    {
        if (OperatingSystem.IsWindows())
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X86 => "windows-x86",
                Architecture.Arm64 => "windows-arm64",
                _ => "windows-x64"
            };
        if (OperatingSystem.IsMacOS())
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "mac-os-arm64" : "mac-os";
        return RuntimeInformation.OSArchitecture == Architecture.X86 ? "linux-i386" : "linux";
    }

    public static string JavaExecutable(string runtimeDirectory)
    {
        var name = OperatingSystem.IsWindows() ? "java.exe" : "java";
        return Path.Combine(runtimeDirectory, "bin", name);
    }

    public async Task<string> EnsureAsync(string component, string root, DownloadManager downloads, IProgress<DownloadState>? progress, CancellationToken ct)
    {
        var platform = PlatformKey();
        var target = Path.Combine(root, "runtime", component, platform);
        var executable = JavaExecutable(target);
        if (File.Exists(executable))
            return executable;

        var products = await _http.GetJsonAsync<RuntimeProducts>(ProductsUrl, ct);
        if (!products.Platforms.TryGetValue(platform, out var platformElement))
            throw new InvalidOperationException($"Нет рантайма для платформы {platform}");

        var runtimes = platformElement.Deserialize<Dictionary<string, List<RuntimeEntry>>>(Json.Options)
                       ?? throw new InvalidOperationException("Пустой список рантаймов");
        if (!runtimes.TryGetValue(component, out var entries) || entries.Count == 0)
            throw new InvalidOperationException($"Нет компонента {component}");

        var entry = entries[0];

        var manifest = await _http.GetJsonAsync<RuntimeManifest>(entry.Manifest.Url, ct);
        var items = new List<DownloadItem>();
        foreach (var file in manifest.Files)
        {
            if (file.Value.Type == "directory")
                continue;
            var download = file.Value.Downloads.Raw ?? file.Value.Downloads.Lzma;
            if (download is null)
                continue;
            var path = Path.Combine(target, file.Key.Replace('/', Path.DirectorySeparatorChar));
            items.Add(new DownloadItem(download.Url, path, download.Size, download.Sha1, file.Key));
        }

        await downloads.RunAsync(items, 8, progress, ct);

        if (!File.Exists(executable))
            throw new InvalidOperationException($"Java не найдена после распаковки: {executable}");

        return executable;
    }
}
