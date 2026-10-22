using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace ForQ.App;

public static class ImageCache
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Gate = new(5, 5);

    public static async Task<Bitmap?> LoadAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        lock (Cache)
        {
            if (Cache.TryGetValue(url, out var cached))
                return cached;
        }

        await Gate.WaitAsync(ct);
        try
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(url, out var cached))
                    return cached;
            }

            var bitmap = await LoadCoreAsync(url, ct);
            lock (Cache)
                Cache[url] = bitmap;
            return bitmap;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<Bitmap?> LoadCoreAsync(string url, CancellationToken ct)
    {
        var directory = Path.Combine(AppServices.Current.CacheDirectory, "images");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32].ToLowerInvariant();
        var path = Path.Combine(directory, key);

        if (!File.Exists(path))
        {
            var bytes = await AppServices.Current.Http.GetBytesAsync(url, ct);
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(path, bytes, ct);
        }

        await using var stream = File.OpenRead(path);
        return new Bitmap(stream);
    }
}
