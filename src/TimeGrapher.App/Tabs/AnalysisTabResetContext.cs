namespace TimeGrapher.App.Tabs;

internal sealed record AnalysisTabResetContext(
    int SampleRate,
    double RateErrorYScale,
    int RateDataPoints);
