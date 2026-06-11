/* TgDetector.cs -- public API entry point and pipeline glue.
 * Port of Timegrapher.cpp (the tg_context struct + tg_* functions).
 *
 * Wires the per-stage primitives (Dsp, Detector, Bph) into a single
 * streaming pipeline:
 *
 *   1. DC blocker        (TgHpf, default 200 Hz)
 *   2. Envelope          (TgEnvelope, 0.15 ms LPF)
 *   3. Detector          (TgDetectorCore) -> A/C events
 *   4. BPH detection      (Bph.PickByPhase) -> lock
 *   5. Sync PLL           (TgSync) -> track / lose
 *   6. Delay-line / public events -> TgEvent emitted to the caller.
 *
 * tg_init/tg_destroy map to the constructor (managed memory -> no
 * IDisposable needed). tg_process returned int (0 success); the only
 * failure paths were argument validation and allocation, so Process is
 * void here (see deviations note in the report).
 */

namespace TimeGrapher.Core.Detection;

public sealed class TgDetector
{
    private const int TG_INITIAL_BUF = 4096;
    private const int TG_EVENT_HISTORY = 256;

    private readonly TgConfig _cfg;

    /* DSP chain */
    private readonly TgHpf _hpf;
    private readonly TgEnvelope _env;
    private readonly TgDetectorCore _det;

    /* BPH and sync tracker */
    private readonly TgSync _sync;
    private int _currentBph;
    private double _currentBeatPeriod;

    /* Working buffers (HPF output, envelope pre-delay). */
    private float[] _bufFilt = Array.Empty<float>();
    private float[] _bufEnv = Array.Empty<float>();
    private int _bufCapacity;

    /* Envelope delay line (50 ms, fixed) for event/PCM alignment. */
    private readonly float[] _delayBuf;
    private readonly int _delayCapacity;
    private readonly int _delaySamples;       // effective delay
    private int _delayWriteIdx;
    private int _delayFilled;
    private ulong _totalEnvSamplesIn;

    /* User-facing delayed envelope output buffer. */
    private float[] _bufEnvOut = Array.Empty<float>();
    private int _bufEnvOutCapacity;

    /* Raw events out of the detector for this batch. */
    private TgRawEvent[] _rawEvents = Array.Empty<TgRawEvent>();
    private int _rawEventsCapacity;

    /* Rolling history of recent A event times for BPH detection. */
    private readonly double[] _evHistory = new double[TG_EVENT_HISTORY];
    private int _evHistoryHead;
    private int _evHistoryCount;

