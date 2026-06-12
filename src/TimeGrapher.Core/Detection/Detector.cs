/* Detector.cs -- silence-based burst detector.
 * Port of Detector.h / Detector.cpp.
 *
 * Segments the envelope into SILENCE and BURST regions and emits two
 * events per accepted burst:
 *   A (IsOnset = true)  : silence -> signal threshold crossing (linear interp).
 *   C (IsOnset = false) : peak of the burst envelope (parabolic interp),
 *                         plus V5.4 C-onset metadata (backward walk).
 *
 * All adaptive state (noise floor percentile ring, peak median ring,
 * regime ring, env ring) is preserved bit-for-bit. Integer indices are
 * ulong (uint64_t); envelope samples are double internally, float in the
 * ring buffer, exactly as in the C++ original.
 */

namespace TimeGrapher.Core.Detection;

/// <summary>Raw event emitted by the detector (tg_raw_event_t).</summary>
internal struct TgRawEvent
{
    public ulong SampleIndex;        // integer absolute sample index
    public double SubSampleOffset;   // in [-0.5, +0.5]
    public double TimeSeconds;       // (sample_index + offset) / fs
    public float PeakValue;          // burst peak envelope value
    public int IsOnset;              // 1 = A (onset), 0 = C (peak)

    /* V5.4: C-event onset metadata. */
    public ulong OnsetSampleIndex;
    public double OnsetSubSampleOffset;
    public double OnsetTimeSeconds;
    public int OnsetValid;
}

/// <summary>
/// Silence-based burst detector (tg_detector_t). Plain procedural state
/// ported from Detector.cpp; one instance per stream.
/// </summary>
internal sealed class TgDetectorCore
{
    /* Ring-buffer sizes (compile-time constants). */
    public const int TG_NOISE_HISTORY_N = 256; // ~256 ms at 1 ms/sample
    public const int TG_PEAK_HISTORY_N = 16;    // ~2 s at 8 Hz beat rate

    /* V5.6 regime-change detector constants. */
    public const int TG_REGIME_RING_N = 8;       // short-term burst peak ring
    public const double TG_REGIME_RATIO = 10.0;  // trip if new peak >= ratio * recent min
    public const double TG_REGIME_FLOOR = 0.001; // skip ratio check when both peaks below this
    public const double TG_REGIME_COOLDOWN_S = 1.0; // seconds between resets

    /* ---- configuration ---- */
    public double Fs;                 // sample rate
    public double NoiseAlpha;         // LPF coeff for EMA fallback noise floor
    public double CeilAlpha;          // LPF coeff for signal_ceiling bootstrap
    public ulong WarmupSamples;       // skip first N samples

    /* Tunable gate fractions. Clamped to [0.001, 0.9] by the setters. */
    public double OnsetFraction;
    public double MinPeakFraction;

    /* V4.5: minimum wall-clock A-to-A interval. */
    public ulong MinAIntervalSamples;
    public ulong SamplesSinceLastA;   // wall-clock counter

    /* ---- opt-in robustness options (config, preserved across Reset; all
     * defaults set in Init keep the V5.x behavior bit-identical) ---- */
    public bool AdaptiveFloorEnabled;
    public double RejectedPeakMinSnr;
    public int RejectedPeakMinCount;
    public double AdaptiveFloorMinMul;
    public double RefDecayAfterS;
    public double RefDecayTauS;
    public bool NoiseCensorEnabled;
    public double NoiseCensorK;
    public int NoiseCensorMaxRun;
    public bool RegimeGuardEnabled;
    public int RegimeTripBeats;

    /* ---- noise floor: 75th percentile of downsampled silence samples ---- */
    public readonly double[] NoiseHistory = new double[TG_NOISE_HISTORY_N];
    public int NoiseHistoryCount;
    public int NoiseHistoryHead;
    public ulong NoiseLastSampleIdx;
    public ulong NoiseSampleInterval;  // samples between decimated picks
    public double NoisePercentileCache;
    public double NoiseFloor;          // EMA min-tracker (bootstrap only)

    /* ---- reference peak: median of last N accepted burst peaks ---- */
    public readonly double[] PeakHistory = new double[TG_PEAK_HISTORY_N];

