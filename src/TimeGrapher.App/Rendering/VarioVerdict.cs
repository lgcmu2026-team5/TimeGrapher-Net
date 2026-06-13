using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>Severity of a Vario assessment, used to colour the status chips and conclusion.</summary>
internal enum VarioVerdictLevel
{
    Pending,
    Good,
    Warn,
    Bad,
}

/// <summary>
/// Derives a short plain-language assessment of a Vario measure from its running
/// statistics and acceptable band. Pure and threshold-based, so any combination
/// of readings maps to one of a small, testable set of verdicts; it reads the
/// already-accumulated stats, so there is no per-beat cost. Because the stats are
/// per watch position, the verdict reflects the current position only.
/// </summary>
internal readonly record struct VarioVerdict(string Text, VarioVerdictLevel Level)
{
    /// <summary>Beats required before a verdict is offered; fewer reads as "measuring".</summary>
    public const long MinSamples = 30;

    /// <summary>Within-band rate spread (s/d) above which the run is called unstable.</summary>
    public const double RateUnstableSigma = 3.0;

    /// <summary>Mean amplitude (deg) below which low amplitude is flagged for service.</summary>
    public const double AmplitudeServiceDeg = 220.0;

    public static readonly VarioVerdict Measuring = new("Measuring…", VarioVerdictLevel.Pending);

    /// <summary>Rate (s/d): in/out of the acceptable band, and stable vs. jittery within it.</summary>
    public static VarioVerdict ForRate(StatsSummary stats, double acceptMin, double acceptMax)
    {
        if (!stats.Valid || stats.Count < MinSamples)
        {
            return Measuring;
        }

        if (stats.Mean > acceptMax)
        {
            return new VarioVerdict("Fast · out of range", VarioVerdictLevel.Bad);
        }

        if (stats.Mean < acceptMin)
        {
            return new VarioVerdict("Slow · out of range", VarioVerdictLevel.Bad);
        }

        return stats.Sigma > RateUnstableSigma
            ? new VarioVerdict("In range · unstable", VarioVerdictLevel.Warn)
            : new VarioVerdict("Stable · in range", VarioVerdictLevel.Good);
    }

    /// <summary>Amplitude (deg): healthy band, marginally low/high, or low enough to flag service.</summary>
    public static VarioVerdict ForAmplitude(StatsSummary stats, double healthyMin, double healthyMax)
    {
        if (!stats.Valid || stats.Count < MinSamples)
        {
            return Measuring;
        }

        if (stats.Mean < AmplitudeServiceDeg)
        {
            return new VarioVerdict("Low · service", VarioVerdictLevel.Bad);
        }

        if (stats.Mean < healthyMin)
        {
            return new VarioVerdict("Slightly low", VarioVerdictLevel.Warn);
        }

        if (stats.Mean > healthyMax)
        {
            return new VarioVerdict("High", VarioVerdictLevel.Warn);
        }

        return new VarioVerdict("Healthy", VarioVerdictLevel.Good);
    }

    /// <summary>
    /// Combined one-line action across both measures: takes the worse severity
    /// and avoids repeating the measure verdicts already shown in Summary.
    /// </summary>
    public static VarioVerdict Overall(VarioVerdict rate, VarioVerdict amplitude)
    {
        if (rate.Level == VarioVerdictLevel.Pending || amplitude.Level == VarioVerdictLevel.Pending)
        {
            return new VarioVerdict(string.Empty, VarioVerdictLevel.Pending);
        }

        var level = (VarioVerdictLevel)Math.Max((int)rate.Level, (int)amplitude.Level);
        string action = level switch
        {
            VarioVerdictLevel.Bad => "ALERT - Service required before recording",
            VarioVerdictLevel.Warn => "WATCH - Keep measuring before judging",
            _ => "OK - Stable enough to record",
        };

        return new VarioVerdict($"Overall: {action}", level);
    }
}
