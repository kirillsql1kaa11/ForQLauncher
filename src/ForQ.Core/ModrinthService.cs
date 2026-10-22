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
        CancellationToken ct)
    {
        var facets = new List<List<string>> { new() { "project_type:" + projectType } };

        if (!string.IsNullOrEmpty(gameVersion))
            facets.Add(new List<string> { "versions:" + gameVersion });

        if (!string.IsNullOrEmpty(loader) && projectType == "mod")
            facets.Add(new List<string> { "categories:" + loader });

        var facetsJson = JsonSerializer.Serialize(facets);
        var url = $"{BaseUrl}/search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facetsJson)}&limit=50";

        return await _http.GetJsonAsync<ModrinthSearchResponse>(url, ct);
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

    public static ModrinthFile PrimaryFile(ModrinthVersion version)
        => version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault()
           ?? throw new InvalidDataException($"У версии {version.Name} нет файлов");
}