    // tg_init
    public TgDetector(TgConfig cfg)
    {
        // tg_init: if (!cfg_in || cfg_in->sample_rate <= 0.0) return NULL;
        if (cfg == null || cfg.SampleRate <= 0.0)
            throw new ArgumentException("Invalid TgConfig: sample_rate must be > 0", nameof(cfg));

        if (cfg.BphMode == TgBphMode.Manual && !Bph.IsValidManualBph(cfg.ManualBph))
            throw new ArgumentException("Invalid TgConfig: manual_bph not in manual list", nameof(cfg));

        /* Copy config (ctx->cfg = *cfg_in) then apply zero-defaults. */
        _cfg = new TgConfig
        {
            SampleRate = cfg.SampleRate,
            BphMode = cfg.BphMode,
            ManualBph = cfg.ManualBph,
            HpfCutoffHz = cfg.HpfCutoffHz,
            EnvelopeSmoothMs = cfg.EnvelopeSmoothMs,
            EventMinSeparationMs = cfg.EventMinSeparationMs,
            SyncTolerancePct = cfg.SyncTolerancePct,
            AutoDetectSeconds = cfg.AutoDetectSeconds,
            SyncLossMisses = cfg.SyncLossMisses,
            PllPeriodGain = cfg.PllPeriodGain,
            PllAcGain = cfg.PllAcGain,
            OnsetFractionInit = cfg.OnsetFractionInit,
            MinPeakFractionInit = cfg.MinPeakFractionInit,
            SuppressPreSyncEvents = cfg.SuppressPreSyncEvents,
            CPlacement = cfg.CPlacement,
        };

        /* Apply zero-defaults at runtime */
        if (_cfg.HpfCutoffHz == 0.0) _cfg.HpfCutoffHz = 200.0;
        if (_cfg.EnvelopeSmoothMs == 0.0) _cfg.EnvelopeSmoothMs = 0.15;
        if (_cfg.EventMinSeparationMs == 0.0) _cfg.EventMinSeparationMs = 2.0;
        if (_cfg.SyncTolerancePct == 0.0) _cfg.SyncTolerancePct = 3.0;
        if (_cfg.AutoDetectSeconds == 0.0) _cfg.AutoDetectSeconds = 1.5;
        if (_cfg.SyncLossMisses == 0) _cfg.SyncLossMisses = 12;
        if (_cfg.PllPeriodGain == 0.0) _cfg.PllPeriodGain = 0.01;
        if (_cfg.PllAcGain == 0.0) _cfg.PllAcGain = 0.05;

        _hpf = new TgHpf(_cfg.SampleRate, _cfg.HpfCutoffHz);
        _env = new TgEnvelope(_cfg.SampleRate, _cfg.EnvelopeSmoothMs);
        _det = new TgDetectorCore();
        _det.Init(_cfg.SampleRate);

        /* Init-time threshold fractions */
        if (_cfg.OnsetFractionInit > 0.0)
            _det.SetOnsetFraction(_cfg.OnsetFractionInit);
        if (_cfg.MinPeakFractionInit > 0.0)
            _det.SetMinPeakFraction(_cfg.MinPeakFractionInit);

        _sync = new TgSync();
        _sync.Init();

        /* Envelope delay line: 50 ms, sample-rate dependent. */
        _delayCapacity = (int)(0.050 * _cfg.SampleRate);
        if (_delayCapacity < 16) _delayCapacity = 16;
        _delaySamples = _delayCapacity;
        _delayBuf = new float[_delayCapacity]; // calloc -> zero-initialized

        EnsureBuf(TG_INITIAL_BUF);
        EnsureRawEvents(64);
    }

    /* ----- buffer helpers ------------------------------------------------ */

    // ensure_buf
    private void EnsureBuf(int n)
    {
        if (n <= _bufCapacity) return;
        int newCap = _bufCapacity != 0 ? _bufCapacity : TG_INITIAL_BUF;
        while (newCap < n) newCap *= 2;
        Array.Resize(ref _bufFilt, newCap);
        Array.Resize(ref _bufEnv, newCap);
        _bufCapacity = newCap;
    }

    // ensure_env_out
    private void EnsureEnvOut(int n)
    {
        if (n <= _bufEnvOutCapacity) return;
        int newCap = _bufEnvOutCapacity != 0 ? _bufEnvOutCapacity : TG_INITIAL_BUF;
        while (newCap < n) newCap *= 2;
        Array.Resize(ref _bufEnvOut, newCap);
        _bufEnvOutCapacity = newCap;
    }

    // ensure_raw_events
    private void EnsureRawEvents(int n)
    {
        if (n <= _rawEventsCapacity) return;
        int newCap = _rawEventsCapacity != 0 ? _rawEventsCapacity : 64;
        while (newCap < n) newCap *= 2;
        Array.Resize(ref _rawEvents, newCap);
        _rawEventsCapacity = newCap;
    }

    /* ----- envelope delay line ------------------------------------------- */

    /* FIFO of fixed length D = delay_samples. Each input sample is
     * enqueued; if the queue has more than D samples, the oldest is
     * popped and written to `out`. */
    // delay_push_pop
    private int DelayPushPop(ReadOnlySpan<float> input, int n, Span<float> output)
    {
        int cap = _delayCapacity;
        int D = _delaySamples;
        if (cap == 0 || n == 0) return 0;
        int produced = 0;
        for (int i = 0; i < n; ++i)
        {
            if (_delayFilled >= D)
            {
                int readIdx = (_delayWriteIdx + cap - D) % cap;
                output[produced++] = _delayBuf[readIdx];
            }
            else
            {
                _delayFilled++;
            }
            _delayBuf[_delayWriteIdx] = input[i];
            _delayWriteIdx = (_delayWriteIdx + 1) % cap;
            _totalEnvSamplesIn++;
        }
        return produced;
    }

    /* ----- event history ring -------------------------------------------- */

    // push_event_history
    private void PushEventHistory(double t)
    {
        _evHistory[_evHistoryHead] = t;
        _evHistoryHead = (_evHistoryHead + 1) % TG_EVENT_HISTORY;
        if (_evHistoryCount < TG_EVENT_HISTORY) _evHistoryCount++;
    }

