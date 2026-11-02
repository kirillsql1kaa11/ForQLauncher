using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForQ.Core;

public sealed record ElyProfile(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

public sealed class ElyAuthResponse
{
    [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("clientToken")] public string ClientToken { get; set; } = "";
    [JsonPropertyName("selectedProfile")] public ElyProfile SelectedProfile { get; set; } = new("", "");
}

public sealed record ElySession(
    string Name,
    string Uuid,
    string AccessToken,
    string InjectorPath,
    bool Authenticated);

public sealed class ElyService
{
    public const string AuthBase = "https://authserver.ely.by";
    public const string InjectorServer = "https://authserver.ely.by";
    public const string SkinsPage = "https://ely.by/skins";

    private const string InjectorMetaUrl = "https://authlib-injector.yushi.moe/artifact/latest.json";

    private readonly HttpApi _http;

    public ElyService(HttpApi http)
    {
        _http = http;
    }

    public static string InjectorPath(string root) => Path.Combine(root, "shared", "authlib-injector.jar");

    public static string Dashed(string uuid)
    {
        var hex = uuid.Replace("-", "");
        if (hex.Length != 32)
            return uuid;
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}".ToLowerInvariant();
    }

    public async Task<ElyAuthResponse> AuthenticateAsync(string username, string password, string clientToken, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            username,
            password,
            clientToken,
            requestUser = false
        }, Json.Options);

        return await _http.PostJsonAsync<ElyAuthResponse>($"{AuthBase}/auth/authenticate", body, ct);
    }

    public async Task<ElyAuthResponse> RefreshAsync(string accessToken, string clientToken, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            accessToken,
            clientToken,
            requestUser = false
        }, Json.Options);

        return await _http.PostJsonAsync<ElyAuthResponse>($"{AuthBase}/auth/refresh", body, ct);
    }

    public async Task InvalidateAsync(string accessToken, string clientToken, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { accessToken, clientToken }, Json.Options);
        try
        {
            await _http.PostJsonAsync<JsonElement>($"{AuthBase}/auth/invalidate", body, ct);
        }
        catch (InvalidDataException)
        {
        }
    }

    public async Task<string?> LookupUuidAsync(string nickname, CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"{AuthBase}/api/users/profiles/minecraft/{Uri.EscapeDataString(nickname)}", ct);
            if (string.IsNullOrWhiteSpace(json))
                return null;
            var profile = JsonSerializer.Deserialize<ElyProfile>(json, Json.Options);
            return string.IsNullOrEmpty(profile?.Id) ? null : Dashed(profile.Id);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    public async Task<string> EnsureInjectorAsync(string root, Action<string>? log, CancellationToken ct)
    {
        var path = InjectorPath(root);
        if (File.Exists(path) && new FileInfo(path).Length > 0)
            return path;

        log?.Invoke("Ely.by: скачиваю authlib-injector...");
        var meta = await _http.GetJsonAsync<InjectorMeta>(InjectorMetaUrl, ct);
        if (string.IsNullOrEmpty(meta.DownloadUrl))
            throw new InvalidOperationException("Не удалось определить ссылку на authlib-injector");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await _http.DownloadFileAsync(meta.DownloadUrl, path, 0, "", ct);
        return path;
    }

    private sealed class InjectorMeta
    {
        [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
    }
}
