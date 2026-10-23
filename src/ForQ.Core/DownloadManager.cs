using System.Diagnostics;

namespace ForQ.Core;

public sealed record DownloadItem(string Url, string Path, long Size = 0, string Sha1 = "", string Name = "");

public sealed record DownloadState(
    string Name,
    long Done,
    long Total,
    bool Completed,
    double BytesPerSecond = 0,
    TimeSpan? Eta = null,
    bool Paused = false,
    int FilesDone = 0,
    int FilesTotal = 0);

public sealed class DownloadControl
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TaskCompletionSource<bool>? _resume;
    private bool _paused;
    private long _speedLimit;
    private double _rateStarted;
    private long _rateBytes;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
                return _paused;
        }
    }

    public long SpeedLimitBytesPerSecond
    {
        get
        {
            lock (_gate)
                return _speedLimit;
        }
        set
        {
            lock (_gate)
            {
                _speedLimit = Math.Max(0, value);
                ResetThrottleUnsafe();
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_paused)
                return;

            _paused = true;
            _resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource<bool>? resume;
        lock (_gate)
        {
            if (!_paused)
                return;

            _paused = false;
            resume = _resume;
            _resume = null;
            ResetThrottleUnsafe();
        }

        resume?.TrySetResult(true);
    }

    public void ResetThrottle()
    {
        lock (_gate)
            ResetThrottleUnsafe();
    }

    public async ValueTask WaitIfPausedAsync(CancellationToken ct)
    {
        while (true)
        {
            Task? wait;
            lock (_gate)
            {
                if (!_paused)
                    return;
                wait = _resume?.Task;
            }

            if (wait is null)
                return;

            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public async ValueTask ThrottleAsync(int bytes, CancellationToken ct)
    {
        if (bytes <= 0)
            return;

        double delayMs;
        lock (_gate)
        {
            if (_speedLimit <= 0)
            {
                ResetThrottleUnsafe();
                return;
            }

            var now = _clock.Elapsed.TotalSeconds;
            if (_rateStarted <= 0)
            {
                _rateStarted = now;
                _rateBytes = 0;
            }

            _rateBytes += bytes;
            var target = _rateBytes / (double)_speedLimit;
            delayMs = Math.Max(0, (target - (now - _rateStarted)) * 1000);
        }

        while (delayMs > 0)
        {
            var slice = Math.Min(delayMs, 100);
            await Task.Delay(TimeSpan.FromMilliseconds(slice), ct).ConfigureAwait(false);
            await WaitIfPausedAsync(ct).ConfigureAwait(false);
            delayMs -= slice;
        }
    }

    private void ResetThrottleUnsafe()
    {
        _rateStarted = 0;
        _rateBytes = 0;
    }
}

public sealed class DownloadManager
{
    private readonly HttpApi _http;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _reportGate = new();
    private int _active;
    private long _done;
    private long _total;
    private int _filesDone;
    private int _filesTotal;

    public DownloadManager(HttpApi http) => _http = http;

    public bool IsActive => Volatile.Read(ref _active) == 1;

    public bool IsPaused => _http.Downloads.IsPaused;

    public long SpeedLimitBytesPerSecond
    {
        get => _http.Downloads.SpeedLimitBytesPerSecond;
        set => _http.Downloads.SpeedLimitBytesPerSecond = value;
    }

    public void Pause() => _http.Downloads.Pause();

    public void Resume() => _http.Downloads.Resume();

    public async Task RunAsync(
        IReadOnlyList<DownloadItem> items,
        int parallelism,
        IProgress<DownloadState>? progress,
        CancellationToken ct)
    {
        var queueItems = items
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _http.Downloads.ResetThrottle();
            _http.Downloads.Resume();

            Interlocked.Exchange(ref _done, 0);
            Interlocked.Exchange(ref _total, queueItems.Sum(item => Math.Max(0, item.Size)));
            Interlocked.Exchange(ref _filesDone, 0);
            Interlocked.Exchange(ref _filesTotal, queueItems.Count);
            Interlocked.Exchange(ref _active, 1);

            var queue = new Queue<DownloadItem>(queueItems);
            var errors = new List<Exception>();
            var lastBytes = 0L;
            var lastTick = Stopwatch.GetTimestamp();

            DownloadItem? Next()
            {
                lock (queue)
                    return queue.Count > 0 ? queue.Dequeue() : null;
            }

            void Report(string name, bool force)
            {
                if (progress is null)
                    return;

                lock (_reportGate)
                {
                    var now = Stopwatch.GetTimestamp();
                    var elapsed = (now - lastTick) / (double)Stopwatch.Frequency;
                    if (!force && elapsed < 0.15)
                        return;

                    var done = Interlocked.Read(ref _done);
                    var total = Interlocked.Read(ref _total);
                    var speed = elapsed > 0 ? Math.Max(0, (done - lastBytes) / elapsed) : 0;
                    var eta = total > 0 && done < total && speed > 1
                        ? TimeSpan.FromSeconds((total - done) / speed)
                        : (TimeSpan?)null;

                    lastBytes = done;
                    lastTick = now;

                    progress.Report(new DownloadState(
                        name,
                        done,
                        total,
                        false,
                        speed,
                        eta,
                        _http.Downloads.IsPaused,
                        Volatile.Read(ref _filesDone),
                        Volatile.Read(ref _filesTotal)));
                }
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
                        if (IsAlreadyComplete(item))
                        {
                            Interlocked.Add(ref _done, item.Size);
                            Report(name, true);
                        }
                        else
                        {
                            await _http.DownloadFileAsync(item.Url, item.Path, item.Size, item.Sha1, ct, bytes =>
                            {
                                Interlocked.Add(ref _done, bytes);
                                Report(name, false);
                            }).ConfigureAwait(false);
                        }

                        Interlocked.Increment(ref _filesDone);
                        Report(name, true);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        lock (errors)
                            errors.Add(new DownloadException(item, ex));
                    }
                }
            }

            var count = Math.Clamp(parallelism, 1, 16);
            var workers = new List<Task>();
            for (var index = 0; index < count; index++)
                workers.Add(Task.Run(Worker, ct));

            await Task.WhenAll(workers).ConfigureAwait(false);

            var finalDone = Interlocked.Read(ref _done);
            var finalTotal = Interlocked.Read(ref _total);
            progress?.Report(new DownloadState(
                "Готово",
                finalDone,
                finalTotal,
                true,
                0,
                TimeSpan.Zero,
                false,
                Volatile.Read(ref _filesDone),
                Volatile.Read(ref _filesTotal)));

            if (errors.Count > 0)
                throw new AggregateException(errors);
        }
        finally
        {
            Interlocked.Exchange(ref _active, 0);
            _runGate.Release();
        }
    }

    private static bool IsAlreadyComplete(DownloadItem item)
    {
        try
        {
            if (!File.Exists(item.Path))
                return false;

            var length = new FileInfo(item.Path).Length;
            if (item.Size > 0 && length != item.Size)
                return false;

            return string.IsNullOrEmpty(item.Sha1) || HttpApi.Sha1Equals(item.Path, item.Sha1);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public sealed class DownloadException : Exception
{
    public DownloadException(DownloadItem item, Exception inner)
        : base($"Не удалось скачать {item.Name} ({item.Url}): {inner.Message}", inner)
    {
    }
}
