using ForQ.Core;

var root = Path.Combine(Path.GetTempPath(), "forq-smoke");
var mc = "1.20.1";
var loader = "vanilla";
string loaderVersion = "";

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--root": root = args[++i]; break;
        case "--mc": mc = args[++i]; break;
        case "--loader": loader = args[++i]; break;
        case "--loader-version": loaderVersion = args[++i]; break;
    }
}

Directory.CreateDirectory(root);

using var http = new HttpApi();
var mojang = new MojangService(http);
var assets = new AssetService(http);
var runtime = new RuntimeService(http);
var loaders = new LoaderService(http);
var downloads = new DownloadManager(http);
var instances = new InstanceService(root);
var installer = new GameInstaller(mojang, assets, runtime, loaders, downloads, http);
var launcher = new LaunchService(mojang, runtime, downloads);

var kind = loader.ToLowerInvariant() switch
{
    "fabric" => LoaderKind.Fabric,
    "quilt" => LoaderKind.Quilt,
    "neoforge" => LoaderKind.NeoForge,
    _ => LoaderKind.Vanilla
};

Console.WriteLine($"== {mc} / {kind} ==");

var name = $"smoke-{mc}-{kind.ToString().ToLowerInvariant()}";
var existing = (await instances.LoadAsync()).FirstOrDefault(i => i.Name == name);
Instance instance;

if (existing is not null)
{
    instance = existing;
    Console.WriteLine($"Использую существующий инстанс {instance.Folder}");
}
else
{
    instance = await instances.CreateAsync(name, mc, kind, loaderVersion);
}

if (kind != LoaderKind.Vanilla)
{
    if (string.IsNullOrEmpty(instance.LoaderVersion))
    {
        var builds = await loaders.GetBuildsAsync(kind, mc, CancellationToken.None);
        Console.WriteLine($"Доступные сборки: {builds.Count}, лучшая: {builds.FirstOrDefault()?.Version}");
        instance.LoaderVersion = builds.FirstOrDefault()?.Version ?? "";
        Console.WriteLine($"Выбрана версия: {instance.LoaderVersion}");
    }

    await installer.InstallLoaderAsync(instance, Console.WriteLine, CancellationToken.None);
    await instances.SaveAsync(instance);
}

if (!File.Exists(Path.Combine(instance.Folder, "versions", instance.MinecraftVersion, instance.MinecraftVersion + ".jar")))
    await installer.InstallVanillaAsync(instance, Console.WriteLine, CancellationToken.None);
else
    Console.WriteLine("Vanilla уже установлена.");

var settings = new AppSettings { Username = "Tester", MemoryMb = 2048 };
var plan = await launcher.BuildPlanAsync(instance, settings, Console.WriteLine, CancellationToken.None);

Console.WriteLine("\n--- План запуска ---");
Console.WriteLine("Java: " + plan.JavaPath);
Console.WriteLine("Main: " + plan.MainClass);
Console.WriteLine("JVM:  " + string.Join(' ', plan.JvmArguments));
Console.WriteLine("Args: " + string.Join(' ', plan.GameArguments));
Console.WriteLine("CWD:  " + plan.WorkingDirectory);

if (args.Contains("--launch"))
{
    Console.WriteLine("\nЗапускаю игру...");
    using var process = await launcher.StartAsync(plan, Console.WriteLine, CancellationToken.None);
    await Task.Delay(TimeSpan.FromSeconds(45));
    if (!process.HasExited)
    {
        Console.WriteLine("Игра жива после 45 секунд.");
        process.Kill(true);
    }
    else
    {
        Console.WriteLine("Игра завершилась с кодом " + process.ExitCode);
    }
}