    // copy_history_linear
    private void CopyHistoryLinear(double[] outArr)
    {
        int cnt = _evHistoryCount;
        if (cnt == 0) return;
        if (cnt < TG_EVENT_HISTORY)
        {
            Array.Copy(_evHistory, 0, outArr, 0, cnt);
        }
        else
        {
            int head = _evHistoryHead;
            int tailLen = TG_EVENT_HISTORY - head;
            Array.Copy(_evHistory, head, outArr, 0, tailLen);
            Array.Copy(_evHistory, 0, outArr, tailLen, head);
        }
    }

    /* ============================ public API ============================ */

    // tg_process
    public void Process(ReadOnlySpan<float> pcm, TgResult result)
    {
        // tg_process: if (!ctx || !result) return -1;  -- result must be non-null.
        if (result == null) throw new ArgumentNullException(nameof(result));

        int numSamples = pcm.Length;

        /* memset(result, 0, sizeof(*result)) equivalent: clear all fields.
         * Events is reused (cleared); ProcessedPcm slice is reassigned. */
        ResetResult(result);

        /* DSP -> envelope -> delay line. */
        int envOutLen = 0;
        if (numSamples > 0)
        {
            EnsureBuf(numSamples);
            EnsureEnvOut(numSamples);
            _hpf.Process(pcm, _bufFilt, numSamples);
            _env.Process(_bufFilt, _bufEnv, numSamples);
            envOutLen = DelayPushPop(_bufEnv, numSamples, _bufEnvOut);
        }

        /* Compute absolute index of first sample of the delayed envelope. */
        ulong inputEnd = _totalEnvSamplesIn;
        ulong outputEnd = (inputEnd > (ulong)_delaySamples)
                          ? (inputEnd - (ulong)_delaySamples) : 0;
        ulong outputStart = outputEnd - (ulong)envOutLen;

        /* processed_pcm = ctx->buf_env_out (length env_out_len). Copy the
         * valid slice into result.ProcessedPcm (reallocated as needed). */
        SetProcessedPcm(result, envOutLen, outputStart);

        /* Detector reads the un-delayed envelope. */
        int rawCount = 0;
        if (numSamples > 0)
        {
            int window = (int)_det.MinSilenceSamples;
            if (window < 16) window = 16;
            int worst = (2 * numSamples) / window + 4;
            EnsureRawEvents(worst);
            int got = 0;
            _det.Process(_bufEnv, numSamples, _rawEvents, ref got, _rawEventsCapacity);
            rawCount = got;
        }

        /* V5.6: regime-change reset. */
        if (_det.ConsumeRegimeReset() != 0)
        {
            result.DetectorResetEvent = true;

            /* Capture the triggering peak before flushing the detector. */
            double seedPeak = _det.BurstMax;

            /* Flush detector adaptive state, saving/restoring the sample
             * clock and env_ring abs-index reference around the reset. */
            ulong savedTotal = _det.TotalSamples;
            ulong savedEnvNewest = _det.EnvRingNewestAbs;
            int savedEnvHas = _det.EnvRingHasData;
            _det.Reset();
            _det.TotalSamples = savedTotal;
            _det.EnvRingNewestAbs = savedEnvNewest;
            _det.EnvRingHasData = savedEnvHas;

            /* Re-seed the regime ring so the next beat doesn't re-trip. */
            _det.RegimePeakRing[0] = seedPeak;
            _det.RegimePeakCount = 1;
            _det.RegimePeakHead = 1;

            /* Flush library-level state too. */
            _currentBph = 0;
            _currentBeatPeriod = 0.0;
            _evHistoryCount = 0;
            _evHistoryHead = 0;
            _sync.Reset();

            /* Suppress the raw events from THIS batch. */
            rawCount = 0;
        }

        /* Push A events into BPH history */
        for (int i = 0; i < rawCount; ++i)
        {
            if (_rawEvents[i].IsOnset != 0)
                PushEventHistory(_rawEvents[i].TimeSeconds);
        }

        /* BPH detection: if not already synced, see if we have enough history. */
        int tryDetect = 0;
        if (_sync.Synced == 0 && _evHistoryCount >= 8)
        {
            double[] tmp = new double[TG_EVENT_HISTORY];
            CopyHistoryLinear(tmp);
            double earliest = tmp[0];
            double latest = tmp[_evHistoryCount - 1];
            if (latest - earliest >= _cfg.AutoDetectSeconds) tryDetect = 1;
        }

        if (_sync.Synced == 0 && tryDetect != 0)
        {
            double[] tmp = new double[TG_EVENT_HISTORY];
            CopyHistoryLinear(tmp);
            int matched = 0;
            double phaseScore = 0.0;
            double matchedPeriod = 0.0;
            if (_cfg.BphMode == TgBphMode.Auto)
            {
                matched = Bph.PickByPhase(tmp, _evHistoryCount,
                                          Bph.AutoBphList, Bph.AutoBphList.Length,
                                          0.7, out phaseScore, out matchedPeriod);
            }
            else
            {
                int u = _cfg.ManualBph;
                double T = 3600.0 / (double)u;
                phaseScore = Bph.PhaseScore(tmp, _evHistoryCount, T);
                if (phaseScore >= 0.7)
                {
                    matched = u; matchedPeriod = T;
                }
            }
            if (matched != 0)
            {
                double half = 0.5 * matchedPeriod;
                double sumSmall = 0.0;
                int cntSmall = 0;
                for (int i = 1; i < _evHistoryCount; ++i)
                {
                    double d = tmp[i] - tmp[i - 1];
                    if (d > 0.0 && d < half) { sumSmall += d; cntSmall++; }
                }
                double ac = (cntSmall > 0) ? sumSmall / (double)cntSmall
                                           : matchedPeriod * 0.05;
                double tol = matchedPeriod * _cfg.SyncTolerancePct * 0.01;
                double lastEv = tmp[_evHistoryCount - 1];
                _sync.Lock(matched, matchedPeriod, ac,
                           lastEv, tol, _cfg.SyncLossMisses,
                           _cfg.PllPeriodGain, _cfg.PllAcGain);
                _currentBph = matched;
                _currentBeatPeriod = matchedPeriod;
                result.SyncAcquiredEvent = true;

                /* Tighten the silence and A-to-A gates now that BPH is known. */
                _det.SetMinSilence(0.4 * matchedPeriod);
                _det.SetMinAInterval(0.7 * matchedPeriod);

                /* V5.2: tune C-search skip from beat period (~3%). */
                double skipS = 0.03 * matchedPeriod;
                _det.SetCSearchSkip(skipS);
            }
        }

        /* Run sync tracker and detect loss */
        int prevSynced = _sync.Synced;
        if (_sync.Synced != 0)
        {
            for (int i = 0; i < rawCount; ++i)
            {
                _sync.Update(_rawEvents[i].TimeSeconds);
                if (_sync.Synced == 0) break;
            }
        }
        /* Time-based sync loss watchdog */
        if (_sync.Synced != 0)
        {
            double streamT = (double)(inputEnd) / _cfg.SampleRate;
            double tol = _currentBeatPeriod * _cfg.SyncTolerancePct * 0.01;
            if (streamT > _sync.NextATime
                + _cfg.SyncLossMisses * _currentBeatPeriod + tol)
            {
                _sync.Synced = 0;
            }
        }
        if (prevSynced != 0 && _sync.Synced == 0)
        {
            result.SyncLostEvent = true;
            _currentBph = 0;
            _currentBeatPeriod = 0.0;
            _evHistoryCount = 0;
            _evHistoryHead = 0;
            _det.SetMinSilence(0.020);
            _det.SetMinAInterval(0.0);
            _det.SetCSearchSkip(0.003);  // back to default
        }

        /* Build sync_status */
        if (_cfg.BphMode == TgBphMode.Manual)
        {
            if (_sync.Synced != 0)
            {
                result.SyncStatus = TgSyncStatus.Synced;
                result.DetectedBph = _cfg.ManualBph;
            }
            else if (tryDetect != 0)
            {
                result.SyncStatus = TgSyncStatus.Mismatch;
                result.DetectedBph = _cfg.ManualBph;
            }
            else
            {
                result.SyncStatus = TgSyncStatus.NotSynced;
                result.DetectedBph = _cfg.ManualBph;
            }
        }
        else
        {
            if (_sync.Synced != 0)
            {
                result.SyncStatus = TgSyncStatus.Synced;
                result.DetectedBph = _currentBph;
            }
            else
            {
                result.SyncStatus = TgSyncStatus.NotSynced;
                result.DetectedBph = 0;
            }
        }
        result.MeasuredPeriodS = _currentBeatPeriod;

        /* Emit events: copy timing, set is_pre_sync, optionally drop. */
        if (rawCount > 0)
        {
            bool preSync = (_currentBph <= 0);
            for (int i = 0; i < rawCount; ++i)
            {
                if (preSync && _cfg.SuppressPreSyncEvents) continue;
                ref TgRawEvent r = ref _rawEvents[i];
                bool isA = r.IsOnset != 0;

                TgEvent ev = default;
                ev.Type = isA ? TgEventType.A : TgEventType.C;
                ev.IsPreSync = preSync;
                ev.PeakValue = r.PeakValue;

                /* V5.4: choose primary timing per c_placement (C only). */
                bool useOnsetPrimary =
                    (!isA)
                    && (_cfg.CPlacement == TgCPlacement.Onset)
                    && (r.OnsetValid != 0);

                if (useOnsetPrimary)
                {
                    ev.SampleIndex = r.OnsetSampleIndex;
                    ev.SubSampleOffset = r.OnsetSubSampleOffset;
                    ev.TimeSeconds = r.OnsetTimeSeconds;
                }
                else
                {
                    ev.SampleIndex = r.SampleIndex;
                    ev.SubSampleOffset = r.SubSampleOffset;
                    ev.TimeSeconds = r.TimeSeconds;
                }

                /* V5.4: always populate onset metadata fields. */
                ev.OnsetSampleIndex = r.OnsetSampleIndex;
                ev.OnsetSubSampleOffset = r.OnsetSubSampleOffset;
                ev.OnsetTimeSeconds = r.OnsetTimeSeconds;
                ev.OnsetValid = r.OnsetValid != 0;

                result.Events.Add(ev);
            }
        }

        /* Detector state for diagnostics */
        {
            _det.GetThresholds(out double onsetThr, out double minPeakThr,
                               out double effNoise, out double refPeak);
            result.OnsetThreshold = (float)onsetThr;
            result.MinPeakThreshold = (float)minPeakThr;
            result.NoiseFloor = (float)effNoise;
            result.ReferencePeak = (float)refPeak;
        }
    }

