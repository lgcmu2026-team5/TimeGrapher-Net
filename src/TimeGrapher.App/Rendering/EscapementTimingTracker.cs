using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

/// <summary>
/// Which A→C timing reference repeats more tightly over the tracked window:
/// the C cluster's rising edge (onset) or its envelope peak.
/// </summary>
internal enum EscapementReferenceVerdict
{
    /// <summary>Not enough samples on at least one reference to judge.</summary>
    Undecided,
    Onset,
    Peak,
}

/// <summary>
/// Pure accumulator behind the Escapement Analyzer: a fixed ring over the last
/// <see cref="WindowSegments"/> beat segments holding the A→C interval per
/// timing reference — the peak interval for every segment with a valid C peak,
/// plus the onset interval when the detector also located the C cluster's
/// rising edge. Exposes mean and sigma (population standard deviation) per
/// reference and the repeatability verdict: the reference with the smaller
/// sigma, judged only once both references hold at least
/// <see cref="MinSamplesForVerdict"/> samples (ties keep the conventional peak
/// reference).
///
/// Fed from the cumulative BeatSegmentsSnapshot the frame carries: segments
/// already seen (by stream start time) are skipped, so re-feeding the same or
/// an overlapping snapshot never double-counts. Bounded by construction (one
/// fixed entry array, no per-beat allocation) and UI-thread only.
/// </summary>
internal sealed class EscapementTimingTracker
{
    /// <summary>Segments the repeatability statistics look back over.</summary>
    public const int WindowSegments = 32;

    /// <summary>Samples each reference needs before the verdict is judged.</summary>
    public const int MinSamplesForVerdict = 8;

    private struct Entry
    {
        public double PeakMs;
        public bool HasOnset;
        public double OnsetMs;
    }

    private readonly Entry[] _entries = new Entry[WindowSegments];
    private int _head;
    private int _count;
    private bool _hasSeen;
    private double _lastSeenStartTimeS;

    /// <summary>Tracked beats measured against the C peak (every stored entry).</summary>
    public int PeakCount => _count;

    /// <summary>Tracked beats that also carry a valid C onset.</summary>
    public int OnsetCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _count; i++)
            {
                if (At(i).HasOnset)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public double? PeakMeanMs => Mean(onset: false);
    public double? PeakSigmaMs => Sigma(onset: false);
    public double? OnsetMeanMs => Mean(onset: true);
    public double? OnsetSigmaMs => Sigma(onset: true);

    /// <summary>Smaller-sigma reference once both hold ≥ <see cref="MinSamplesForVerdict"/> samples.</summary>
    public EscapementReferenceVerdict Verdict
    {
        get
        {
            if (PeakCount < MinSamplesForVerdict || OnsetCount < MinSamplesForVerdict)
            {
                return EscapementReferenceVerdict.Undecided;
            }

            return OnsetSigmaMs < PeakSigmaMs
                ? EscapementReferenceVerdict.Onset
                : EscapementReferenceVerdict.Peak;
        }
    }

    /// <summary>
    /// Folds the snapshot's segments not seen yet into the window, oldest
    /// first. Segments without a valid C peak advance the seen watermark but
    /// store nothing (there is no A→C interval to measure).
    /// </summary>
    public void Accumulate(BeatSegmentsSnapshot snapshot)
    {
        IReadOnlyList<BeatSegment> segments = snapshot.Segments;
        for (int i = 0; i < segments.Count; i++)
        {
            BeatSegment segment = segments[i];
            if (_hasSeen && segment.StartTimeS <= _lastSeenStartTimeS)
            {
                continue;
            }

            _hasSeen = true;
            _lastSeenStartTimeS = segment.StartTimeS;
            if (!segment.CPeakValid)
            {
                continue;
            }

            Push(new Entry
            {
                PeakMs = segment.CPeakOffsetMs - segment.AOffsetMs,
                HasOnset = segment.COnsetValid,
                OnsetMs = segment.COnsetOffsetMs - segment.AOffsetMs,
            });
        }
    }

    public void Reset()
    {
        _head = 0;
        _count = 0;
        _hasSeen = false;
        _lastSeenStartTimeS = 0.0;
    }

    private void Push(Entry entry)
    {
        if (_count == WindowSegments)
        {
            _entries[_head] = entry;
            _head = (_head + 1) % WindowSegments;
        }
        else
        {
            _entries[(_head + _count) % WindowSegments] = entry;
            _count++;
        }
    }

    private Entry At(int index) => _entries[(_head + index) % WindowSegments];

    private double? Mean(bool onset)
    {
        double sum = 0.0;
        int count = 0;
        for (int i = 0; i < _count; i++)
        {
            Entry entry = At(i);
            if (onset && !entry.HasOnset)
            {
                continue;
            }

            sum += onset ? entry.OnsetMs : entry.PeakMs;
            count++;
        }

        return count > 0 ? sum / count : null;
    }

    private double? Sigma(bool onset)
    {
        if (Mean(onset) is not double mean)
        {
            return null;
        }

        double sumSquares = 0.0;
        int count = 0;
        for (int i = 0; i < _count; i++)
        {
            Entry entry = At(i);
            if (onset && !entry.HasOnset)
            {
                continue;
            }

            double deviation = (onset ? entry.OnsetMs : entry.PeakMs) - mean;
            sumSquares += deviation * deviation;
            count++;
        }

        return Math.Sqrt(sumSquares / count);
    }
}
