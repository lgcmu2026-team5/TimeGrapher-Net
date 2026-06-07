using TimeGrapher.App.Services;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class AnalysisFrameRenderSchedulerTests
{
    [Fact]
    public void EnqueueRendersFramePostedToUi()
    {
        var harness = new SchedulerHarness();

        harness.Scheduler.Enqueue(new AnalysisFrame { SourceId = 10 });
        harness.RunNextPostedAction();

        Assert.Collection(
            harness.Rendered,
            rendered =>
            {
                Assert.Equal<ulong>(10, rendered.Frame.SourceId);
                Assert.Equal<ulong>(0, rendered.DroppedFrames);
            });
    }

    [Fact]
    public void EnqueueCoalescesPendingFramesAndReportsDrops()
    {
        var harness = new SchedulerHarness();

        harness.Scheduler.Enqueue(new AnalysisFrame { SourceId = 1 });
        harness.Scheduler.Enqueue(new AnalysisFrame { SourceId = 2 });
        harness.Scheduler.Enqueue(new AnalysisFrame { SourceId = 3 });
        harness.RunNextPostedAction();

        Assert.Collection(
            harness.Rendered,
            rendered =>
            {
                Assert.Equal<ulong>(3, rendered.Frame.SourceId);
                Assert.Equal<ulong>(2, rendered.DroppedFrames);
            });
    }

    [Fact]
    public void EnqueueMergesTransientSignalsFromDisplacedFrames()
    {
        var harness = new SchedulerHarness();
        var soundImage = new PixelBuffer(4, 4);

        harness.Scheduler.Enqueue(new AnalysisFrame
        {
            SourceId = 1,
            InputOverrun = true,
            InputSamplesDropped = 100,
            SoundImage = soundImage,
            SoundImageUpdated = true,
        });
        harness.Scheduler.Enqueue(new AnalysisFrame { SourceId = 2, InputSamplesDropped = 5 });
        harness.RunNextPostedAction();

        Assert.Collection(
            harness.Rendered,
            rendered =>
            {
                Assert.Equal<ulong>(2, rendered.Frame.SourceId);
                Assert.True(rendered.Frame.InputOverrun);
                Assert.Equal<ulong>(105, rendered.Frame.InputSamplesDropped);
                Assert.True(rendered.Frame.SoundImageUpdated);
                Assert.Same(soundImage, rendered.Frame.SoundImage);
            });
    }

    [Fact]
    public void EnqueueKeepsReplacementSoundImageWhenBothFramesUpdated()
    {
        var harness = new SchedulerHarness();
        var newerImage = new PixelBuffer(4, 4);

        harness.Scheduler.Enqueue(new AnalysisFrame
        {
            SourceId = 1,
            SoundImage = new PixelBuffer(4, 4),
            SoundImageUpdated = true,
        });
        harness.Scheduler.Enqueue(new AnalysisFrame
        {
            SourceId = 2,
            SoundImage = newerImage,
            SoundImageUpdated = true,
        });
        harness.RunNextPostedAction();

        Assert.Same(newerImage, harness.Rendered.Single().Frame.SoundImage);
    }

    [Fact]
    public void ResetInvalidatesQueuedRender()
    {
        var harness = new SchedulerHarness();

        harness.Scheduler.Enqueue(new AnalysisFrame { SourceId = 1 });
        harness.Scheduler.Reset();
        harness.RunNextPostedAction();

        Assert.Empty(harness.Rendered);
    }

    [Fact]
    public void ResetTimingPreservesPendingFrame()
    {
        var harness = new SchedulerHarness();

        harness.Scheduler.Enqueue(new AnalysisFrame { SourceId = 1 });
        harness.RunNextPostedAction();

        harness.Scheduler.Enqueue(new AnalysisFrame { SourceId = 2 });
        harness.Scheduler.ResetTiming();
        harness.RunNextPostedAction();

        Assert.Equal(new ulong[] { 1, 2 }, harness.Rendered.Select(rendered => rendered.Frame.SourceId));
    }

    [Fact]
    public void RefreshIntervalDelaysNextRender()
    {
        var harness = new SchedulerHarness();

        harness.Scheduler.Enqueue(new AnalysisFrame { SourceId = 1 });
        harness.RunNextPostedAction();

        harness.Scheduler.Enqueue(new AnalysisFrame { SourceId = 2 });
        harness.RunNextPostedAction();

        Assert.Single(harness.Delays);
        Assert.Single(harness.Rendered);

        harness.UtcNow = harness.UtcNow.Add(harness.Delays[0]);
        harness.RunNextPostedAction();

        Assert.Equal(new ulong[] { 1, 2 }, harness.Rendered.Select(rendered => rendered.Frame.SourceId));
    }

    private sealed class SchedulerHarness
    {
        private readonly Queue<Action> _postedActions = new();

        public SchedulerHarness()
        {
            Scheduler = new AnalysisFrameRenderScheduler(
                action => _postedActions.Enqueue(action),
                () => 100,
                (frame, droppedFrames) => Rendered.Add(new RenderedFrame(frame, droppedFrames)),
                () => UtcNow,
                delay =>
                {
                    Delays.Add(delay);
                    return Task.CompletedTask;
                });
        }

        public AnalysisFrameRenderScheduler Scheduler { get; }

        public DateTime UtcNow { get; set; } = new(2026, 6, 6, 0, 0, 0, DateTimeKind.Utc);

        public List<RenderedFrame> Rendered { get; } = new();

        public List<TimeSpan> Delays { get; } = new();

        public void RunNextPostedAction()
        {
            if (_postedActions.TryDequeue(out Action? action))
            {
                action();
            }
        }
    }

    private readonly record struct RenderedFrame(AnalysisFrame Frame, ulong DroppedFrames);
}