    // tg_flush
    public void Flush(TgResult result)
    {
        if (result == null) throw new ArgumentNullException(nameof(result));
        // n = max(delay_samples, burst_end_samples) + 32
        int n = _delaySamples;
        if ((int)_det.BurstEndSamples > n) n = (int)_det.BurstEndSamples;
        n += 32;
        if (n == 0) { ResetResult(result); return; }
        float[] silent = new float[n]; // calloc -> zeroed
        Process(silent, result);
    }

    /* ----- result helpers ------------------------------------------------ */

    /* Clear the reusable TgResult (memset(result, 0, sizeof) equivalent). */
    private static void ResetResult(TgResult result)
    {
        result.SyncStatus = TgSyncStatus.NotSynced;
        result.DetectedBph = 0;
        result.MeasuredPeriodS = 0.0;
        result.Events.Clear();
        result.ProcessedPcmLen = 0;
        result.ProcessedPcmStartSample = 0;
        result.SyncLostEvent = false;
        result.SyncAcquiredEvent = false;
        result.DetectorResetEvent = false;
        result.OnsetThreshold = 0.0f;
        result.MinPeakThreshold = 0.0f;
        result.NoiseFloor = 0.0f;
        result.ReferencePeak = 0.0f;
    }

    /* Copy the env-out slice into result.ProcessedPcm. In the C++ original
     * processed_pcm aliases ctx->buf_env_out (length env_out_len); since
     * the C# caller reuses TgResult and its array, copy the valid prefix
     * (reallocating only when it would grow). */
    private void SetProcessedPcm(TgResult result, int envOutLen, ulong outputStart)
    {
        if (result.ProcessedPcm.Length < envOutLen)
            result.ProcessedPcm = new float[envOutLen];
        if (envOutLen > 0)
            Array.Copy(_bufEnvOut, 0, result.ProcessedPcm, 0, envOutLen);
        result.ProcessedPcmLen = envOutLen;
        result.ProcessedPcmStartSample = outputStart;
    }
}
