using System.Xml.Linq;

namespace ForQ.Core;

public sealed class LoaderService
{
    private const string FabricMeta = "https://meta.fabricmc.net/v2";
    private const string QuiltMeta = "https://meta.quiltmc.org/v3";
    private const string NeoForgeMaven = "https://maven.neoforged.net/releases/net/neoforged/neoforge";

    private readonly HttpApi _http;

    public LoaderService(HttpApi http) => _http = http;

    public async Task<List<LoaderBuild>> GetBuildsAsync(LoaderKind kind, string minecraftVersion, CancellationToken ct)
    {
        return kind switch
        {
            LoaderKind.Fabric => await GetFabricAsync(minecraftVersion, ct),
            LoaderKind.Quilt => await GetQuiltAsync(minecraftVersion, ct),
            LoaderKind.NeoForge => await GetNeoForgeAsync(minecraftVersion, ct),
            _ => new List<LoaderBuild>()
        };
    }

    public async Task<GameVersion> BuildProfileAsync(LoaderKind kind, string minecraftVersion, string loaderVersion, CancellationToken ct)
    {
        return kind switch
        {
            LoaderKind.Fabric => await BuildFabricAsync(minecraftVersion, loaderVersion, ct),
            LoaderKind.Quilt => await BuildQuiltAsync(minecraftVersion, loaderVersion, ct),
            _ => throw new NotSupportedException($"Профиль {kind} создаётся установщиком")
        };
    }

    private async Task<List<LoaderBuild>> GetFabricAsync(string minecraftVersion, CancellationToken ct)
    {
        var entries = await _http.GetJsonAsync<List<FabricLoaderEntry>>(
            $"{FabricMeta}/versions/loader/{minecraftVersion}", ct);

        return entries
            .Select(e => new LoaderBuild { Version = e.Loader.Version, Stable = e.Loader.Stable })
            .OrderByDescending(b => b.Stable)
            .ThenByDescending(b => ParseVersion(b.Version))
            .ToList();
    }

    private async Task<List<LoaderBuild>> GetQuiltAsync(string minecraftVersion, CancellationToken ct)
    {
        var entries = await _http.GetJsonAsync<List<QuiltLoaderEntry>>(
            $"{QuiltMeta}/versions/loader/{minecraftVersion}", ct);

        return entries
            .Select(e => new LoaderBuild { Version = e.Loader.Version, Stable = IsStableName(e.Loader.Version) })
            .OrderByDescending(b => b.Stable)
            .ThenByDescending(b => ParseVersion(b.Version))
            .ToList();
    }

    private async Task<List<LoaderBuild>> GetNeoForgeAsync(string minecraftVersion, CancellationToken ct)
    {
        var xml = await _http.GetStringAsync($"{NeoForgeMaven}/maven-metadata.xml", ct);
        var document = XDocument.Parse(xml);
        var versions = document.Root!
            .Elements("versioning")
            .Elements("versions")
            .Elements("version")
            .Select(e => e.Value)
            .Where(v => MatchesNeoForge(minecraftVersion, v))
            .ToList();

        return versions
            .Select(v => new LoaderBuild { Version = v, Stable = IsStableName(v) })
            .OrderByDescending(b => b.Stable)
            .ThenByDescending(b => ParseVersion(b.Version))
            .ToList();
    }

    private static bool MatchesNeoForge(string minecraftVersion, string neoForgeVersion)
    {
        var line = NeoForgeLine(minecraftVersion);
        if (line is null)
            return false;

        return neoForgeVersion.StartsWith(line + ".", StringComparison.OrdinalIgnoreCase);
    }

    public static string? NeoForgeLine(string minecraftVersion)
    {
        if (minecraftVersion.StartsWith("1.20.", StringComparison.Ordinal))
            return "20." + minecraftVersion[5..];
        if (minecraftVersion.StartsWith("1.21.", StringComparison.Ordinal))
            return "21." + minecraftVersion[5..];
        if (minecraftVersion.StartsWith("26.", StringComparison.Ordinal))
        {
            var parts = minecraftVersion.Split('.');
            return parts.Length >= 2 ? "26." + parts[1] : null;
        }

        return null;
    }

