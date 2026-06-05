/* Bph.cs -- BPH (beats-per-hour) detection and sync tracking.
 * Port of Bph.h / Bph.cpp.
 *
 * Three concerns:
 *   1. Candidate BPH lists (auto / manual) + lookup helpers.
 *   2. Rayleigh phase-score period detection (V5.3 R1-only).
 *   3. TgSync -- a small PLL-ish tracker (acquire / track / lose).
 *
 * Event-time arrays are passed as ReadOnlySpan<double> with an explicit
 * count (n) to mirror the (const double*, size_t) C signatures.
 */

namespace TimeGrapher.Core.Detection;

/// <summary>
/// Static BPH candidate lists and phase-score helpers (Bph.cpp free
/// functions). All methods are direct ports of the C functions.
/// </summary>
internal static class Bph
{
    /* Auto-detect BPH list (common rates). TG_AUTO_BPH_LIST. */
    public static readonly int[] AutoBphList =
    {
        12000, 14400, 18000, 19800, 21600, 25200, 28800, 36000, 43200
    };

    /* Manual-mode BPH list (wider range of antique rates). TG_MANUAL_BPH_LIST. */
    public static readonly int[] ManualBphList =
    {
         3600,  6000,  7200,  7380,  7440,  7800,  9000,  9100, 10800, 11880,
        12000, 12342, 12480, 12600, 13320, 13440, 13500, 14000, 14040, 14160,
        14200, 14280, 14400, 14520, 14580, 14760, 14850, 15000, 15360, 15600,
        16200, 16320, 16800, 17196, 17258, 17280, 17786, 17897, 18000, 18049,
        18514, 19332, 19440, 19800, 20160, 20222, 20944, 21000, 21031, 21306,
        21600, 25200, 28800, 32400, 36000, 43200
    };

    // tg_is_valid_manual_bph
    public static bool IsValidManualBph(int bph)
    {
        for (int i = 0; i < ManualBphList.Length; ++i)
            if (ManualBphList[i] == bph) return true;
        return false;
    }

    // tg_bph_match
    public static int Match(double candidateBph, int[] list, int listLen, double tolerancePct)
    {
        if (candidateBph <= 0.0 || listLen == 0) return 0;

        int best = 0;
        double bestErr = 1e30;
        for (int i = 0; i < listLen; ++i)
        {
            double err = Math.Abs(candidateBph - (double)list[i]) / (double)list[i];
            if (err < bestErr)
            {
                bestErr = err;
                best = list[i];
            }
        }
        if (bestErr * 100.0 <= tolerancePct) return best;
        return 0;
    }

    // tg_estimate_beat_period
    // For events ACACAC..., t[i+2] - t[i] == beat period. Median of deltas.
    public static double EstimateBeatPeriod(ReadOnlySpan<double> eventTimes, int n)
    {
        if (n < 4) return 0.0;
        int m = n - 2;
        double[] deltas = new double[m];
        for (int i = 0; i < m; ++i)
        {
            deltas[i] = eventTimes[i + 2] - eventTimes[i];
        }
        Array.Sort(deltas); // qsort with double comparator
        double med = deltas[m / 2];
        return med;
    }

    /* Phase score: fold event times mod period and measure how
     * concentrated the resulting phases are using the standard Rayleigh
     * statistic.
     *
     *   ph_i  = 2*pi * (t_i mod T) / T
     *   score = | mean(e^{i*ph}) |       (R1, in [0,1])
     *
     * V5.3: returns R1 (first harmonic) only. */
    // tg_phase_score
    public static double PhaseScore(ReadOnlySpan<double> eventTimes, int n, double period)
    {
        if (n < 6 || period <= 0.0) return 0.0;
        double s1 = 0.0, c1 = 0.0;
        double invT = 1.0 / period;
        for (int i = 0; i < n; ++i)
        {
            double ph = 2.0 * Math.PI * (eventTimes[i] - Math.Floor(eventTimes[i] * invT) * period) * invT;
            c1 += Math.Cos(ph);
            s1 += Math.Sin(ph);
        }
        double invN = 1.0 / (double)n;
        return Math.Sqrt(c1 * c1 + s1 * s1) * invN;
    }

    // tg_bph_pick_by_phase
    public static int PickByPhase(ReadOnlySpan<double> eventTimes, int n,
                                  int[] list, int listLen,
                                  double minScore,
                                  out double outScore,
                                  out double outPeriod)
    {
        outScore = 0.0;
        outPeriod = 0.0;
        if (n < 6 || list == null || listLen == 0) return 0;

        /* Compute median A-to-A interval. Test periods less than
         * 0.7 * median are physically implausible and rejected. */
        double[] aa = new double[256];
        int nAa = 0;
        for (int i = 1; i < n && nAa < 256; ++i)
        {
            double d = eventTimes[i] - eventTimes[i - 1];
            if (d > 0.0) aa[nAa++] = d;
        }
        /* simple insertion sort for small nAa */
        for (int i = 1; i < nAa; ++i)
        {
            double v = aa[i];
            int j = i - 1;
            while (j >= 0 && aa[j] > v) { aa[j + 1] = aa[j]; j--; }
            aa[j + 1] = v;
        }
        double medianAa = (nAa > 0) ? aa[nAa / 2] : 0.0;
        double minPeriod = 0.7 * medianAa;

        int bestBph = 0;
        double bestScore = -1.0;
        double bestPeriod = 0.0;
        for (int i = 0; i < listLen; ++i)
        {
            double T = 3600.0 / (double)list[i];
            if (T < minPeriod) continue;   // implausibly short
            double s = PhaseScore(eventTimes, n, T);
            if (s > bestScore)
            {
                bestScore = s;
                bestBph = list[i];
                bestPeriod = T;
            }
        }
        outScore = bestScore;
        outPeriod = bestPeriod;
        if (bestScore >= minScore) return bestBph;
        return 0;
    }
}

