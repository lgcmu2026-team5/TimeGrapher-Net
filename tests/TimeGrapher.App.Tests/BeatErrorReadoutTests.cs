using TimeGrapher.App.Rendering;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.App.Tests;

/// <summary>
/// Pure formatter behind the Beat Error Display numeric panel: the four plan
/// readings plus the derived DiffTicTac / DiffPeriod / AvgPeriod measures.
/// </summary>
public sealed class BeatErrorReadoutTests
{
    [Fact]
    public void Labels_CoverPlanReadingsAndDerivedMeasures()
    {
        Assert.Equal(
            new[]
            {
                "RATE", "AMPLITUDE", "BEAT ERROR", "BPH",
                "DIFF TIC-TAC", "DIFF PERIOD", "AVG PERIOD",
            },
            BeatErrorReadout.Labels);
    }

    [Fact]
    public void Values_FormatEveryReadingWithItsUnit()
    {
        var snapshot = new BeatMetricsHistorySnapshot
        {
            RateValid = true,
            RateSPerDay = 4.2,
            AmplitudeValid = true,
            AmplitudeDeg = 285.4,
            BeatErrorValid = true,
            BeatErrorSignedMs = -0.8,
            Bph = 28800,
            Derived = new DerivedTimingMeasures(true, 1.6, true, 0.25, true, -0.4),
        };

        Assert.Equal(
            new[]
            {
                "+4.2 s/d", "285°", "-0.80 ms", "28800 bph",
                "+1.60 ms", "+0.25 ms", "-0.40 ms",
            },
            BeatErrorReadout.Values(snapshot));
    }

    [Fact]
    public void Values_ShowDashesForAbsentReadings()
    {
        string[] values = BeatErrorReadout.Values(new BeatMetricsHistorySnapshot());

        Assert.Equal(BeatErrorReadout.Labels.Length, values.Length);
        Assert.All(values, value => Assert.Equal(VarioReadout.Missing, value));
    }
}
