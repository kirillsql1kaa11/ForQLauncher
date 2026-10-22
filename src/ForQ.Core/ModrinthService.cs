using System.Security.Cryptography;
using System.Text.Json;

namespace ForQ.Core;

public sealed class ModrinthService
{
    private const string BaseUrl = "https://api.modrinth.com/v2";

    private readonly HttpApi _http;

    public ModrinthService(HttpApi http) => _http = http;

    public async Task<ModrinthSearchResponse> SearchAsync(
        string query,
        string projectType,
        string gameVersion,
        string? loader,
        CancellationToken ct,
        int limit = 50,
        int offset = 0)
    {
        var facets = new List<List<string>> { new() { "project_type:" + projectType } };

        if (!string.IsNullOrEmpty(gameVersion))
            facets.Add(new List<string> { "versions:" + gameVersion });

        foreach (var tag in LoaderTags(projectType, loader))
            facets.Add(new List<string> { "categories:" + tag });

        return await SearchInternalAsync(query, facets, limit, offset, null, ct);
    }

    public async Task<ModrinthSearchResponse> SearchByCategoryAsync(
        string projectType,
        IEnumerable<string> categories,
        string gameVersion,
        string? loader,
        CancellationToken ct,
        int limit = 8)
    {
        var facets = new List<List<string>> { new() { "project_type:" + projectType } };
        var categoryFacet = categories.Select(c => "categories:" + c).ToList();
        if (categoryFacet.Count > 0)
            facets.Add(categoryFacet);

        if (!string.IsNullOrEmpty(gameVersion))
            facets.Add(new List<string> { "versions:" + gameVersion });

        foreach (var tag in LoaderTags(projectType, loader))
            facets.Add(new List<string> { "categories:" + tag });

        return await SearchInternalAsync("", facets, limit, 0, "downloads", ct);
    }

    private async Task<ModrinthSearchResponse> SearchInternalAsync(
        string query,
        List<List<string>> facets,
        int limit,
        int offset,
        string? index,
        CancellationToken ct)
    {
        var facetsJson = JsonSerializer.Serialize(facets);
        var url = $"{BaseUrl}/search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facetsJson)}&limit={limit}&offset={offset}";
        if (!string.IsNullOrEmpty(index))
            url += "&index=" + index;

        return await _http.GetJsonAsync<ModrinthSearchResponse>(url, ct);
    }

    private static IEnumerable<string> LoaderTags(string projectType, string? loader)
    {
        if (string.IsNullOrEmpty(loader) || projectType != "mod")
            return Array.Empty<string>();

        return loader == "quilt" ? new[] { "quilt", "fabric" } : new[] { loader };
    }

    public async Task<ModrinthProjectDetails> GetProjectAsync(string projectId, CancellationToken ct)
        => await _http.GetJsonAsync<ModrinthProjectDetails>($"{BaseUrl}/project/{projectId}", ct);

    public async Task<List<ModrinthProjectDetails>> GetProjectsAsync(IEnumerable<string> projectIds, CancellationToken ct)
    {
        var result = new List<ModrinthProjectDetails>();
        var ids = projectIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();

        foreach (var chunk in ids.Chunk(50))
        {
            var json = JsonSerializer.Serialize(chunk);
            var projects = await _http.GetJsonAsync<List<ModrinthProjectDetails>>(
                $"{BaseUrl}/projects?ids={Uri.EscapeDataString(json)}", ct);
            result.AddRange(projects);
        }

        return result;
    }

    public async Task<List<ModrinthVersion>> GetVersionsAsync(string projectId, CancellationToken ct)
        => await _http.GetJsonAsync<List<ModrinthVersion>>($"{BaseUrl}/project/{projectId}/version", ct);

    public async Task<ModrinthVersion?> PickVersionAsync(
        string projectId,
        string gameVersion,
        string? loader,
        CancellationToken ct)
    {
        var versions = await GetVersionsAsync(projectId, ct);

        var candidates = versions
            .Where(v => v.GameVersions.Count == 0 || v.GameVersions.Contains(gameVersion))
            .ToList();

        if (!string.IsNullOrEmpty(loader))
        {
            var withLoader = candidates.Where(v => v.Loaders.Contains(loader)).ToList();
            if (withLoader.Count > 0)
                candidates = withLoader;
        }

        return candidates
            .OrderByDescending(v => v.VersionType == "release")
            .ThenByDescending(v => v.DatePublished)
            .FirstOrDefault();
    }

    public async Task<ModrinthVersion?> PickVersionAsync(
        string projectId,
        string gameVersion,
        string? loader,
        string projectType,
        CancellationToken ct)
    {
        var effectiveLoader = projectType == "mod" ? loader : null;
        return await PickVersionAsync(projectId, gameVersion, effectiveLoader, ct);
    }

    public async Task<Dictionary<string, ModrinthVersion>> GetVersionsByHashesAsync(
        IEnumerable<string> hashes,
        CancellationToken ct)
    {
        var clean = hashes.Where(h => !string.IsNullOrEmpty(h)).Select(h => h.ToLowerInvariant()).Distinct().ToList();
        if (clean.Count == 0)
            return new Dictionary<string, ModrinthVersion>();

        var body = JsonSerializer.Serialize(new { hashes = clean, algorithm = "sha1" });
        return await _http.PostJsonAsync<Dictionary<string, ModrinthVersion>>($"{BaseUrl}/version_files", body, ct);
    }

    public async Task<Dictionary<string, ModrinthVersion>> GetLatestVersionsForHashesAsync(
        IEnumerable<string> hashes,
        string gameVersion,
        string? loader,
        CancellationToken ct)
    {
        var clean = hashes.Where(h => !string.IsNullOrEmpty(h)).Select(h => h.ToLowerInvariant()).Distinct().ToList();
        if (clean.Count == 0)
            return new Dictionary<string, ModrinthVersion>();

        var payload = new Dictionary<string, object>
        {
            ["hashes"] = clean,
            ["algorithm"] = "sha1"
        };

        if (!string.IsNullOrEmpty(gameVersion))
            payload["game_versions"] = new[] { gameVersion };

        if (!string.IsNullOrEmpty(loader))
            payload["loaders"] = new[] { loader };

        var body = JsonSerializer.Serialize(payload);
        return await _http.PostJsonAsync<Dictionary<string, ModrinthVersion>>($"{BaseUrl}/version_files/update", body, ct);
    }

    public static ModrinthFile PrimaryFile(ModrinthVersion version)
        => version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault()
           ?? throw new InvalidDataException($"У версии {version.Name} нет файлов");

    public static string Sha1Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }
}