/// <summary>
/// Sync state tracker (tg_sync_t). Tracks expected next event time using
/// the current beat period and within-beat A-C offset.
/// </summary>
internal sealed class TgSync
{
    public int Bph;                 // current locked BPH
    public double BeatPeriod;       // seconds
    public double AcOffset;         // mean A->C delta (seconds)

    /* PLL-ish tracking */
    public double NextATime;        // predicted absolute time of next A
    public int Synced;
    public int ConsecutiveMisses;

    /* tolerance */
    public double ToleranceS;       // +/- around predicted event
    public int MaxMisses;

    /* PLL gains */
    public double PeriodGain;       // default 0.01
    public double AcGain;           // default 0.05

    /* timestamp of the most recent matched event */
    public double LastMatchTime;

    // tg_sync_init: memset(s, 0, sizeof(*s)) -- all fields zeroed.
    public void Init()
    {
        Bph = 0;
        BeatPeriod = 0.0;
        AcOffset = 0.0;
        NextATime = 0.0;
        Synced = 0;
        ConsecutiveMisses = 0;
        ToleranceS = 0.0;
        MaxMisses = 0;
        PeriodGain = 0.0;
        AcGain = 0.0;
        LastMatchTime = 0.0;
    }

    // tg_sync_reset
    public void Reset()
    {
        Synced = 0;
        ConsecutiveMisses = 0;
    }

    // tg_sync_lock
    public void Lock(int bph, double beatPeriod, double acOffset, double firstATime,
                     double toleranceS, int maxMisses, double periodGain, double acGain)
    {
        Bph = bph;
        BeatPeriod = beatPeriod;
        AcOffset = acOffset;
        NextATime = firstATime + beatPeriod;
        Synced = 1;
        ConsecutiveMisses = 0;
        ToleranceS = toleranceS;
        MaxMisses = maxMisses;
        PeriodGain = periodGain;
        AcGain = acGain;
        LastMatchTime = firstATime;
    }

    // tg_sync_update
    public int Update(double eventTime)
    {
        if (Synced == 0) return 0;

        /* Phase-based matching: each event should fall near one of two
         * phases within the beat period - phase 0 (reference event) and
         * phase |ac_offset| (companion event). */

        double reff = NextATime - BeatPeriod; // last A time
        double phase = eventTime - reff;
        double T = BeatPeriod;
        while (phase > 0.5 * T) phase -= T;
        while (phase < -0.5 * T) phase += T;

        double g = AcOffset; // magnitude of within-beat gap
        double errA = Math.Abs(phase);
        double errCPos = Math.Abs(phase - g);
        double errCNeg = Math.Abs(phase + g);
        double errC = (errCPos < errCNeg) ? errCPos : errCNeg;

        if (errA <= ToleranceS)
        {
            // Matched main phase - update reference
            NextATime = eventTime + BeatPeriod;
            BeatPeriod += PeriodGain * phase;
            ConsecutiveMisses = 0;
            LastMatchTime = eventTime;
            return 1;
        }
        if (errC <= ToleranceS)
        {
            // Matched companion phase. Don't advance next_a_time, but refine g.
            if (g > 0)
            {
                double nudge = (errCPos < errCNeg) ? (phase - g) : -(phase + g);
                AcOffset += AcGain * nudge;
            }
            ConsecutiveMisses = 0;
            LastMatchTime = eventTime;
            return 1;
        }

        /* Event didn't match either expected phase. If we're far past the
         * predicted next A, advance the window. */
        while (eventTime > NextATime + 1.5 * BeatPeriod)
        {
            NextATime += BeatPeriod;
        }

        ConsecutiveMisses++;
        if (ConsecutiveMisses >= MaxMisses)
        {
            Synced = 0;
        }
        return 0;
    }

    // tg_sync_check_timeout
    public int CheckTimeout(double streamTime)
    {
        if (Synced == 0) return 0;
        int timeoutBeats = MaxMisses / 2;
        if (timeoutBeats < 3) timeoutBeats = 3;
        double timeout = (double)timeoutBeats * BeatPeriod;
        if (streamTime - LastMatchTime > timeout)
        {
            Synced = 0;
            return 1;
        }
        return 0;
    }
}
