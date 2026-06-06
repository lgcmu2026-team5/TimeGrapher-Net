namespace TimeGrapher.App.Tabs;

public sealed record AnalysisTabResetContext(
    int SampleRate,
    double RateErrorYScale,
    int RateDataPoints);
