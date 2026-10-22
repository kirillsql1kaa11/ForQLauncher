using Avalonia;
using System;

namespace ForQ.App;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "ForQLauncher", "crash.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, e.ExceptionObject?.ToString() ?? "unknown");
            }
            catch (Exception)
            {
            }
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
