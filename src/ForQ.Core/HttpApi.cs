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

    public DownloadControl Downloads { get; } = new();

    private const int MaxAttempts = 3;

    public async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var response = await SendGetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static HttpRequestMessage CreateGet(string url)
        => new(HttpMethod.Get, url);

    private static bool IsTransient(Exception ex)
    {
        if (ex is IOException)
            return true;
        if (ex is not HttpRequestException http)
            return false;
        if (http.StatusCode is null)
            return true;
        var code = (int)http.StatusCode.Value;
        return code == 408 || code == 429 || code >= 500;
    }

    private async Task<HttpResponseMessage> SendGetAsync(string url, HttpCompletionOption option, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = CreateGet(url);
            try
            {
                return await _client.SendAsync(request, option, ct);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                await Task.Delay(400 * attempt, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new HttpRequestException($"{url}: {ex.Message}", ex);
            }
        }
    }

    public async Task<T> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        var json = await GetStringAsync(url, ct);
        return System.Text.Json.JsonSerializer.Deserialize<T>(json, Json.Options)
               ?? throw new InvalidDataException($"Пустой ответ: {url}");
    }

    public async Task<T> PostJsonAsync<T>(string url, string body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            try
            {
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct);
                return System.Text.Json.JsonSerializer.Deserialize<T>(json, Json.Options)
                       ?? throw new InvalidDataException($"Пустой ответ: {url}");
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                await Task.Delay(400 * attempt, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new HttpRequestException($"{url}: {ex.Message}", ex);
            }
        }
    }

    public async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
    {
        using var response = await SendGetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task DownloadFileAsync(string url, string destination, long expectedSize, string expectedSha1, CancellationToken ct,
        Action<long>? onBytes = null)
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

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(url, destination, expectedSha1, ct, onBytes);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                if (File.Exists(destination))
                    File.Delete(destination);
                await Task.Delay(400 * attempt, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                if (File.Exists(destination))
                    File.Delete(destination);
                if (ex is HttpRequestException http)
                    throw new HttpRequestException($"{url}: {http.Message}", http);
                throw new IOException($"{url}: {ex.Message}", ex);
            }
        }
    }

    private async Task DownloadOnceAsync(string url, string destination, string expectedSha1, CancellationToken ct,
        Action<long>? onBytes)
    {
        long reported = 0;
        var completed = false;
        try
        {
            using var request = CreateGet(url);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, true))
            {
                var buffer = new byte[1 << 16];
                while (true)
                {
                    await Downloads.WaitIfPausedAsync(ct);
                    var read = await input.ReadAsync(buffer, ct);
                    if (read == 0)
                        break;
                    await Downloads.ThrottleAsync(read, ct);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    onBytes?.Invoke(read);
                    reported += read;
                }
                await output.FlushAsync(ct);
            }

            if (!string.IsNullOrEmpty(expectedSha1) && !Sha1Equals(destination, expectedSha1))
            {
                File.Delete(destination);
                throw new InvalidDataException($"Контрольная сумма не совпала: {Path.GetFileName(destination)}");
            }

            completed = true;
        }
        finally
        {
            if (!completed && reported > 0)
                onBytes?.Invoke(-reported);
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
