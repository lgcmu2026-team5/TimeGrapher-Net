using Avalonia;
using TimeGrapher.App.Audio;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppStartupOptions.Configure(args);

        if (args.Contains("--smoke", StringComparer.Ordinal))
        {
            _ = BuildAvaloniaApp();
            _ = typeof(AnalysisFrame).Assembly.FullName;
            Console.WriteLine("TimeGrapher.App smoke OK");
            return 0;
        }

        if (args.Contains("--audio-smoke", StringComparer.Ordinal))
        {
            return AudioSmokeRunner.Run(args, capture: false);
        }

        if (args.Contains("--capture-smoke", StringComparer.Ordinal))
        {
            return AudioSmokeRunner.Run(args, capture: true);
        }

        if (args.Contains("--analysis-benchmark", StringComparer.Ordinal))
        {
            return AnalysisBenchmarkRunner.Run(args);
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
