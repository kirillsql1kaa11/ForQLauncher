using ForQ.Core;

namespace ForQ.App;

public sealed class AppServices : IDisposable
{
    public static AppServices Current { get; } = new();

    public AppServices()
    {
        Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ForQLauncher");
        CacheDirectory = Path.Combine(Root, "cache");

        Http = new HttpApi();
        Mojang = new MojangService(Http);
        Assets = new AssetService(Http);
        Runtime = new RuntimeService(Http);
        Loaders = new LoaderService(Http);
        Modrinth = new ModrinthService(Http);
        Downloads = new DownloadManager(Http);
        Content = new ContentService(Downloads);
        Conflicts = new ModConflictService(Content);
        Crashes = new CrashAnalyzer(Content);
        Instances = new InstanceService(Root);
        Installer = new GameInstaller(Mojang, Assets, Runtime, Loaders, Downloads, Http);
        Launcher = new LaunchService(Mojang, Runtime, Downloads);
        Modpacks = new ModpackService(Instances, Installer, Downloads, Modrinth, Content);
        Updates = new UpdateService(Http, Downloads);
        Translation = new TranslationService(Http, Path.Combine(CacheDirectory, "translations.json"));
        Settings = Instances.LoadSettings();
    }

    public string Root { get; }
    public string CacheDirectory { get; }
    public HttpApi Http { get; }
    public MojangService Mojang { get; }
    public AssetService Assets { get; }
    public RuntimeService Runtime { get; }
    public LoaderService Loaders { get; }
    public ModrinthService Modrinth { get; }
    public ContentService Content { get; }
    public ModConflictService Conflicts { get; }
    public CrashAnalyzer Crashes { get; }
    public DownloadManager Downloads { get; }
    public InstanceService Instances { get; }
    public GameInstaller Installer { get; }
    public LaunchService Launcher { get; }
    public ModpackService Modpacks { get; }
    public UpdateService Updates { get; }
    public TranslationService Translation { get; }
    public AppSettings Settings { get; }

    public static string Version
    {
        get
        {
            var version = typeof(AppServices).Assembly.GetName().Version;
            return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public void SaveSettings() => Instances.SaveSettings(Settings);

    public void Dispose() => Http.Dispose();
}
