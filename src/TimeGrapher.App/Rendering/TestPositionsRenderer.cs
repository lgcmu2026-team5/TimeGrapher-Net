using Avalonia.Controls;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// Positions tab: one button per NIHS 95-10 / ISO 3158 watch test position.
/// The active button carries the "active" style class (accent
/// background, white text; styles in App.axaml so the highlight re-themes via
/// DynamicResource). Clicking highlights immediately; while frames flow the
/// highlight follows the position Core actually stamps into the cumulative
/// metrics snapshot, so the display shows what the analysis is really tagging.
/// Re-renders are gated on the snapshot version, so coalesced or repeated
/// frames cost nothing.
/// </summary>
internal sealed class TestPositionsRenderer
{
    private const string ActiveClass = "active";

    private readonly Button[] _buttons; // indexed by WatchPosition ordinal
    private int _activeIndex = -1;
    private ulong _lastVersion;

    // The user's latest click, still in flight to the analysis thread. Snapshot
    // feedback is ignored until Core echoes it back: an in-flight snapshot built
    // BEFORE the click otherwise snapped the highlight back to the old position
    // for up to a second before the next snapshot corrected it.
    private WatchPosition? _pendingPosition;

    public TestPositionsRenderer(Button[] buttons, WatchPosition initialPosition)
    {
        _buttons = buttons;
        Highlight(initialPosition);
    }

    /// <summary>User click: highlight immediately and latch until Core echoes it.</summary>
    public void RequestPosition(WatchPosition position)
    {
        _pendingPosition = position;
        Highlight(position);
    }

    private void Highlight(WatchPosition position)
    {
        int index = (int)position;
        if (_activeIndex == index)
        {
            return;
        }

        _activeIndex = index;
        for (int i = 0; i < _buttons.Length; i++)
        {
            if (i == index)
            {
                if (!_buttons[i].Classes.Contains(ActiveClass))
                {
                    _buttons[i].Classes.Add(ActiveClass);
                }
            }
            else
            {
                _buttons[i].Classes.Remove(ActiveClass);
            }
        }
    }

    public void Reset()
    {
        // The highlight is selection state (the watch's physical orientation),
        // not run data; the snapshot version gate restarts. The in-flight
        // latch dies with the worker it was sent to: a new session must not
        // ignore snapshots on behalf of a request the old session never
        // confirmed (today RunSessionController replays the position into the
        // new worker, so the first snapshot would echo-clear it anyway, but
        // the renderer must not depend on that replay invariant).
        _lastVersion = 0;
        _pendingPosition = null;
    }

    public void RenderFrame(AnalysisFrame frame)
    {
        BeatMetricsHistorySnapshot? history = frame.MetricsHistory;
        if (history == null || history.Version == _lastVersion)
        {
            return;
        }

        _lastVersion = history.Version;

        if (_pendingPosition is WatchPosition pending)
        {
            // Older in-flight snapshots still carry the pre-click position;
            // hold the user's choice until Core confirms it.
            //
            // Residual window: confirmation is value equality, so the echo
            // carries no request identity. Rapid clicks A->B->A straddling a
            // slow analysis pass can clear the latch on the FIRST A click's
            // echo, letting the in-flight B echo flash backward until the
            // third click's echo corrects it. A monotonic request token
            // echoed through the snapshot would close it (the value alone
            // cannot distinguish click1's echo from click3's) - deemed out
            // of scope: it needs a Core+Shared contract change for a window
            // that requires three clicks within roughly one pass-publish
            // latency.
            if (history.ActivePosition != pending)
            {
                return;
            }

            _pendingPosition = null;
        }

        Highlight(history.ActivePosition);
    }
}