    /* Sort scratch for the median / 75th-percentile caches. The detector is
     * driven by a single analysis thread and the percentile cache refreshes once
     * per ~1 ms of silence, so reusing these keeps that path allocation-free. */
    private readonly double[] _medianSortScratch = new double[TG_PEAK_HISTORY_N];
    private readonly double[] _noiseSortScratch = new double[TG_NOISE_HISTORY_N];
    public int PeakHistoryCount;
    public int PeakHistoryHead;
    public double MedianPeakCache;
    public double SignalCeiling;       // max-hold fallback used pre-history

    /* ---- V5.6: regime-change detector ---- */
    public readonly double[] RegimePeakRing = new double[TG_REGIME_RING_N];
    public int RegimePeakCount;
    public int RegimePeakHead;
    public ulong RegimeLastResetIdx;   // abs sample idx of last reset, 0 = never
    public int RegimeResetPending;     // set on trip, cleared by lib after flush

    /* ---- wall-clock gates ---- */
    public ulong SilenceSamples;       // samples since burst end (capped)
    public ulong MinSilenceSamples;    // threshold for new A onset
    public ulong BurstEndSamples;      // threshold for C emission

    /* ---- state machine ---- */
    public int InBurst;

    /* onset (A) state */
    public ulong BurstStartIdx;
    public double BurstStartTime;
    public double BurstStartOffset;

    /* peak (C) tracking */
    public double BurstMax;
    public ulong BurstMaxIdx;
    public double BurstMaxYMinus1;
    public double BurstMaxYPlus1;
    public int HavePeakPlus1;

    /* V5.2: separate C-peak tracking. */
    public ulong CSearchSkipSamples;
    public int CHavePeak;
    public double BurstCMax;
    public ulong BurstCIdx;
    public double BurstCYMinus1;
    public double BurstCYPlus1;
    public int CHavePeakPlus1;

    /* V5.4: C-onset detection via backward walk from burst_c_idx. */
    public float[]? EnvRing;
    public int EnvRingCapacity;
    public int EnvRingHead;            // next write position; wraps
    public ulong EnvRingNewestAbs;     // abs sample index of most-recent write; 0 if empty
    public int EnvRingHasData;         // 1 once at least one sample has been written

    /* C-onset detection parameters. */
    public ulong COnsetDwellSamples;        // default ~0.3 ms
    public ulong COnsetSearchMaxSamples;    // default 5 ms (fixed; never re-tuned, matching the original)

    /* misc */
    public double PrevSample;
    public ulong TotalSamples;

    // tg_detector_reset
    public void Reset()
    {
        InBurst = 0;
        BurstStartIdx = 0;
        BurstStartTime = 0.0;
        BurstStartOffset = 0.0;
        BurstMax = 0.0;
        BurstMaxIdx = 0;
        BurstMaxYMinus1 = 0.0;
        BurstMaxYPlus1 = 0.0;
        HavePeakPlus1 = 0;
        /* V5.2 */
        CHavePeak = 0;
        BurstCMax = 0.0;
        BurstCIdx = 0;
        BurstCYMinus1 = 0.0;
        BurstCYPlus1 = 0.0;
        CHavePeakPlus1 = 0;
        PrevSample = 0.0;
        TotalSamples = 0;
        /* Start with a large virtual silence credit so the first real tick
         * isn't blocked by the silence gate. */
        SilenceSamples = 1UL << 30;

        /* V4.5: A-to-A counter starts large too. The threshold
         * (min_a_interval_samples) is preserved (config, not runtime state). */
        SamplesSinceLastA = 1UL << 30;

        /* Bootstrap fallbacks */
        NoiseFloor = 1e-6;
        SignalCeiling = 1e-4;

        /* Clear both history buffers and their caches */
        for (int i = 0; i < TG_PEAK_HISTORY_N; ++i) PeakHistory[i] = 0.0;
        PeakHistoryCount = 0;
        PeakHistoryHead = 0;
        MedianPeakCache = 0.0;
        for (int i = 0; i < TG_NOISE_HISTORY_N; ++i) NoiseHistory[i] = 0.0;
        NoiseHistoryCount = 0;
        NoiseHistoryHead = 0;
        NoiseLastSampleIdx = 0;
        NoisePercentileCache = 0.0;

        /* V5.6: regime-change detector. last_reset_idx is NOT cleared here. */
        for (int i = 0; i < TG_REGIME_RING_N; ++i) RegimePeakRing[i] = 0.0;
        RegimePeakCount = 0;
        RegimePeakHead = 0;
        RegimeResetPending = 0;

        /* V5.4: clear envelope ring buffer (don't free; kept across resets). */
        if (EnvRing != null && EnvRingCapacity > 0)
        {
            for (int i = 0; i < EnvRingCapacity; ++i) EnvRing[i] = 0.0f;
        }
        EnvRingHead = 0;
        EnvRingNewestAbs = 0;
        EnvRingHasData = 0;
    }

