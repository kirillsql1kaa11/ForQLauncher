using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ForQ.Core;

public sealed class HttpApi : IDisposable
{
    private readonly HttpClient _client;

    public HttpApi()
    {
        _client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("ForQLauncher/1.0");
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct)
        => await GetStringAsync(url, ct, null);

    public async Task<string> GetStringAsync(string url, CancellationToken ct, string? bearerToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<T> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        var json = await GetStringAsync(url, ct);
        return System.Text.Json.JsonSerializer.Deserialize<T>(json, Json.Options)
               ?? throw new InvalidDataException($"Пустой ответ: {url}");
    }

    public async Task<T> GetJsonAsync<T>(string url, CancellationToken ct, string? bearerToken)
    {
        var json = await GetStringAsync(url, ct, bearerToken);
        return System.Text.Json.JsonSerializer.Deserialize<T>(json, Json.Options)
               ?? throw new InvalidDataException($"Пустой ответ: {url}");
    }

    public async Task<T> PostJsonAsync<T>(string url, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return System.Text.Json.JsonSerializer.Deserialize<T>(json, Json.Options)
               ?? throw new InvalidDataException($"Пустой ответ: {url}");
    }

    public async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
    {
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task DownloadFileAsync(string url, string destination, long expectedSize, string expectedSha1, CancellationToken ct,
        Action<long>? onBytes = null, string? bearerToken = null)
    {
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(destination))
        {
            var info = new FileInfo(destination);
            var sizeOk = expectedSize <= 0 || info.Length == expectedSize;
            var hashOk = string.IsNullOrEmpty(expectedSha1) || Sha1Equals(destination, expectedSha1);
            if (sizeOk && hashOk)
                return;
            File.Delete(destination);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, true))
        {
            var buffer = new byte[1 << 16];
            while (true)
            {
                var read = await input.ReadAsync(buffer, ct);
                if (read == 0)
                    break;
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                onBytes?.Invoke(read);
            }
            await output.FlushAsync(ct);
        }

        if (!string.IsNullOrEmpty(expectedSha1) && !Sha1Equals(destination, expectedSha1))
        {
            File.Delete(destination);
            throw new InvalidDataException($"Контрольная сумма не совпала: {Path.GetFileName(destination)}");
        }
    }

    public void Dispose() => _client.Dispose();

    public static bool Sha1Equals(string path, string sha1)
    {
        if (string.IsNullOrEmpty(sha1))
            return true;
        using var stream = File.OpenRead(path);
        var hash = SHA1.HashData(stream);
        return Convert.ToHexString(hash).Equals(sha1, StringComparison.OrdinalIgnoreCase);
    }

    public static void ExtractJar(string jarPath, string targetDirectory, IEnumerable<string> excludePatterns)
    {
        var excludes = excludePatterns.Select(GlobToRegex).ToList();
        Directory.CreateDirectory(targetDirectory);

        using var archive = ZipFile.OpenRead(jarPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            var normalized = entry.FullName.Replace('\\', '/');
            if (excludes.Any(re => re.IsMatch(normalized)))
                continue;

            var destination = Path.Combine(targetDirectory, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            entry.ExtractToFile(destination, true);
        }
    }

    public static Regex GlobToRegex(string pattern)
    {
        var builder = new StringBuilder("^");
        foreach (var ch in pattern)
        {
            switch (ch)
            {
                case '*':
                    builder.Append(".*");
                    break;
                case '?':
                    builder.Append('.');
                    break;
                default:
                    builder.Append(System.Text.RegularExpressions.Regex.Escape(ch.ToString()));
                    break;
            }
        }
        builder.Append('$');
        return new System.Text.RegularExpressions.Regex(builder.ToString(),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);    }
}
