using System.Security.Cryptography;

namespace ForQ.Core;

public sealed record DownloadItem(string Url, string Path, long Size = 0, string Sha1 = "", string Name = "");

public sealed record DownloadState(string Name, long Done, long Total, bool Completed);

public sealed class DownloadManager
{
    private readonly HttpApi _http;

    public DownloadManager(HttpApi http) => _http = http;

    public async Task RunAsync(
        IReadOnlyList<DownloadItem> items,
        int parallelism,
        IProgress<DownloadState>? progress,
        CancellationToken ct)
    {
        var total = items.Sum(i => i.Size);
        var done = 0L;
        var queue = new Queue<DownloadItem>(items.DistinctBy(i => i.Path, StringComparer.OrdinalIgnoreCase));
        var errors = new List<Exception>();

        DownloadItem? Next()
        {
            lock (queue)
                return queue.Count > 0 ? queue.Dequeue() : null;
        }

        async Task Worker()
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var item = Next();
                if (item is null)
                    break;
                var name = item.Name.Length > 0 ? item.Name : Path.GetFileName(item.Path);
                try
                {
                    await _http.DownloadFileAsync(item.Url, item.Path, item.Size, item.Sha1, ct, bytes =>
                    {
                        var current = Interlocked.Add(ref done, bytes);
                        progress?.Report(new DownloadState(name, current, total, false));
                    });
                    progress?.Report(new DownloadState(name, done, total, true));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add(new DownloadException(item, ex));
                }
            }
        }

        var workers = new List<Task>();
        var count = Math.Clamp(parallelism, 1, 16);
        for (var i = 0; i < count; i++)
            workers.Add(Task.Run(Worker, ct));

        await Task.WhenAll(workers);

        if (errors.Count > 0)
            throw new AggregateException(errors);
    }
}

public sealed class DownloadException : Exception
{
    public DownloadException(DownloadItem item, Exception inner)
        : base($"Не удалось скачать {item.Name} ({item.Url}): {inner.Message}", inner)
    {
    }
}