    // tg_detector_init
    public void Init(double fs)
    {
        Fs = fs;

        /* EMA time constants used only for bootstrap fallbacks. */
        NoiseAlpha = 1.0 - Math.Exp(-1.0 / (0.3 * fs));  // tau 0.3 s
        CeilAlpha = 1.0 - Math.Exp(-1.0 / (1.5 * fs));   // tau 1.5 s

        WarmupSamples = (ulong)(0.2 * fs);  // skip first 200 ms

        /* Pre-sync defaults: min_silence = 20 ms, burst_end = 10 ms. */
        MinSilenceSamples = (ulong)(0.020 * fs);
        BurstEndSamples = (ulong)(0.010 * fs);

        /* Sample the envelope into the noise-percentile buffer once per ~1 ms. */
        NoiseSampleInterval = (ulong)(0.001 * fs);
        if (NoiseSampleInterval < 1) NoiseSampleInterval = 1;

        /* Default gate fractions. */
        OnsetFraction = 0.03;
        MinPeakFraction = 0.20;

        /* V4.5: A-to-A interval gate disabled at init / pre-sync. */
        MinAIntervalSamples = 0;

        /* Robustness options all off by default (V5.x bit-identical). The
         * TgDetector ctor overload overrides these from TgDetectorOptions. */
        AdaptiveFloorEnabled = false;
        RejectedPeakMinSnr = 2.0;
        RejectedPeakMinCount = 8;
        AdaptiveFloorMinMul = 3.0;
        RefDecayAfterS = 2.0;
        RefDecayTauS = 5.0;
        NoiseCensorEnabled = false;
        NoiseCensorK = 3.5;
        NoiseCensorMaxRun = 128;
        RegimeGuardEnabled = false;
        RegimeTripBeats = 3;

        /* V5.2: C-search skip default 3 ms. */
        CSearchSkipSamples = (ulong)(0.003 * fs);

        /* V5.4: C-onset detection defaults.
         * Search bound 5 ms pre-sync. Dwell 0.3 ms (min 2 samples). */
        COnsetSearchMaxSamples = (ulong)(0.005 * fs);
        COnsetDwellSamples = (ulong)(0.0003 * fs);
        if (COnsetDwellSamples < 2) COnsetDwellSamples = 2;

        /* Allocate the recent-envelope ring buffer (50 ms, min 256). */
        EnvRing = null;
        EnvRingCapacity = (int)(0.050 * fs);
        if (EnvRingCapacity < 256) EnvRingCapacity = 256;
        EnvRing = new float[EnvRingCapacity]; // calloc -> zero-initialized
        EnvRingHead = 0;
        EnvRingNewestAbs = 0;
        EnvRingHasData = 0;

        /* V5.6: last_reset_idx set once at init to 0. Not cleared by Reset. */
        RegimeLastResetIdx = 0;

        Reset();
    }

    // tg_detector_set_c_search_skip
    public void SetCSearchSkip(double skipS)
    {
        if (skipS < 0.0) skipS = 0.0;
        CSearchSkipSamples = (ulong)(skipS * Fs);
    }

    // tg_detector_set_min_silence
    public void SetMinSilence(double minSilenceS)
    {
        ulong s = (ulong)(minSilenceS * Fs);
        if (s < 2) s = 2;
        MinSilenceSamples = s;
        /* burst_end is half of min_silence. */
        BurstEndSamples = s / 2;
        if (BurstEndSamples < 2) BurstEndSamples = 2;
    }

