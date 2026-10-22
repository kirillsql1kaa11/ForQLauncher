using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForQ.Core;

public sealed record LaunchPlan(
    string JavaPath,
    IReadOnlyList<string> JvmArguments,
    IReadOnlyList<string> GameArguments,
    string MainClass,
    string WorkingDirectory);

public sealed class LaunchService
{
    private static readonly string[] LegacyGameArguments =
    {
        "--username", "${auth_player_name}",
        "--version", "${version_name}",
        "--gameDir", "${game_directory}",
        "--assetsDir", "${assets_root}",
        "--uuid", "${auth_uuid}",
        "--accessToken", "${auth_access_token}",
        "--userType", "${user_type}",
        "--versionType", "${version_type}"
    };

    private static readonly string[] LegacyJvmArguments =
    {
        "-Djava.library.path=${natives_directory}",
        "-Dminecraft.launcher.brand=${launcher_name}",
        "-Dminecraft.launcher.version=${launcher_version}",
        "-cp", "${classpath}"
    };

    private readonly MojangService _mojang;
    private readonly RuntimeService _runtime;
    private readonly DownloadManager _downloads;

    public LaunchService(MojangService mojang, RuntimeService runtime, DownloadManager downloads)
    {
        _mojang = mojang;
        _runtime = runtime;
        _downloads = downloads;
    }

