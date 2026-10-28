using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ForQ.Core;

public sealed class UpdateService
{
    public const string Repository = "kirillsql1kaa11/ForQLauncher";

    private readonly HttpApi _http;
    private readonly DownloadManager _downloads;

    public UpdateService(HttpApi http, DownloadManager downloads)
    {
        _http = http;
        _downloads = downloads;
    }

    public async Task<GitHubRelease?> GetLatestAsync(CancellationToken ct)
    {
        try
        {
            return await _http.GetJsonAsync<GitHubRelease>(
                $"https://api.github.com/repos/{Repository}/releases/latest", ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(int count, CancellationToken ct)
    {
        try
        {
            return await _http.GetJsonAsync<List<GitHubRelease>>(
                $"https://api.github.com/repos/{Repository}/releases?per_page={Math.Clamp(count, 1, 30)}", ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Array.Empty<GitHubRelease>();
        }
    }

    public static GitHubAsset? PickAsset(GitHubRelease release)
        => release.Assets.FirstOrDefault(a => a.Name.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase))
           ?? release.Assets.FirstOrDefault();

    public static bool IsNewer(string latest, string current)
    {
        var latestParts = VersionParts(latest);
        var currentParts = VersionParts(current);

        for (var index = 0; index < Math.Max(latestParts.Length, currentParts.Length); index++)
        {
            var left = index < latestParts.Length ? latestParts[index] : 0;
            var right = index < currentParts.Length ? currentParts[index] : 0;
            if (left != right)
                return left > right;
        }

        return false;
    }

    private static int[] VersionParts(string value)
    {
        var match = Regex.Match(value ?? "", @"\d+(\.\d+)*");
        return match.Success
            ? match.Value.Split('.').Select(part => int.TryParse(part, out var number) ? number : 0).ToArray()
            : new[] { 0 };
    }

    public async Task<string> DownloadAsync(GitHubAsset asset, IProgress<DownloadState>? progress, CancellationToken ct)
    {
        var destination = Path.Combine(Path.GetTempPath(), "ForQLauncher", "update", asset.Name);

        await _downloads.RunAsync(
            new[] { new DownloadItem(asset.BrowserDownloadUrl, destination, asset.Size, "", asset.Name) },
            1,
            progress,
            ct);

        return destination;
    }

    public void ApplyAndRestart(string downloadedPath)
    {
        var target = Environment.ProcessPath
                     ?? throw new InvalidOperationException("Не удалось определить путь лаунчера");

        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(downloadedPath), StringComparison.OrdinalIgnoreCase))
            return;

        var script = Path.Combine(Path.GetTempPath(), "ForQLauncher", "update", "apply-" + Guid.NewGuid().ToString("N")[..8] + ".cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);

        var lines = new[]
        {
            "@echo off",
            "timeout /t 2 /nobreak >nul",
            $":wait_{Environment.ProcessId}",
            $"tasklist /FI \"PID eq {Environment.ProcessId}\" 2>nul | find \"{Environment.ProcessId}\" >nul",
            $"if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait_{Environment.ProcessId})",
            $"copy /Y \"{downloadedPath}\" \"{target}\" >nul",
            $"start \"\" \"{target}\"",
            "del \"%~f0\""
        };

        File.WriteAllText(script, string.Join("\r\n", lines) + "\r\n");

        Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script)!
        });
    }
}