    // tg_detector_set_min_a_interval
    public void SetMinAInterval(double minAS)
    {
        if (minAS <= 0.0)
        {
            MinAIntervalSamples = 0;  // disable
        }
        else
        {
            MinAIntervalSamples = (ulong)(minAS * Fs);
        }
    }

    // parabolic_offset
    private static double ParabolicOffset(double yM1, double y0, double yP1)
    {
        double denom = (yM1 - 2.0 * y0 + yP1);
        if (Math.Abs(denom) < 1e-20) return 0.0;
        double off = 0.5 * (yM1 - yP1) / denom;
        if (off < -0.5) off = -0.5;
        if (off > 0.5) off = 0.5;
        return off;
    }

    /* V5.4: read the envelope sample at absolute index `abs` from the
     * ring buffer. Returns 0 if `abs` is out of range. */
    // env_ring_at
    private double EnvRingAt(ulong abs)
    {
        if (EnvRing == null || EnvRingCapacity == 0) return 0.0;
        if (EnvRingHasData == 0) return 0.0;
        ulong newestAbs = EnvRingNewestAbs;
        if (abs > newestAbs) return 0.0;
        ulong age = newestAbs - abs;
        if (age >= (ulong)EnvRingCapacity) return 0.0;
        /* The most-recent slot is at (head - 1) mod cap. */
        int newestSlot = (EnvRingHead + EnvRingCapacity - 1) % EnvRingCapacity;
        int idx = (int)(((ulong)newestSlot + (ulong)EnvRingCapacity - age) % (ulong)EnvRingCapacity);
        return (double)EnvRing[idx];
    }

    /* V5.4: find the C-cluster onset by walking backward from the C peak. */
    // find_c_onset
    private int FindCOnset(ulong cPeakIdx, double cPeakValue,
                           out ulong onsetIdxOut, out double onsetSubOffOut)
    {
        onsetIdxOut = 0;
        onsetSubOffOut = 0.0;

        if (EnvRing == null || EnvRingCapacity == 0) return 0;
        if (cPeakIdx == 0) return 0;

        double threshold = 0.5 * cPeakValue;
        if (threshold <= 0.0) return 0;

        ulong searchMax = COnsetSearchMaxSamples;
        if (searchMax == 0) return 0;

        /* Don't walk past where the burst started + skip window. */
        ulong earliestIdx;
        {
            ulong skipEnd = BurstStartIdx + CSearchSkipSamples;
            ulong windowLo = (cPeakIdx > searchMax) ? (cPeakIdx - searchMax) : 0;
            earliestIdx = (skipEnd > windowLo) ? skipEnd : windowLo;
        }
        /* Also bounded by what the ring buffer holds. */
        {
            ulong ringOldest = (EnvRingNewestAbs >= (ulong)EnvRingCapacity - 1)
                                   ? (EnvRingNewestAbs - ((ulong)EnvRingCapacity - 1))
                                   : 0;
            if (earliestIdx < ringOldest) earliestIdx = ringOldest;
        }
        if (earliestIdx >= cPeakIdx) return 0;

        /* Walk back: track consecutive below-threshold samples. */
        ulong consecutiveBelow = 0;
        int haveAbove = 0;
        ulong lastAboveIdx = 0;
        double lastAboveVal = 0.0;

        /* Start from one sample earlier than the peak. */
        for (ulong i = cPeakIdx; i > earliestIdx;)
        {
            --i;
            double v = EnvRingAt(i);
            if (v >= threshold)
            {
                haveAbove = 1;
                lastAboveIdx = i;
                lastAboveVal = v;
                consecutiveBelow = 0;
            }
            else
            {
                consecutiveBelow++;
                if (haveAbove != 0 && consecutiveBelow >= COnsetDwellSamples)
                {
                    /* Onset is the threshold crossing between the sample
                     * just before last_above_idx (env < thr) and
                     * last_above_idx (env >= thr). Linear interp. */
                    double vPrev = (lastAboveIdx > 0)
                                       ? EnvRingAt(lastAboveIdx - 1)
                                       : 0.0;
                    double frac = 0.0;
                    if (lastAboveVal > vPrev)
                    {
                        /* monotonic rise across the crossing */
                        frac = (threshold - vPrev) / (lastAboveVal - vPrev);
                        if (frac < 0.0) frac = 0.0;
                        if (frac > 1.0) frac = 1.0;
                    }
                    /* sample_index convention: integer index just at or
                     * after the crossing; sub_off in [-0.5, +0.5]. */
                    onsetIdxOut = lastAboveIdx;
                    onsetSubOffOut = frac - 1.0;  // in [-1, 0]
                    /* Renormalize to canonical [-0.5, +0.5] range. */
                    if (onsetSubOffOut < -0.5)
                    {
                        onsetSubOffOut += 1.0;
                        if (onsetIdxOut > 0) onsetIdxOut--;
                    }
                    return 1;
                }
            }
        }
        /* Reached the search bound without finding a clean dwell. */
        return 0;
    }

