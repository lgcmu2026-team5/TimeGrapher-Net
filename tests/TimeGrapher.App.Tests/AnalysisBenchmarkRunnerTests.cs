using TimeGrapher.App.Audio;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class AnalysisBenchmarkRunnerTests
{
    [Fact]
    public void RunCompletesShortSyntheticBenchmark()
    {
        int exitCode = AnalysisBenchmarkRunner.Run(
            new[] { "--analysis-benchmark", "--bph", "43200", "--rate", "48000", "--duration-ms", "3000" },
            analysisLogPath: null);

        Assert.Equal(0, exitCode);
    }
}