    public static string OfflineUuid(string username)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }

    public async Task<List<GameVersion>> LoadChainAsync(Instance instance, CancellationToken ct)
    {
        var chain = new List<GameVersion>();
        var id = instance.ProfileId;

        while (!string.IsNullOrEmpty(id))
        {
            var path = Path.Combine(instance.Folder, "versions", id, id + ".json");
            if (!File.Exists(path))
                throw new InvalidOperationException($"Не найден файл версии: {id}.json");
            var version = JsonSerializer.Deserialize<GameVersion>(await File.ReadAllTextAsync(path, ct), Json.Options)
                          ?? throw new InvalidOperationException($"Не удалось прочитать версию {id}");
            chain.Add(version);
            id = version.InheritsFrom;
        }

        chain.Reverse();
        return chain;
    }

    public async Task<LaunchPlan> BuildPlanAsync(Instance instance, AppSettings settings, Action<string>? log, CancellationToken ct)
    {
        var chain = await LoadChainAsync(instance, ct);

        var libraries = new List<GameLibrary>();
        var index = new Dictionary<string, int>();
        var mainClass = "";
        GameArguments? arguments = null;
        JavaVersionRef? javaVersion = null;
        AssetIndexRef? assetIndex = null;

        foreach (var version in chain)
        {
            if (!string.IsNullOrEmpty(version.MainClass))
                mainClass = version.MainClass;
            if (version.Arguments is not null)
                arguments = version.Arguments;
            if (version.JavaVersion is not null)
                javaVersion = version.JavaVersion;
            if (version.AssetIndex is not null)
                assetIndex = version.AssetIndex;

            foreach (var library in version.Libraries)
            {
                if (index.TryGetValue(library.Name, out var position))
                    libraries[position] = library;
                else
                {
                    index[library.Name] = libraries.Count;
                    libraries.Add(library);
                }
            }
        }

        var librariesDirectory = Path.Combine(instance.Folder, "libraries");
        var resolved = LibraryResolver.Resolve(libraries);

        var missing = new List<DownloadItem>();
        foreach (var library in resolved)
        {
            if (library.ClasspathFile is not null)
            {
                var info = library.Library.Downloads?.Classpath ?? library.Library.Downloads?.Artifact;
                if (info is not null)
                {
                    var path = Path.Combine(librariesDirectory, library.ClasspathFile);
                    if (!File.Exists(path))
                        missing.Add(new DownloadItem(info.Url, path, info.Size, info.Sha1, library.Library.Name));
                }
            }

            if (library.NativeDownload is not null && library.NativeFile is not null)
            {
                var path = Path.Combine(librariesDirectory, library.NativeFile);
                if (!File.Exists(path))
                    missing.Add(new DownloadItem(library.NativeDownload.Url, path, library.NativeDownload.Size,
                        library.NativeDownload.Sha1, library.Library.Name));
            }
        }

        if (missing.Count > 0)
        {
            log?.Invoke($"Докачиваю {missing.Count} библиотек...");
            await _downloads.RunAsync(missing, 8, null, ct);
        }

        var nativeDirectories = ExtractNatives(instance, resolved, librariesDirectory);

        var classpath = new List<string>();
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var jar = Path.Combine(instance.Folder, "versions", chain[i].Id, chain[i].Id + ".jar");
            if (File.Exists(jar))
                classpath.Add(jar);
        }

        foreach (var library in resolved)
        {
            if (library.ClasspathFile is null)
                continue;
            var path = Path.Combine(librariesDirectory, library.ClasspathFile);
            if (File.Exists(path))
                classpath.Add(path);
        }

        var versionType = await _mojang.GetVersionTypeAsync(instance.MinecraftVersion, ct);
        var nativesRoot = Path.Combine(instance.Folder, "natives");
        var libraryPath = nativeDirectories.Count > 0
            ? string.Join(Path.PathSeparator, nativeDirectories)
            : nativesRoot;

        var placeholders = new Dictionary<string, string>
        {
            ["auth_player_name"] = instance.Username,
            ["version_name"] = instance.ProfileId,
            ["game_directory"] = instance.Folder,
            ["assets_root"] = Path.Combine(instance.Folder, "assets"),
            ["assets_index_name"] = assetIndex?.Id ?? "",
            ["auth_uuid"] = OfflineUuid(instance.Username),
            ["auth_access_token"] = "0",
            ["auth_session"] = "token:0:" + OfflineUuid(instance.Username),
            ["clientid"] = "",
            ["client_id"] = "",
            ["auth_xuid"] = "",
            ["user_type"] = "legacy",
            ["version_type"] = versionType,
            ["resolution_width"] = instance.Width.ToString(),
            ["resolution_height"] = instance.Height.ToString(),
            ["natives_directory"] = nativesRoot,
            ["library_directory"] = librariesDirectory,
            ["classpath"] = string.Join(Path.PathSeparator, classpath),
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["classpath_separator_1"] = Path.PathSeparator.ToString(),
            ["launcher_name"] = "ForQLauncher",
            ["launcher_version"] = "1.0"
        };

        var jvmArguments = new List<string> { "-Xmx" + instance.MemoryMb + "M" };
        if (arguments?.Jvm is { Count: > 0 })
            jvmArguments.AddRange(ExpandArguments(arguments.Jvm));
        else
            jvmArguments.AddRange(LegacyJvmArguments);

        if (settings.BlockTelemetry)
        {
            jvmArguments.Add("-Dhttp.proxyHost=127.0.0.1");
            jvmArguments.Add("-Dhttp.proxyPort=9");
            jvmArguments.Add("-Dhttps.proxyHost=127.0.0.1");
            jvmArguments.Add("-Dhttps.proxyPort=9");
        }

        var resolvedJvm = jvmArguments
            .Select(arg => arg.StartsWith("-Djava.library.path=", StringComparison.Ordinal)
                ? "-Djava.library.path=" + libraryPath
                : Substitute(arg, placeholders))
            .ToList();

        var gameArguments = arguments?.Game is { Count: > 0 }
            ? ExpandArguments(arguments.Game)
            : LegacyGameArguments.ToList();

        var resolvedGame = gameArguments.Select(arg => Substitute(arg, placeholders)).ToList();

        var javaPath = !string.IsNullOrEmpty(settings.JavaPath) && File.Exists(settings.JavaPath)
            ? settings.JavaPath
            : await _runtime.EnsureAsync(javaVersion?.Component ?? "jre-legacy", instance.RootPath, _downloads, null, ct);

        return new LaunchPlan(javaPath, resolvedJvm, resolvedGame, mainClass, instance.Folder);
    }

    public async Task<Process> StartAsync(LaunchPlan plan, Action<string>? log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(plan.JavaPath)
        {
            WorkingDirectory = plan.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in plan.JvmArguments)
            psi.ArgumentList.Add(argument);
        psi.ArgumentList.Add(plan.MainClass);
        foreach (var argument in plan.GameArguments)
            psi.ArgumentList.Add(argument);

        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log?.Invoke(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await Task.CompletedTask;
        return process;
    }

    public static IReadOnlyList<string> ExtractNatives(Instance instance, IEnumerable<ResolvedLibrary> libraries, string librariesDirectory)
    {
        var directories = new List<string>();

        foreach (var library in libraries)
        {
            if (library.NativeDownload is null || library.NativeFile is null)
                continue;

            var jar = Path.Combine(librariesDirectory, library.NativeFile);
            if (!File.Exists(jar))
                continue;

            var target = Path.Combine(instance.Folder, "natives", library.Library.Name.Replace(':', '_'));
            HttpApi.ExtractJar(jar, target, library.Extract?.Exclude ?? new List<string>());
            directories.Add(target);
        }

        return directories;
    }

    public static IReadOnlyList<string> ExpandArguments(List<JsonElement> items)
    {
        var result = new List<string>();

        foreach (var item in items)
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(item.GetString() ?? "");
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (item.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
            {
                var allowed = false;
                foreach (var rule in rules.EnumerateArray())
                {
                    if (!RuleEvaluator.MatchesElement(rule))
                        continue;
                    allowed = rule.TryGetProperty("action", out var action)
                              && string.Equals(action.GetString(), "allow", StringComparison.OrdinalIgnoreCase);
                }

                if (!allowed)
                    continue;
            }

            if (!item.TryGetProperty("value", out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                result.Add(value.GetString() ?? "");
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in value.EnumerateArray())
                    result.Add(entry.GetString() ?? "");
            }
        }

        return result;
    }

    public static string Substitute(string argument, IReadOnlyDictionary<string, string> placeholders)
    {
        if (!argument.Contains("${", StringComparison.Ordinal))
            return argument;

        var result = argument;
        foreach (var pair in placeholders)
            result = result.Replace("${" + pair.Key + "}", pair.Value, StringComparison.Ordinal);

        return System.Text.RegularExpressions.Regex.Replace(result, "\\$\\{[^}]*\\}", string.Empty);
    }
}