    /* Insertion-sort a small array in place. N <= 256 so this is fine. */
    // insertion_sort
    private static void InsertionSort(double[] a, int n)
    {
        for (int i = 1; i < n; ++i)
        {
            double x = a[i];
            int j = i - 1;
            while (j >= 0 && a[j] > x) { a[j + 1] = a[j]; --j; }
            a[j + 1] = x;
        }
    }

    /* Recompute cached median after inserting a peak. */
    // update_median_cache
    private void UpdateMedianCache()
    {
        int n = PeakHistoryCount;
        if (n < 1) { MedianPeakCache = 0.0; return; }
        double[] tmp = _medianSortScratch;
        for (int i = 0; i < n; ++i) tmp[i] = PeakHistory[i];
        InsertionSort(tmp, n);
        if ((n & 1) != 0)
        {
            MedianPeakCache = tmp[n / 2];
        }
        else
        {
            MedianPeakCache = 0.5 * (tmp[n / 2 - 1] + tmp[n / 2]);
        }
    }

    /* V5.6: regime-change detector helpers. */
    // regime_ring_min
    private double RegimeRingMin()
    {
        if (RegimePeakCount <= 0) return 0.0;
        double m = RegimePeakRing[0];
        for (int i = 1; i < RegimePeakCount; ++i)
        {
            if (RegimePeakRing[i] < m) m = RegimePeakRing[i];
        }
        return m;
    }

    // regime_push_peak
    private void RegimePushPeak(double peak, ulong absIdx)
    {
        /* Check for regime trip BEFORE storing the new peak. */
        if (RegimePeakCount >= 4)
        {
            double prevMin = RegimeRingMin();
            bool aboveFloor = (peak >= TG_REGIME_FLOOR && prevMin >= TG_REGIME_FLOOR);
            bool absFloorJump = (peak >= TG_REGIME_FLOOR && prevMin < TG_REGIME_FLOOR);
            /* Trip if both peaks are above the floor and the ratio is
             * large enough, OR if jumping from noise to signal. */
            bool ratioOk = aboveFloor && (peak >= TG_REGIME_RATIO * prevMin);
            int trip = (ratioOk || absFloorJump) ? 1 : 0;
            /* Apply cooldown. */
            if (trip != 0 && RegimeLastResetIdx > 0)
            {
                ulong since = absIdx - RegimeLastResetIdx;
                ulong cooldown = (ulong)(TG_REGIME_COOLDOWN_S * Fs);
                if (since < cooldown) trip = 0;
            }
            if (trip != 0)
            {
                RegimeResetPending = 1;
                RegimeLastResetIdx = absIdx;
            }
        }
        /* Always store the new peak into the ring. */
        RegimePeakRing[RegimePeakHead] = peak;
        RegimePeakHead = (RegimePeakHead + 1) % TG_REGIME_RING_N;
        if (RegimePeakCount < TG_REGIME_RING_N) RegimePeakCount++;
    }

    /* Push a newly-observed peak into the ring buffer and refresh median. */
    // push_peak
    private void PushPeak(double peak, ulong absIdx)
    {
        PeakHistory[PeakHistoryHead] = peak;
        PeakHistoryHead = (PeakHistoryHead + 1) % TG_PEAK_HISTORY_N;
        if (PeakHistoryCount < TG_PEAK_HISTORY_N) PeakHistoryCount++;
        UpdateMedianCache();
        RegimePushPeak(peak, absIdx);
    }

