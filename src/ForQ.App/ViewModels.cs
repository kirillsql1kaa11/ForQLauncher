using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ForQ.Core;

namespace ForQ.App;

public abstract class ObservableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void Raise([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class SearchItem : ObservableBase
{
    private Bitmap? _icon;
    private bool _installed;
    private bool _selected;
    private string _description;

    public SearchItem(ModrinthProject project, string typeLabel)
    {
        Project = project;
        TypeLabel = typeLabel;
        _description = project.Description;
    }

    public ModrinthProject Project { get; }
    public string Title => Project.Title;
    public string Description => _description;
    public string Author => string.IsNullOrWhiteSpace(Project.Author) ? "неизвестно" : Project.Author!;
    public string TypeLabel { get; }
    public string Downloads => Format.Count(Project.Downloads);
    public string Meta => $"{Author} · {Downloads} скачиваний";
    public string Versions => Project.GameVersions.Count == 0
        ? TypeLabel
        : $"{TypeLabel} · {string.Join(", ", Project.GameVersions.Take(4))}";

    public Bitmap? Icon
    {
        get => _icon;
        set => Set(ref _icon, value);
    }

    public bool Installed
    {
        get => _installed;
        set
        {
            if (!Set(ref _installed, value))
                return;
            Raise(nameof(NotInstalled));
            Raise(nameof(InstallLabel));
        }
    }

    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public void SetDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return;
        Set(ref _description, description, nameof(Description));
    }

    public bool NotInstalled => !_installed;
    public string InstallLabel => _installed ? "Установлено" : "Установить";

    public async Task LoadIconAsync(CancellationToken ct)
        => Icon = await ImageCache.LoadAsync(Project.IconUrl, ct);
}

public sealed class InstalledItem : ObservableBase
{
    private Bitmap? _icon;
    private bool _updateAvailable;
    private bool _confirmDelete;
    private bool _pinned;
    private string _subtitle = "";

    public InstalledItem(InstalledContent content)
    {
        Content = content;
        _pinned = content.Pinned;
        UpdateSubtitle(content.VersionLabel);
    }

    public InstalledContent Content { get; }
    public string FileName => Content.FileName;
    public string Title => Content.DisplayName;
    public string Subtitle => _subtitle;
    public bool Enabled => Content.Enabled;
    public bool Pinned => _pinned;
    public string PinLabel => _pinned ? "Открепить" : "Закрепить";

    public Bitmap? Icon
    {
        get => _icon;
        set => Set(ref _icon, value);
    }

    public bool UpdateAvailable
    {
        get => _updateAvailable;
        set => Set(ref _updateAvailable, value);
    }

    public bool ConfirmDelete
    {
        get => _confirmDelete;
        set
        {
            if (Set(ref _confirmDelete, value))
                Raise(nameof(DeleteLabel));
        }
    }

    public string DeleteLabel => _confirmDelete ? "Точно?" : "Удалить";

    public void SetVersion(string label) => UpdateSubtitle(label);

    public void SetPinned(bool pinned)
    {
        if (!Set(ref _pinned, pinned, nameof(Pinned)))
            return;

        Raise(nameof(PinLabel));
        UpdateSubtitle(Content.VersionLabel);
    }

    private void UpdateSubtitle(string versionLabel)
    {
        var parts = new List<string> { versionLabel };
        if (Content.VersionChannel != "release")
            parts.Add(Content.ChannelName);
        if (_pinned)
            parts.Add("закреплено");

        _subtitle = string.Join(" · ", parts);
        Raise(nameof(Subtitle));
    }

    public async Task LoadIconAsync(CancellationToken ct)
        => Icon = await ImageCache.LoadAsync(Content.IconUrl, ct);
}

public sealed class GalleryItem
{
    public required string Url { get; init; }
    public string? Title { get; init; }
    public Bitmap? Image { get; set; }
}

public sealed class ReleaseNoteItem
{
    public string Title { get; init; } = "";
    public string Meta { get; init; } = "";
    public string Body { get; init; } = "";
}

public enum LogLevel
{
    Info,
    Mod,
    Warning,
    Error
}

public sealed class LogEntry
{
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#E7EBF2"));
    private static readonly IBrush ModBrush = new SolidColorBrush(Color.Parse("#7DD482"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#E0B25B"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E0575B"));

    public LogEntry(string text, LogLevel level)
    {
        Text = text;
        Level = level;
    }

    public string Text { get; }
    public LogLevel Level { get; }

    public IBrush Brush => Level switch
    {
        LogLevel.Error => ErrorBrush,
        LogLevel.Warning => WarningBrush,
        LogLevel.Mod => ModBrush,
        _ => InfoBrush
    };

    public static LogLevel Classify(string text)
    {
        if (text.Contains("error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("exception", StringComparison.OrdinalIgnoreCase)
            || text.Contains("fatal", StringComparison.OrdinalIgnoreCase)
            || text.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ошибка", StringComparison.OrdinalIgnoreCase)
            || text.Contains("краш", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Error;

        if (text.Contains("warn", StringComparison.OrdinalIgnoreCase)
            || text.Contains("предупрежд", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Warning;

        if (text.Contains(".jar", StringComparison.OrdinalIgnoreCase)
            || text.Contains("fabric", StringComparison.OrdinalIgnoreCase)
            || text.Contains("quilt", StringComparison.OrdinalIgnoreCase)
            || text.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Mod;

        return LogLevel.Info;
    }
}

public static class Format
{
    public static string Count(long value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000.0).ToString("0.#") + " млн",
        >= 1_000 => (value / 1_000.0).ToString("0.#") + " тыс.",
        _ => value.ToString()
    };
}
