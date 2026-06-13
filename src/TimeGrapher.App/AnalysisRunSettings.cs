using TimeGrapher.App.Rendering;
using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.AudioIo;
using TimeGrapher.Core.Detection.Scoring;

namespace TimeGrapher.App;

internal sealed record AnalysisRunSettings(
    int SampleRate,
    double LiftAngle,
    int AveragingPeriod,
    bool UseCOnset,
    bool AutoBph,
    int ManualBph,
    double HpfCutoffHz,
    int SoundImageWidth,
    int SoundImageHeight,
    int ScopeSnapshotPointBudget,
    bool PllEventVeto)
{
    public AnalysisWorker.Config ToWorkerConfig(ulong sessionId, ISampleWriter? sampleWriter)
    {
        return new AnalysisWorker.Config
        {
            SampleRate = SampleRate,
            LiftAngle = LiftAngle,
            AveragingPeriod = AveragingPeriod,
            UseCOnset = UseCOnset,
            SessionId = sessionId,
            AutoBph = AutoBph,
            ManualBph = ManualBph,
            HpfCutoffHz = HpfCutoffHz,
            // The PLL event veto stays opt-in because it trades a small recall
            // cost for precision on impulse-contaminated streams.
            EventGate = PllEventVeto ? new PllMatchGate() : null,
            SoundImageWidth = SoundImageWidth,
            SoundImageHeight = SoundImageHeight,
            ScopeSnapshotPointBudget = ScopeSnapshotPointBudget,
            // Sound print background follows the scope background (single source: App.axaml ScopeBgColor).
            SoundImageBackgroundColor = PlotThemePalette.Current.ScopeBg,
            SampleWriter = sampleWriter,
        };
    }
}