    private async Task<GameVersion> BuildFabricAsync(string minecraftVersion, string loaderVersion, CancellationToken ct)
    {
        var entries = await _http.GetJsonAsync<List<FabricLoaderEntry>>(
            $"{FabricMeta}/versions/loader/{minecraftVersion}", ct);
        var entry = entries.FirstOrDefault(e => e.Loader.Version == loaderVersion)
                    ?? throw new InvalidOperationException($"Fabric {loaderVersion} не найден");

        var profile = new GameVersion
        {
            Id = $"fabric-loader-{loaderVersion}-{minecraftVersion}",
            Type = "release",
            InheritsFrom = minecraftVersion,
            MainClass = entry.LauncherMeta?.ClientMainClass("net.fabricmc.loader.impl.launch.knot.KnotClient")
                        ?? "net.fabricmc.loader.impl.launch.knot.KnotClient"
        };

        profile.Libraries.Add(MavenLibrary(entry.Loader.Maven, MavenBase(entry.Loader.Maven)));

        if (!string.IsNullOrEmpty(entry.Intermediary?.Maven))
            profile.Libraries.Add(MavenLibrary(entry.Intermediary!.Maven, MavenBase(entry.Intermediary.Maven)));

        foreach (var library in entry.LauncherMeta?.Libraries.Common ?? new List<LoaderLibrary>())
            profile.Libraries.Add(MavenLibrary(library));

        foreach (var library in entry.LauncherMeta?.Libraries.Client ?? new List<LoaderLibrary>())
            profile.Libraries.Add(MavenLibrary(library));

        return profile;
    }

    private async Task<GameVersion> BuildQuiltAsync(string minecraftVersion, string loaderVersion, CancellationToken ct)
    {
        var entries = await _http.GetJsonAsync<List<QuiltLoaderEntry>>(
            $"{QuiltMeta}/versions/loader/{minecraftVersion}", ct);
        var entry = entries.FirstOrDefault(e => e.Loader.Version == loaderVersion)
                    ?? throw new InvalidOperationException($"Quilt {loaderVersion} не найден");

        var profile = new GameVersion
        {
            Id = $"quilt-loader-{loaderVersion}-{minecraftVersion}",
            Type = "release",
            InheritsFrom = minecraftVersion,
            MainClass = entry.LauncherMeta?.ClientMainClass("org.quiltmc.loader.impl.launch.knot.KnotClient")
                        ?? "org.quiltmc.loader.impl.launch.knot.KnotClient"
        };

        profile.Libraries.Add(MavenLibrary(entry.Loader.Maven, MavenBase(entry.Loader.Maven)));

        if (!string.IsNullOrEmpty(entry.Intermediary?.Maven))
            profile.Libraries.Add(MavenLibrary(entry.Intermediary!.Maven, MavenBase(entry.Intermediary.Maven)));

        foreach (var library in entry.LauncherMeta?.Libraries.Common ?? new List<LoaderLibrary>())
            profile.Libraries.Add(MavenLibrary(library));

        foreach (var library in entry.LauncherMeta?.Libraries.Client ?? new List<LoaderLibrary>())
            profile.Libraries.Add(MavenLibrary(library));

        return profile;
    }

    public static GameLibrary MavenLibrary(string maven, string baseUrl, string? sha1 = null, long? size = null)
    {
        var parts = maven.Split(':');
        if (parts.Length < 3)
            throw new ArgumentException($"Некорректные maven-координаты: {maven}");

        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        var fileName = $"{artifact}-{version}.jar";
        var url = baseUrl.TrimEnd('/') + "/" + group.Replace('.', '/') + "/" + artifact + "/"
                  + version + "/" + fileName;

        return new GameLibrary
        {
            Name = maven,
            Downloads = new LibraryDownloads
            {
                Artifact = new DownloadInfo { Url = url, Size = size ?? 0, Sha1 = sha1 ?? "" }
            }
        };
    }

    private static GameLibrary MavenLibrary(LoaderLibrary library)
        => MavenLibrary(library.Name, library.Url, library.Sha1, library.Size);

    public static string MavenBase(string maven)
    {
        var group = maven.Split(':')[0];
        return group switch
        {
            "net.fabricmc" => "https://maven.fabricmc.net/",
            "org.quiltmc" => "https://maven.quiltmc.org/repository/release/",
            _ => "https://maven.neoforged.net/releases/"
        };
    }

    public static string NeoForgeInstallerUrl(string version)
        => $"{NeoForgeMaven}/{version}/neoforge-{version}-installer.jar";

    private static bool IsStableName(string version)
    {
        var lower = version.ToLowerInvariant();
        return !lower.Contains("beta") && !lower.Contains("alpha") && !lower.Contains("rc") && !lower.Contains("snapshot");
    }

    private static Version ParseVersion(string text)
    {
        var digits = new string(text.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        var parts = digits.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out var value) ? value : 0)
            .ToArray();
        return parts.Length switch
        {
            0 => new Version(0, 0),
            1 => new Version(parts[0], 0),
            2 => new Version(parts[0], parts[1]),
            3 => new Version(parts[0], parts[1], parts[2]),
            _ => new Version(parts[0], parts[1], parts[2], parts[3])
        };
    }
}
