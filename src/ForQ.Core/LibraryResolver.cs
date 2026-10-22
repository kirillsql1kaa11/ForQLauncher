using System.Runtime.InteropServices;
using System.Text.Json;

namespace ForQ.Core;

public sealed record ResolvedLibrary(
    GameLibrary Library,
    string? ClasspathFile,
    DownloadInfo? NativeDownload,
    string? NativeFile,
    ExtractInfo? Extract);

public static class RuleEvaluator
{
    public static bool HasCustomResolution { get; set; } = true;

    private static readonly Dictionary<string, bool> FeatureStates = new()
    {
        ["is_demo_user"] = false,
        ["has_quick_plays_support"] = false,
        ["is_quick_play_singleplayer"] = false,
        ["is_quick_play_multiplayer"] = false,
        ["is_quick_play_realms"] = false
    };

    public static bool FeatureState(string name) => name switch
    {
        "has_custom_resolution" => HasCustomResolution,
        _ => FeatureStates.TryGetValue(name, out var state) && state
    };

    public static string OsName()
    {
        if (OperatingSystem.IsWindows())
            return "windows";
        if (OperatingSystem.IsMacOS())
            return "osx";
        return "linux";
    }

    public static string OsArch()
    {
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "x86_64"
        };
    }

    public static bool IsAllowed(GameLibrary library)
    {
        if (library.Rules is null || library.Rules.Count == 0)
            return true;

        var allowed = false;
        foreach (var rule in library.Rules)
        {
            if (Matches(rule))
                allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static bool Matches(GameRule rule)
    {
        if (rule.Os is not null)
        {
            if (rule.Os.Name is not null && !string.Equals(rule.Os.Name, OsName(), StringComparison.OrdinalIgnoreCase))
                return false;
            if (rule.Os.Arch is not null && !string.Equals(rule.Os.Arch, OsArch(), StringComparison.OrdinalIgnoreCase))
                return false;
            if (rule.Os.Version is not null && !System.Text.RegularExpressions.Regex.IsMatch(Environment.OSVersion.VersionString, rule.Os.Version))
                return false;
        }

        if (rule.Features is not null)
        {
            if (rule.Features.IsDemoUser is { } demo && demo)
                return false;
            if (rule.Features.HasCustomResolution is { } custom && custom != HasCustomResolution)
                return false;
            if (rule.Features.Extra is not null)
            {
                foreach (var extra in rule.Features.Extra)
                {
                    var value = extra.Value.ValueKind == JsonValueKind.True;
                    if (value != FeatureState(extra.Key))
                        return false;
                }
            }
        }

        return true;
    }

    public static bool MatchesElement(System.Text.Json.JsonElement rule)
    {
        if (rule.TryGetProperty("os", out var os) && os.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (os.TryGetProperty("name", out var name) && name.GetString() is { } osName
                                                             && !string.Equals(osName, OsName(), StringComparison.OrdinalIgnoreCase))
                return false;
            if (os.TryGetProperty("arch", out var arch) && arch.GetString() is { } osArch
                                                             && !string.Equals(osArch, OsArch(), StringComparison.OrdinalIgnoreCase))
                return false;
            if (os.TryGetProperty("version", out var version) && version.GetString() is { } osVersion
                                                                     && !System.Text.RegularExpressions.Regex.IsMatch(Environment.OSVersion.VersionString, osVersion))
                return false;
        }

        if (rule.TryGetProperty("features", out var features) && features.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var feature in features.EnumerateObject())
            {
                var value = feature.Value.ValueKind == System.Text.Json.JsonValueKind.True;
                if (value != FeatureState(feature.Name))
                    return false;
            }
        }

        return true;
    }
}

public static class LibraryResolver
{
    public static string DirectoryFor(string mavenName)
    {
        var parts = mavenName.Split(':');
        if (parts.Length < 3)
            return "unknown";
        return Path.Combine(parts[0].Replace('.', Path.DirectorySeparatorChar), parts[1], parts[2]);
    }

    public static string FileNameFromUrl(string url)
    {
        var path = new Uri(url).AbsolutePath;
        var name = path[(path.LastIndexOf('/') + 1)..];
        return Uri.UnescapeDataString(name);
    }

    public static string PathFor(string mavenName, string fileName)
        => Path.Combine(DirectoryFor(mavenName), fileName);

    public static IReadOnlyList<ResolvedLibrary> Resolve(IEnumerable<GameLibrary> libraries)
    {
        var result = new List<ResolvedLibrary>();
        var osName = RuleEvaluator.OsName();

        foreach (var library in libraries)
        {
            if (!RuleEvaluator.IsAllowed(library))
                continue;

            var downloads = library.Downloads;
            if (downloads is null)
                continue;

            string? nativeClassifier = null;
            if (library.Natives is not null && library.Natives.TryGetValue(osName, out var classifier))
                nativeClassifier = classifier;

            DownloadInfo? nativeDownload = null;
            if (nativeClassifier is not null && downloads.Classifiers is not null
                                         && downloads.Classifiers.TryGetValue(nativeClassifier, out var found))
                nativeDownload = found;

            var main = downloads.Classpath ?? downloads.Artifact;
            if (main is null && nativeDownload is null)
                continue;

            var classpathFile = main is null ? null : PathFor(library.Name, FileNameFromUrl(main.Url));

            if (nativeDownload is not null)
            {
                result.Add(new ResolvedLibrary(
                    library,
                    classpathFile,
                    nativeDownload,
                    PathFor(library.Name, FileNameFromUrl(nativeDownload.Url)),
                    null));
            }
            else if (library.Extract is not null && main is not null)
            {
                result.Add(new ResolvedLibrary(
                    library,
                    classpathFile,
                    main,
                    PathFor(library.Name, FileNameFromUrl(main.Url)),
                    library.Extract));
            }
            else
            {
                result.Add(new ResolvedLibrary(library, classpathFile, null, null, null));
            }
        }

        return result;
    }
}
