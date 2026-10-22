using System.ComponentModel;
using System.Runtime.CompilerServices;
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

    public SearchItem(ModrinthProject project, string typeLabel)
    {
        Project = project;
        TypeLabel = typeLabel;
    }

    public ModrinthProject Project { get; }
    public string Title => Project.Title;
    public string Description => Project.Description;
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
    private string _versionNumber = "";

    public InstalledItem(InstalledContent content)
    {
        Content = content;
        _versionNumber = content.VersionLabel;
    }

    public InstalledContent Content { get; }
    public string FileName => Content.FileName;
    public string Title => Content.DisplayName;
    public string Subtitle => _versionNumber;
    public bool Enabled => Content.Enabled;

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

    public void SetVersion(string label)
    {
        _versionNumber = label;
        Raise(nameof(Subtitle));
    }

    public async Task LoadIconAsync(CancellationToken ct)
        => Icon = await ImageCache.LoadAsync(Content.IconUrl, ct);
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