    /* Compute 75th percentile of noise_history using partial sort. */
    // update_noise_percentile_cache
    private void UpdateNoisePercentileCache()
    {
        int n = NoiseHistoryCount;
        if (n < 1) { NoisePercentileCache = 0.0; return; }
        double[] tmp = _noiseSortScratch;
        for (int i = 0; i < n; ++i) tmp[i] = NoiseHistory[i];
        InsertionSort(tmp, n);
        /* 75th percentile index */
        int idx = (3 * (n - 1)) / 4;
        NoisePercentileCache = tmp[idx];
    }

    /* Record one downsampled silence-region envelope sample. */
    // push_noise_sample
    private void PushNoiseSample(double e)
    {
        NoiseHistory[NoiseHistoryHead] = e;
        NoiseHistoryHead = (NoiseHistoryHead + 1) % TG_NOISE_HISTORY_N;
        if (NoiseHistoryCount < TG_NOISE_HISTORY_N) NoiseHistoryCount++;
        UpdateNoisePercentileCache();
    }

    /* Return the current "effective noise floor" for threshold computation. */
    // effective_noise
    private double EffectiveNoise()
    {
        if (NoiseHistoryCount < 32)
        {
            return NoiseFloor;
        }
        double p = NoisePercentileCache;
        double f = NoiseFloor;
        return (p > f) ? p : f;
    }

    /* Compute the current reference peak level used to derive thresholds. */
    // reference_peak
    private double ReferencePeak()
    {
        double floorRef = 10.0 * NoiseFloor;
        double r;
        if (PeakHistoryCount == 0)
        {
            r = SignalCeiling;
        }
        else if (PeakHistoryCount < 4)
        {
            r = 0.0;
            for (int i = 0; i < PeakHistoryCount; ++i)
            {
                if (PeakHistory[i] > r) r = PeakHistory[i];
            }
        }
        else
        {
            r = MedianPeakCache;
        }
        return (r > floorRef) ? r : floorRef;
    }

    /* Compute the current thresholds from the noise floor and reference peak. */
    // compute_thresholds
    private void ComputeThresholds(out double effNoise, out double refPeak,
                                   out double span, out double onsetThr, out double minPeakThr)
    {
        double n = EffectiveNoise();
        double r = ReferencePeak();
        double sp = r - n;
        if (sp < n * 2.0) sp = n * 2.0;
        effNoise = n;
        refPeak = r;
        span = sp;
        onsetThr = n + OnsetFraction * sp;
        minPeakThr = n + MinPeakFraction * sp;
    }

    // tg_detector_get_thresholds
    public void GetThresholds(out double onsetThr, out double minPeakThr,
                              out double effNoise, out double refPeak)
    {
        ComputeThresholds(out effNoise, out refPeak, out _, out onsetThr, out minPeakThr);
    }

    // clamp_fraction
    private static double ClampFraction(double f)
    {
        if (f < 0.001) f = 0.001;
        if (f > 0.9) f = 0.9;
        return f;
    }

    // tg_detector_set_onset_fraction
    public void SetOnsetFraction(double frac) => OnsetFraction = ClampFraction(frac);

    // tg_detector_set_min_peak_fraction
    public void SetMinPeakFraction(double frac) => MinPeakFraction = ClampFraction(frac);

