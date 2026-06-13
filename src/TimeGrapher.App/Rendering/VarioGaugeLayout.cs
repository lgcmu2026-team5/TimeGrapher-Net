namespace TimeGrapher.App.Rendering;

/// <summary>Horizontal text anchor for a gauge marker label.</summary>
internal enum GaugeLabelAnchor
{
    Left,
    Center,
    Right,
}

/// <summary>A marker label to render: its short role text, data x, and anchor.</summary>
internal readonly record struct GaugeLabel(string Role, double X, GaugeLabelAnchor Anchor);

/// <summary>
/// Decides which Vario gauge marker labels (min/max/avg/now) to show and how to
/// anchor them so they never overlap each other and never clip at the axis edges,
/// for any arrangement of values. Pure and unit-testable — the renderer just
/// draws whatever this returns. When markers crowd together, the lower-priority
/// labels are dropped (the range bar still shows the extent and the table still
/// lists every number) instead of printing on top of one another.
/// </summary>
internal static class VarioGaugeLayout
{
    /// <summary>A label is treated as this fraction of the axis span wide; closer centres overlap.</summary>
    public const double LabelWidthFraction = 0.07;

    /// <summary>Markers within this fraction of an edge anchor inward so text cannot clip.</summary>
    public const double EdgeFraction = 0.05;

    /// <summary>
    /// Labels to draw, ordered left-to-right. Priority when crowded: average &gt;
    /// current &gt; max &gt; min — the bar ends are self-evident, the interpretive
    /// lines are not.
    /// </summary>
    public static IReadOnlyList<GaugeLabel> LayOut(
        double lo, double hi, double? min, double? max, double? avg, double? current)
    {
        double span = hi - lo;
        if (span <= 0.0)
        {
            return Array.Empty<GaugeLabel>();
        }

        double minGap = span * LabelWidthFraction;
        double edge = span * EdgeFraction;

        // Candidates in descending priority.
        var candidates = new List<(string Role, double X)>(4);
        if (avg is double a)
        {
            candidates.Add(("avg", a));
        }

        if (current is double c)
        {
            candidates.Add(("now", c));
        }

        if (max is double mx)
        {
            candidates.Add(("max", mx));
        }

        if (min is double mn)
        {
            candidates.Add(("min", mn));
        }

        var kept = new List<GaugeLabel>(4);
        foreach ((string role, double x) in candidates)
        {
            bool collides = false;
            foreach (GaugeLabel placed in kept)
            {
                if (Math.Abs(placed.X - x) < minGap)
                {
                    collides = true;
                    break;
                }
            }

            if (collides)
            {
                continue;
            }

            GaugeLabelAnchor anchor =
                x <= lo + edge ? GaugeLabelAnchor.Left :
                x >= hi - edge ? GaugeLabelAnchor.Right :
                GaugeLabelAnchor.Center;
            kept.Add(new GaugeLabel(role, x, anchor));
        }

        kept.Sort((l, r) => l.X.CompareTo(r.X));
        return kept;
    }
}
