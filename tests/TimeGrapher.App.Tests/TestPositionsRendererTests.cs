using System;
using System.Linq;
using Avalonia.Controls;
using TimeGrapher.App.Rendering;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.App.Tests;

/// <summary>
/// The Positions click-latch contract: a click highlights immediately and
/// holds against stale in-flight snapshots until Core echoes the requested
/// position back, after which the snapshot regains authority. Buttons are
/// constructed headless (the InfoTabRegistryTests pattern).
/// </summary>
public sealed class TestPositionsRendererTests
{
    private const string ActiveClass = "active";

    private static Button[] Buttons()
    {
        return Enumerable.Range(0, Enum.GetValues<WatchPosition>().Length)
            .Select(_ => new Button())
            .ToArray();
    }

    private static AnalysisFrame Frame(ulong version, WatchPosition position)
    {
        return new AnalysisFrame
        {
            MetricsHistory = new BeatMetricsHistorySnapshot
            {
                Version = version,
                ActivePosition = position,
            },
        };
    }

    private static WatchPosition? ActivePosition(Button[] buttons)
    {
        for (int i = 0; i < buttons.Length; i++)
        {
            if (buttons[i].Classes.Contains(ActiveClass))
            {
                return (WatchPosition)i;
            }
        }

        return null;
    }

    [Fact]
    public void ClickHighlightsImmediatelyWithoutWaitingForAFrame()
    {
        Button[] buttons = Buttons();
        var renderer = new TestPositionsRenderer(buttons, WatchPosition.CH);

        renderer.RequestPosition(WatchPosition.P6H);

        Assert.Equal(WatchPosition.P6H, ActivePosition(buttons));
    }

    [Fact]
    public void StaleSnapshotIsHeldWhileTheClickIsStillInFlight()
    {
        Button[] buttons = Buttons();
        var renderer = new TestPositionsRenderer(buttons, WatchPosition.CH);
        renderer.RequestPosition(WatchPosition.P6H);

        // An in-flight snapshot built BEFORE the click still carries the old
        // position; the latch must hold the user's choice.
        renderer.RenderFrame(Frame(version: 1, WatchPosition.CH));

        Assert.Equal(WatchPosition.P6H, ActivePosition(buttons));
    }

    [Fact]
    public void MatchingEchoClearsTheLatch()
    {
        Button[] buttons = Buttons();
        var renderer = new TestPositionsRenderer(buttons, WatchPosition.CH);
        renderer.RequestPosition(WatchPosition.P6H);

        renderer.RenderFrame(Frame(version: 1, WatchPosition.P6H));

        Assert.Equal(WatchPosition.P6H, ActivePosition(buttons));

        // The latch is cleared: a later snapshot regains authority.
        renderer.RenderFrame(Frame(version: 2, WatchPosition.CB));

        Assert.Equal(WatchPosition.CB, ActivePosition(buttons));
    }

    [Fact]
    public void SnapshotDrivesTheHighlightWhenNoClickIsPending()
    {
        Button[] buttons = Buttons();
        var renderer = new TestPositionsRenderer(buttons, WatchPosition.CH);

        renderer.RenderFrame(Frame(version: 1, WatchPosition.P9H));

        Assert.Equal(WatchPosition.P9H, ActivePosition(buttons));
    }

    [Fact]
    public void VersionGateShortCircuitsRepeatedSnapshots()
    {
        Button[] buttons = Buttons();
        var renderer = new TestPositionsRenderer(buttons, WatchPosition.CH);
        renderer.RenderFrame(Frame(version: 1, WatchPosition.CB));

        // Same version again (a coalesced or repeated frame): no re-render,
        // even though the carried position differs.
        renderer.RenderFrame(Frame(version: 1, WatchPosition.P12H));

        Assert.Equal(WatchPosition.CB, ActivePosition(buttons));
    }

    [Fact]
    public void ResetClearsThePendingLatch()
    {
        Button[] buttons = Buttons();
        var renderer = new TestPositionsRenderer(buttons, WatchPosition.CH);
        renderer.RequestPosition(WatchPosition.P6H);

        // Session boundary: the unconfirmed request died with the old worker.
        // The next session's snapshot must regain authority immediately, even
        // when it carries a position the dead request never matched.
        renderer.Reset();
        renderer.RenderFrame(Frame(version: 1, WatchPosition.CB));

        Assert.Equal(WatchPosition.CB, ActivePosition(buttons));
    }

    [Fact]
    public void FrameWithoutHistoryIsIgnored()
    {
        Button[] buttons = Buttons();
        var renderer = new TestPositionsRenderer(buttons, WatchPosition.CH);

        renderer.RenderFrame(new AnalysisFrame());

        Assert.Equal(WatchPosition.CH, ActivePosition(buttons));
    }
}
