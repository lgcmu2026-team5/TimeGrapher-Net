using Avalonia;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--smoke", StringComparer.Ordinal))
        {
            _ = BuildAvaloniaApp();
            _ = typeof(AnalysisFrame).Assembly.FullName;
            Console.WriteLine("TimeGrapher.App smoke OK");
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
