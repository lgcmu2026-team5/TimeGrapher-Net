using TimeGrapher.App;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class AppStartupOptionsTests
{
    [Fact]
    public void ParseReadsSeparateAndInlineAnalysisLogPath()
    {
        Assert.Equal("pi.csv", AppStartupOptions.Parse(
            new[] { "--analysis-log", "pi.csv" }).AnalysisLogPath);

        Assert.Equal("/tmp/pi.csv", AppStartupOptions.Parse(
            new[] { "--analysis-log=/tmp/pi.csv" }).AnalysisLogPath);
    }

    [Fact]
    public void ParseIgnoresMissingOrBlankAnalysisLogPath()
    {
        Assert.Null(AppStartupOptions.Parse(
            new[] { "--analysis-log" }).AnalysisLogPath);

        Assert.Null(AppStartupOptions.Parse(
            new[] { "--analysis-log=" }).AnalysisLogPath);
    }
}
