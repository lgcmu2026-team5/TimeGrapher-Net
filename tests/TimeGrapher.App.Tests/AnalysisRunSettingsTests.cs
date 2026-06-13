using TimeGrapher.App;
using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.Detection.Scoring;
using Xunit;

namespace TimeGrapher.App.Tests;

/// <summary>
/// Pins the run-settings -> worker-config policy: adaptive floor + regime
/// guard (the regression-free pair, per the adverse A/B measurements) are
/// ALWAYS on for GUI runs, while the PLL event veto - which boosts precision
/// on weak/impulsive signals but costs recall under extreme sustained noise
/// - stays behind the checkbox, default off.
/// </summary>
public sealed class AnalysisRunSettingsTests
{
    private static AnalysisRunSettings NewSettings(bool pllEventVeto) => new(
        SampleRate: 48000,
        LiftAngle: 52.0,
        AveragingPeriod: 2,
        UseCOnset: false,
        AutoBph: true,
        ManualBph: 0,
        HpfCutoffHz: 200.0,
        SoundImageWidth: 100,
        SoundImageHeight: 100,
        ScopeSnapshotPointBudget: 8000,
        PllEventVeto: pllEventVeto);

    [Fact]
    public void Default_WiresFloorAndGuardWithoutTheVeto()
    {
        AnalysisWorker.Config config = NewSettings(pllEventVeto: false)
            .ToWorkerConfig(sessionId: 1, sampleWriter: null);

        Assert.NotNull(config.DetectorOptions);
        Assert.True(config.DetectorOptions!.EnableAdaptiveFloor);
        Assert.True(config.DetectorOptions.EnableRegimeGuard);
        Assert.Null(config.EventGate);
    }

    [Fact]
    public void PllEventVetoOn_AddsTheGateOnTopOfFloorAndGuard()
    {
        AnalysisWorker.Config config = NewSettings(pllEventVeto: true)
            .ToWorkerConfig(sessionId: 1, sampleWriter: null);

        Assert.NotNull(config.DetectorOptions);
        Assert.True(config.DetectorOptions!.EnableAdaptiveFloor);
        Assert.True(config.DetectorOptions.EnableRegimeGuard);
        Assert.IsType<PllMatchGate>(config.EventGate);
    }
}