    /* Process a block of envelope samples. Appends up to maxEvents-outCount
     * events to outEvents, increments outCount, returns events added. */
    // tg_detector_process
    public int Process(ReadOnlySpan<float> envelope, int n,
                       TgRawEvent[] outEvents, ref int outCount, int maxEvents)
    {
        int produced = 0;

        for (int i = 0; i < n; ++i)
        {
            double e = (double)envelope[i];
            ulong absIdx = TotalSamples + (ulong)i;

            /* V5.4: write current envelope sample into the ring buffer. */
            if (EnvRing != null && EnvRingCapacity > 0)
            {
                EnvRing[EnvRingHead] = (float)e;
                EnvRingHead = (EnvRingHead + 1) % EnvRingCapacity;
                EnvRingNewestAbs = absIdx;
                EnvRingHasData = 1;
            }

            /* EMA-tracked noise floor: snap-down min-tracker. */
            if (InBurst == 0)
            {
                if (e < NoiseFloor)
                {
                    NoiseFloor = e;
                }
                else
                {
                    NoiseFloor += NoiseAlpha * (e - NoiseFloor);
                }
                if (NoiseFloor < 1e-9) NoiseFloor = 1e-9;

                /* Record one silence sample per ~1ms. */
                if (absIdx >= NoiseLastSampleIdx + NoiseSampleInterval)
                {
                    PushNoiseSample(e);
                    NoiseLastSampleIdx = absIdx;
                }
            }

            /* Keep the legacy signal_ceiling updated (for diagnostics). */
            if (e > SignalCeiling)
            {
                SignalCeiling = e;
            }
            else
            {
                SignalCeiling += CeilAlpha * (e - SignalCeiling);
            }
            if (SignalCeiling < NoiseFloor * 3.0)
            {
                SignalCeiling = NoiseFloor * 3.0;
            }

            if (absIdx < WarmupSamples)
            {
                PrevSample = e;
                continue;
            }

            /* Thresholds anchored to median of recent peak heights. */
            ComputeThresholds(out _, out _, out double span, out double onsetThr, out double _);

            /* Silence gate is wall-clock based. Increment silence_samples
             * every sample we're NOT in a burst, unconditionally. */
            if (InBurst == 0)
            {
                SilenceSamples++;
            }
            /* V4.5: count wall-clock samples since last A onset. Saturate. */
            if (SamplesSinceLastA < (1UL << 60))
            {
                SamplesSinceLastA++;
            }

            if (InBurst == 0)
            {
                /* Silence -> burst transition. Two gates must BOTH pass. */
                ulong minSilenceSamples = MinSilenceSamples;
                if (e > onsetThr && PrevSample <= onsetThr &&
                    SilenceSamples >= minSilenceSamples &&
                    SamplesSinceLastA >= MinAIntervalSamples)
                {
                    InBurst = 1;
                    SilenceSamples = 0;
                    /* NOTE: samples_since_last_a is NOT reset here. */

                    /* Sub-sample crossing time: linear interpolation. */
                    double frac = 0.0;
                    double denom = e - PrevSample;
                    if (denom > 1e-20)
                    {
                        frac = (onsetThr - PrevSample) / denom;
                        if (frac < 0.0) frac = 0.0;
                        if (frac > 1.0) frac = 1.0;
                    }
                    /* Crossing is at time (abs_idx - 1 + frac). */
                    ulong idx = absIdx - 1;
                    double sub = frac;
                    if (sub > 0.5) { idx += 1; sub -= 1.0; }
                    BurstStartIdx = idx;
                    BurstStartOffset = sub;
                    BurstStartTime = ((double)idx + sub) / Fs;

                    /* Reset burst max tracking. */
                    BurstMax = e;
                    BurstMaxIdx = absIdx;
                    BurstMaxYMinus1 = PrevSample;
                    HavePeakPlus1 = 0;

                    /* V5.2: reset C-peak tracker. */
                    CHavePeak = 0;
                    BurstCMax = 0.0;
                    BurstCIdx = 0;
                    CHavePeakPlus1 = 0;

                    /* DON'T emit A yet -- defer until burst ends. */
                }
            }
            else
            {
                /* Inside a burst. Track running max for the C event. */
                if (e > BurstMax)
                {
                    BurstMax = e;
                    BurstMaxIdx = absIdx;
                    BurstMaxYMinus1 = PrevSample;
                    HavePeakPlus1 = 0;
                }
                else if (HavePeakPlus1 == 0 && absIdx == BurstMaxIdx + 1)
                {
                    BurstMaxYPlus1 = e;
                    HavePeakPlus1 = 1;
                }

                /* V5.2: independently track C-peak only past the skip window. */
                ulong samplesSinceBurst = absIdx - BurstStartIdx;
                if (samplesSinceBurst >= CSearchSkipSamples)
                {
                    if (CHavePeak == 0 || e > BurstCMax)
                    {
                        BurstCMax = e;
                        BurstCIdx = absIdx;
                        BurstCYMinus1 = PrevSample;
                        CHavePeak = 1;
                        CHavePeakPlus1 = 0;
                    }
                    else if (CHavePeakPlus1 == 0 && absIdx == BurstCIdx + 1)
                    {
                        BurstCYPlus1 = e;
                        CHavePeakPlus1 = 1;
                    }
                }

                ulong samplesSincePeak = absIdx - BurstMaxIdx;

                if (samplesSincePeak >= BurstEndSamples)
                {
                    /* Minimum tick height: fraction of the dynamic range. */
                    double minPeak = EffectiveNoise() + MinPeakFraction * span;
                    bool isRealTick = (BurstMax >= minPeak);

                    if (isRealTick)
                    {
                        /* Emit A first (at burst start onset time) */
                        if (produced + outCount < maxEvents)
                        {
                            ref TgRawEvent ev = ref outEvents[outCount + produced];
                            ev.SampleIndex = BurstStartIdx;
                            ev.SubSampleOffset = BurstStartOffset;
                            ev.TimeSeconds = BurstStartTime;
                            ev.PeakValue = (float)BurstMax;
                            ev.IsOnset = 1;
                            /* V5.4: A events don't carry onset metadata. */
                            ev.OnsetSampleIndex = 0;
                            ev.OnsetSubSampleOffset = 0.0;
                            ev.OnsetTimeSeconds = 0.0;
                            ev.OnsetValid = 0;
                            produced++;
                        }
                        /* V4.5: reset A-to-A counter to (current - burst_start)
                         * so it reflects time since A onset. */
                        ulong burstDurSamples = absIdx - BurstStartIdx;
                        SamplesSinceLastA = burstDurSamples;
                        /* V5.2: pick C-peak (post-skip) if available; else
                         * fall back to burst_max. */
                        double cMaxV;
                        ulong cMaxIdx;
                        double cYM1, cYP1;
                        int cHaveP1;
                        if (CHavePeak != 0)
                        {
                            cMaxV = BurstCMax;
                            cMaxIdx = BurstCIdx;
                            cYM1 = BurstCYMinus1;
                            cYP1 = BurstCYPlus1;
                            cHaveP1 = CHavePeakPlus1;
                        }
                        else
                        {
                            cMaxV = BurstMax;
                            cMaxIdx = BurstMaxIdx;
                            cYM1 = BurstMaxYMinus1;
                            cYP1 = BurstMaxYPlus1;
                            cHaveP1 = HavePeakPlus1;
                        }

                        /* Emit C (at burst peak, with parabolic interp) */
                        double subOff = 0.0;
                        if (cHaveP1 != 0)
                        {
                            subOff = ParabolicOffset(cYM1, cMaxV, cYP1);
                        }

                        /* V5.4: find C-cluster onset by walking back. */
                        int onsetValid = FindCOnset(cMaxIdx, cMaxV,
                                                    out ulong onsetIdx, out double onsetSubOff);

                        if (produced + outCount < maxEvents)
                        {
                            ref TgRawEvent ev = ref outEvents[outCount + produced];
                            ev.SampleIndex = cMaxIdx;
                            ev.SubSampleOffset = subOff;
                            ev.TimeSeconds = ((double)cMaxIdx + subOff) / Fs;
                            ev.PeakValue = (float)cMaxV;
                            ev.IsOnset = 0;
                            /* V5.4: onset metadata */
                            ev.OnsetSampleIndex = onsetIdx;
                            ev.OnsetSubSampleOffset = onsetSubOff;
                            ev.OnsetTimeSeconds =
                                onsetValid != 0
                                    ? ((double)onsetIdx + onsetSubOff) / Fs
                                    : 0.0;
                            ev.OnsetValid = onsetValid;
                            produced++;
                        }
                        /* Credit the wall-clock time since peak toward the
                         * next silence gate. */
                        SilenceSamples = samplesSincePeak;
                        /* Record this real peak in the height history. Use
                         * burst_max. abs_idx for V5.6 regime detector. */
                        PushPeak(BurstMax, absIdx);
                    }
                    else
                    {
                        /* Noise bump rejected - pretend the whole episode
                         * was quiet so the silence-gate is open. */
                        SilenceSamples = 1000000;
                    }
                    InBurst = 0;
                }
            }

            PrevSample = e;
        }

        TotalSamples += (ulong)n;
        outCount += produced;
        return produced;
    }

    /* V5.6: atomic read-and-clear of the regime reset flag. */
    // tg_detector_consume_regime_reset
    public int ConsumeRegimeReset()
    {
        if (RegimeResetPending == 0) return 0;
        RegimeResetPending = 0;
        return 1;
    }
}
