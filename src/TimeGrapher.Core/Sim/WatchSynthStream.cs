using System.Globalization;

namespace TimeGrapher.Core.Sim;

/*
    WatchSynthStream

    Continuous mono float-PCM synthetic mechanical-watch stream generator.
    Direct port of watch_synth_stream.h / watch_synth_stream.cpp.

    The generator is stateful. The caller repeatedly provides a destination float
    buffer and the module fills that buffer with the next contiguous block of
    samples. There is no internal output file dependency and no assumption that
    the caller's block size is constant.

    See the original header for full unit/naming documentation. Key points:
      - pcm_peak_amplitude: normalized float PCM full-scale fraction (0..1) — digital loudness.
      - watch_amplitude_degrees / lift_angle_degrees: mechanical model used to derive A->C time.
      - beat_error_ms = 0.5 * (interval(Tick->Tock) - interval(Tock->Tick)).

    STREAMING CONTRACT
      - The stream starts at absolute sample index 0 after init/reset.
      - Each fill call continues exactly where the previous fill call stopped.
      - Any caller block size is valid, including odd sizes such as 257 samples.
      - Packets may begin in one block and continue into later blocks.
      - Resonator and noise filter states persist across blocks.
*/

public enum WatchSynthEventKind
{
    Tick = 0, // WATCH_SYNTH_EVENT_TICK
    Tock = 1  // WATCH_SYNTH_EVENT_TOCK
}

/// <summary>
/// Public configuration (port of WatchSynthStreamConfig). All fields carry the same
/// meaning and default values as the original C struct. Use <see cref="Default"/> /
/// <see cref="Clean"/> / <see cref="Realistic"/> to obtain pre-filled configs.
/// </summary>
public sealed class WatchSynthStreamConfig
{
    public uint SampleRateHz;            // Hz. Supported: 44100..384000.
    public double Bph;                   // Beats per hour. Supported: 3600..43200.
    public double RateErrorSPerDay;      // s/day. Positive = fast = shorter intervals.
    public double BeatErrorMs;           // ms. Timegrapher-style displayed beat error. Example: 0.210 ms.
    public double TimingJitterUs;        // us. Uniform random jitter added to intervals, +/- value.
    public double StartTimeS;            // seconds. Time of first generated beat after reset.
    public ulong Seed;                   // deterministic random seed.

    public double PcmPeakAmplitude;      // normalized float PCM target level, 0..1.
    public double NoisePeakAmplitude;    // normalized float PCM noise level, 0..1.

    public double WatchAmplitudeDegrees; // degrees. Mechanical watch amplitude, e.g. 180..320.
    public double LiftAngleDegrees;      // degrees. Watch lift angle, e.g. 44..60.
    public int UseWatchAmplitudeForAToC; // 1 = derive A->C from watch amplitude/lift angle.
    public double ManualAToCTimeS;       // seconds. Used if UseWatchAmplitudeForAToC is 0.
    public double MinAToCTimeS;          // seconds. Safety clamp for A->C.
    public double MaxAToCTimeS;          // seconds. Safety clamp for A->C.

    public int EnableRealisticPacket;       // 1 = multi-impact A/B/C packet; 0 = simple packet.
    public int EnablePacketShapeVariation;  // 1 = vary lobe delay/frequency/decay/level per beat.
    public int EnableAmplitudeDrift;        // 1 = slow PCM packet gain drift.
    public int EnableSensorResonance;       // 1 = contact/case resonator model.
    public int EnableBandlimitedNoise;      // 1 = band-limit synthetic mechanical noise.
    public int EnableBphWander;             // 1 = tiny random-walk interval wander.
    public int EnableTickTockSpectralDiff;  // 1 = Tick and Tock differ spectrally.

    // C-peak control. The exact C anchor is a narrow Gaussian peak placed at the
    // computed A->C time, keeping timegrapher amplitude readings tied to
    // watch_amplitude_degrees even at high sample rates.
    public int EnableCPeakLock;          // 1 = make intended C peak dominate later C ringing.
    public double PostCLobeScale;        // unitless. Scale later C/ring lobes when lock is on.
    public double CPeakAnchorGain;       // unitless. Dominant exact C anchor gain.
    public double CPeakAnchorWidthS;     // seconds. Gaussian width for exact C anchor, e.g. 20 us.

    public double PacketTailAfterCS;     // seconds. Ringing duration after computed C time.
    public double PacketGainVariation;   // fraction. Per-packet random PCM gain variation.
    public double ShapeDelayJitterUs;    // us. Random lobe delay perturbation.
    public double ShapeFrequencyJitter;  // fraction. Random lobe frequency variation.
    public double ShapeDecayJitter;      // fraction. Random lobe decay variation.

    public double AmplitudeDriftDepth;   // fraction. Example 0.08 = +/-8%.
    public double AmplitudeDriftPeriodS; // seconds.
    public double BphWanderDepthUs;      // us. Random-walk clamp.
    public double BphWanderStepUs;       // us. Random-walk step per beat.

    public double SensorResonance1Hz;    // Hz.
    public double SensorResonance1Q;     // unitless Q.
    public double SensorResonance1Gain;  // mix gain.
    public double SensorResonance2Hz;    // Hz.
    public double SensorResonance2Q;     // unitless Q.
    public double SensorResonance2Gain;  // mix gain.

    public double NoiseLowHz;            // Hz. Noise high-pass corner.
    public double NoiseHighHz;           // Hz. Noise low-pass corner.

    /// <summary>
    /// Fill cfg with a low-variation test configuration (watch_synth_stream_clean_config).
    /// Best for verifying timing equations: jitter, wander, drift, resonance, and
    /// spectral Tick/Tock differences are disabled. The A->C time is still derived
    /// from watch amplitude and lift angle by default.
    /// </summary>
    public static WatchSynthStreamConfig Clean()
    {
        // memset(cfg, 0, sizeof(*cfg)) then explicit assignments.
        var cfg = new WatchSynthStreamConfig
        {
            SampleRateHz = 48000u,
            Bph = 19800.0,
            RateErrorSPerDay = 0.0,
            BeatErrorMs = 0.0,
            TimingJitterUs = 0.0,
            StartTimeS = 0.050,
            Seed = 0x123456789abcdefUL,
            PcmPeakAmplitude = 0.70,
            NoisePeakAmplitude = 0.0005,
            WatchAmplitudeDegrees = 270.0,
            LiftAngleDegrees = 52.0,
            UseWatchAmplitudeForAToC = 1,
            ManualAToCTimeS = 0.0084,
            MinAToCTimeS = 0.0010,
            MaxAToCTimeS = 0.2500,
            EnableRealisticPacket = 0,
            EnablePacketShapeVariation = 0,
            EnableAmplitudeDrift = 0,
            EnableSensorResonance = 0,
            EnableBandlimitedNoise = 0,
            EnableBphWander = 0,
            EnableTickTockSpectralDiff = 0,

            // Keep the intended C peak dominant, including at high sample rates.
            EnableCPeakLock = 1,
            PostCLobeScale = 0.22,
            CPeakAnchorGain = 3.00,
            CPeakAnchorWidthS = 0.000020,

            PacketTailAfterCS = 0.0080,
            PacketGainVariation = 0.0,
            ShapeDelayJitterUs = 0.0,
            ShapeFrequencyJitter = 0.0,
            ShapeDecayJitter = 0.0,
            AmplitudeDriftDepth = 0.0,
            AmplitudeDriftPeriodS = 10.0,
            BphWanderDepthUs = 0.0,
            BphWanderStepUs = 0.0,
            SensorResonance1Hz = 3600.0,
            SensorResonance1Q = 2.2,
            SensorResonance1Gain = 0.0,
            SensorResonance2Hz = 9200.0,
            SensorResonance2Q = 4.5,
            SensorResonance2Gain = 0.0,
            NoiseLowHz = 700.0,
            NoiseHighHz = 18000.0
        };
        return cfg;
    }

    /// <summary>
    /// Fill cfg with a more watch-like configuration (watch_synth_stream_realistic_config).
    /// Starts from <see cref="Clean"/> and enables controlled acoustic imperfections.
    /// Realistic defaults are rate-preserving (gentle BPH wander). Deterministic for a
    /// given seed so test runs can be reproduced exactly.
    /// </summary>
    public static WatchSynthStreamConfig Realistic()
    {
        var cfg = Clean();
        cfg.TimingJitterUs = 1.0;
        cfg.NoisePeakAmplitude = 0.0022;
        cfg.EnableRealisticPacket = 1;
        cfg.EnablePacketShapeVariation = 1;
        cfg.EnableAmplitudeDrift = 1;
        cfg.EnableSensorResonance = 1;
        cfg.EnableBandlimitedNoise = 1;
        cfg.EnableBphWander = 1;
        cfg.EnableTickTockSpectralDiff = 1;
        cfg.PacketTailAfterCS = 0.0120;
        cfg.PacketGainVariation = 0.040;
        cfg.ShapeDelayJitterUs = 35.0;
        cfg.ShapeFrequencyJitter = 0.045;
        cfg.ShapeDecayJitter = 0.18;
        cfg.AmplitudeDriftDepth = 0.08;
        cfg.AmplitudeDriftPeriodS = 11.0;
        // Gentle default wander: keeps realistic mode close to configured rate.
        cfg.BphWanderDepthUs = 0.75;
        cfg.BphWanderStepUs = 0.075;
        // Modest normalized resonance: enough color without high-rate clipping.
        cfg.SensorResonance1Gain = 0.12;
        cfg.SensorResonance2Gain = 0.06;
        return cfg;
    }

    /// <summary>Default is currently the realistic configuration (watch_synth_stream_default_config).</summary>
    public static WatchSynthStreamConfig Default() => Realistic();

    /// <summary>Deep copy (the original passes the config struct by value into init).</summary>
    public WatchSynthStreamConfig Clone() => (WatchSynthStreamConfig)MemberwiseClone();
}

/// <summary>Port of WatchSynthStreamEvent — ground-truth beat side channel.</summary>
public struct WatchSynthStreamEvent
{
    public ulong BeatIndex;               // 0-based event number: Tick0, Tock1, Tick2...
    public WatchSynthEventKind Kind;      // Tick for even beat_index, Tock for odd beat_index.
    public double TimeS;                  // seconds. Ground-truth packet onset time.
    public ulong SampleIndex;             // absolute sample index corresponding to time_s.

    public double IntervalFromPreviousUs; // us. 0 for the first event after reset.
    public double AppliedIntervalOffsetUs;// us. +/- beat_error_ms converted to us; raw interval difference is 2x.
    public double TimingJitterUs;         // us. Actual random jitter contribution for this interval.
    public double BphWanderUs;            // us. Smooth random-walk contribution for this interval.

    public double PacketGain;             // normalized PCM gain used for this packet.
    public double AToCTimeS;              // seconds. Ground-truth A/onset to C-like lobe time.
    public double WatchAmplitudeDegrees;  // degrees. Copied from config for traceability.
    public double LiftAngleDegrees;       // degrees. Copied from config for traceability.
}

/// <summary>Port of WatchSynthStreamFillResult.</summary>
public struct WatchSynthStreamFillResult
{
    public ulong FirstSampleIndex; // absolute index of output sample 0 in this block.
    public ulong NextSampleIndex;  // absolute index that will be generated next.
    public int SamplesWritten;     // normally equals requested out_count.
    public int EventsWritten;      // number of events copied to caller's event buffer.
    public int EventsDropped;      // events not returned because event buffer was full.
}

public sealed class WatchSynthStream
{
    private const double MPi = 3.14159265358979323846; // M_PI

    private const double WsMinBph = 3600.0;
    private const double WsMaxBph = 43200.0;
    private const uint WsMinSr = 44100u;
    private const uint WsMaxSr = 384000u;

    private const int WatchSynthMaxActivePackets = 16;
    private const int WatchSynthMaxLobes = 12;

    // ---- damped sinusoidal lobe (WatchSynthLobe) ----
    private struct WatchSynthLobe
    {
        public double DelayS;
        public double RelAmp;
        public double FreqHz;
        public double TauS;
    }

    // ---- active packet (WatchSynthActivePacket) ----
    private sealed class WatchSynthActivePacket
    {
        public int Active;
        public ulong StartSampleIndex;
        public ulong EndSampleIndex;
        public WatchSynthEventKind Kind;
        public double Polarity;
        public double PacketGain;
        public int CAnchorEnabled;
        public double CAnchorDelayS;
        public double CAnchorGain;
        public double CAnchorWidthS;
        public int LobeCount;
        public readonly WatchSynthLobe[] Lobes = new WatchSynthLobe[WatchSynthMaxLobes];

        // memset(p, 0, sizeof(*p)) equivalent: clear all fields and lobes.
        public void Clear()
        {
            Active = 0;
            StartSampleIndex = 0;
            EndSampleIndex = 0;
            Kind = WatchSynthEventKind.Tick;
            Polarity = 0.0;
            PacketGain = 0.0;
            CAnchorEnabled = 0;
            CAnchorDelayS = 0.0;
            CAnchorGain = 0.0;
            CAnchorWidthS = 0.0;
            LobeCount = 0;
            Array.Clear(Lobes, 0, Lobes.Length);
        }
    }

    // ---- stream state (WatchSynthStream struct) ----
    private WatchSynthStreamConfig _cfg;
    private ulong _absoluteSampleIndex;
    private ulong _nextEventSampleIndex;
    private ulong _beatIndex;
    private double _nextEventTimeS;
    private double _lastEventTimeS;
    private double _adjustedIntervalS;
    private double _currentBphWanderUs;
    private double _nextIntervalOffsetUs;
    private double _nextIntervalJitterUs;
    private ulong _rngState;
    private readonly WatchSynthActivePacket[] _activePackets = new WatchSynthActivePacket[WatchSynthMaxActivePackets];
    private double _resonator1S1, _resonator1S2;
    private double _resonator2S1, _resonator2S2;
    private double _noiseLpState, _noiseHpLowState;

    // ---- static helpers ----

    /// <summary>Clamp helper used for all defensive range limits (ws_clamp).</summary>
    private static double WsClamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

    public static bool IsSupportedRate(uint sr) => sr >= WsMinSr && sr <= WsMaxSr;

    /*
        SplitMix64 pseudo-random generator (ws_next_u64).
        Deterministic, tiny, portable, adequate for repeatable synthetic jitter/noise.
        Operates on the stream's _rngState (ref parameter mirrors uint64_t *state).
    */
    private static ulong WsNextU64(ref ulong state)
    {
        ulong z;
        if (state == 0) state = 0x9e3779b97f4a7c15UL;
        z = (state += 0x9e3779b97f4a7c15UL);
        z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
        z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
        return z ^ (z >> 31);
    }

    private static double WsRand01(ref ulong state)
        => (double)(WsNextU64(ref state) >> 11) * (1.0 / 9007199254740992.0);

    private static double WsRandSigned(ref ulong state) => 2.0 * WsRand01(ref state) - 1.0;

    /// <summary>
    /// Compute the A->C time in seconds from a config (watch_synth_stream_compute_a_to_c_time_s).
    /// If UseWatchAmplitudeForAToC is 1, uses the sinusoidal amplitude/lift-angle formula.
    /// Otherwise returns ManualAToCTimeS after clamping.
    /// </summary>
    public static double ComputeAToCTimeS(WatchSynthStreamConfig cfg)
    {
        double t;
        if (cfg == null) return 0.0;
        if (cfg.UseWatchAmplitudeForAToC != 0)
        {
            double beatIntervalS = 3600.0 / cfg.Bph;

            /*
                Sinusoidal balance approximation.
                  A = watch_amplitude_degrees, L = lift_angle_degrees, T = beat_interval_s.
                  theta(t) = A * sin(pi * t / T); lift zone spans roughly -L/2..+L/2.
                  A_to_C = (2T / pi) * asin(L / (2A))
            */
            double ratio = cfg.LiftAngleDegrees / (2.0 * cfg.WatchAmplitudeDegrees);
            ratio = WsClamp(ratio, 0.0, 0.999999);
            t = (2.0 * beatIntervalS / MPi) * Math.Asin(ratio);
        }
        else
        {
            t = cfg.ManualAToCTimeS;
        }
        return WsClamp(t, cfg.MinAToCTimeS, cfg.MaxAToCTimeS);
    }

    /*
        Validate public configuration before a stream starts
        (watch_synth_stream_validate_config). Returns false and sets err on failure.
    */
    public static bool ValidateConfig(WatchSynthStreamConfig cfg, out string err)
    {
        err = "";
        if (cfg == null) { err = "cfg is NULL"; return false; }
        if (!IsSupportedRate(cfg.SampleRateHz))
        {
            err = $"sample_rate_hz must be {WsMinSr}..{WsMaxSr} Hz";
            return false;
        }
        if (cfg.Bph < WsMinBph || cfg.Bph > WsMaxBph)
        {
            err = string.Format(CultureInfo.InvariantCulture, "bph must be {0:F0}..{1:F0}", WsMinBph, WsMaxBph);
            return false;
        }
        if (cfg.PcmPeakAmplitude < 0.0 || cfg.PcmPeakAmplitude > 1.0) { err = "pcm_peak_amplitude must be 0..1 normalized PCM"; return false; }
        if (cfg.NoisePeakAmplitude < 0.0 || cfg.NoisePeakAmplitude > 1.0) { err = "noise_peak_amplitude must be 0..1 normalized PCM"; return false; }
        if (cfg.WatchAmplitudeDegrees < 90.0 || cfg.WatchAmplitudeDegrees > 450.0) { err = "watch_amplitude_degrees must be 90..450 degrees"; return false; }
        if (cfg.LiftAngleDegrees <= 0.0 || cfg.LiftAngleDegrees > 90.0) { err = "lift_angle_degrees must be >0 and <=90 degrees"; return false; }
        if (cfg.PacketTailAfterCS <= 0.0 || cfg.PacketTailAfterCS > 0.100) { err = "packet_tail_after_c_s must be >0 and <=100 ms"; return false; }
        if (cfg.PostCLobeScale < 0.0 || cfg.PostCLobeScale > 2.0) { err = "post_c_lobe_scale must be 0..2"; return false; }
        if (cfg.CPeakAnchorGain < 0.0 || cfg.CPeakAnchorGain > 10.0) { err = "c_peak_anchor_gain must be 0..10"; return false; }
        if (cfg.CPeakAnchorWidthS <= 0.0 || cfg.CPeakAnchorWidthS > 0.0010) { err = "c_peak_anchor_width_s must be >0 and <=1 ms"; return false; }
        if (cfg.MinAToCTimeS <= 0.0 || cfg.MaxAToCTimeS <= cfg.MinAToCTimeS) { err = "A-to-C clamp range is invalid"; return false; }
        if (cfg.StartTimeS < 0.0) { err = "start_time_s must be >=0"; return false; }
        double adjusted = (3600.0 / cfg.Bph) / (1.0 + cfg.RateErrorSPerDay / 86400.0);
        double beatErrorOffsetS = Math.Abs(cfg.BeatErrorMs) * 1.0e-3;
        if (adjusted <= beatErrorOffsetS + cfg.TimingJitterUs * 1.0e-6 + cfg.BphWanderDepthUs * 1.0e-6)
        {
            err = "beat error/jitter/wander too large for BPH";
            return false;
        }
        return true;
    }

    /*
        Initialize a caller-owned stream object (watch_synth_stream_init).
        Validates the config; throws on validation failure (the original returns false
        and the caller logs err — the SimWorker port reports the message).
    */
    public WatchSynthStream(WatchSynthStreamConfig cfg)
    {
        if (!ValidateConfig(cfg, out string err))
            throw new ArgumentException(err, nameof(cfg));

        for (int i = 0; i < _activePackets.Length; ++i)
            _activePackets[i] = new WatchSynthActivePacket();

        // memset(s, 0, sizeof(*s)) then s->cfg = *cfg (by-value copy).
        _cfg = cfg.Clone();
        _rngState = _cfg.Seed != 0 ? _cfg.Seed : 0x123456789abcdefUL;
        _adjustedIntervalS = (3600.0 / _cfg.Bph) / (1.0 + _cfg.RateErrorSPerDay / 86400.0);
        Reset();
    }

    /*
        Reset to the beginning of the synthetic stream (watch_synth_stream_reset).
        Absolute sample index returns to 0; first Tick re-scheduled at cfg.start_time_s.
        The RNG is reset to cfg.seed, making the stream exactly repeatable after reset.
    */
    public void Reset()
    {
        ulong seed = _cfg.Seed != 0 ? _cfg.Seed : 0x123456789abcdefUL;
        for (int i = 0; i < _activePackets.Length; ++i)
            _activePackets[i].Clear();
        _absoluteSampleIndex = 0;
        _beatIndex = 0;
        _nextEventTimeS = _cfg.StartTimeS;
        _nextEventSampleIndex = (ulong)Llround(_nextEventTimeS * (double)_cfg.SampleRateHz);
        _lastEventTimeS = 0.0;
        _currentBphWanderUs = 0.0;
        _nextIntervalOffsetUs = 0.0;
        _nextIntervalJitterUs = 0.0;
        _rngState = seed;
        _resonator1S1 = _resonator1S2 = 0.0;
        _resonator2S1 = _resonator2S2 = 0.0;
        _noiseLpState = _noiseHpLowState = 0.0;
    }

    // llround(): round half away from zero, then truncate to integer (C long long).
    private static long Llround(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    /*
        Allocate an active packet slot (ws_alloc_packet). Returns the first inactive
        slot, or slot 0 if all are active.
    */
    private WatchSynthActivePacket WsAllocPacket()
    {
        for (int i = 0; i < WatchSynthMaxActivePackets; ++i)
            if (_activePackets[i].Active == 0) return _activePackets[i];
        return _activePackets[0];
    }

    private static void WsAddLobe(WatchSynthActivePacket p, double delayS, double amp, double freq, double tau)
    {
        if (p.LobeCount >= WatchSynthMaxLobes) return;
        ref WatchSynthLobe l = ref p.Lobes[p.LobeCount++];
        l.DelayS = delayS; l.RelAmp = amp; l.FreqHz = freq; l.TauS = tau;
    }

    private static void WsSetCAnchor(WatchSynthActivePacket p, double delayS, double gain, double widthS)
    {
        p.CAnchorEnabled = 1;
        p.CAnchorDelayS = delayS;
        p.CAnchorGain = gain;
        p.CAnchorWidthS = widthS;
    }

    /*
        Add one damped sinusoidal lobe to a packet, with optional variation (ws_add_varied_lobe).
          delayS : time after A/onset when the lobe starts
          amp    : relative contribution before packet_gain scaling
          freq   : ringing frequency in Hz
          tau    : exponential decay time constant in seconds
        Realistic mode perturbs delay/frequency/decay/level per packet.
    */
    private void WsAddVariedLobe(WatchSynthActivePacket p, double packetDurationS, double delayS, double amp, double freq, double tau, double freqScale)
    {
        WatchSynthStreamConfig cfg = _cfg;
        freq *= freqScale;
        if (cfg.EnablePacketShapeVariation != 0)
        {
            delayS += cfg.ShapeDelayJitterUs * 1.0e-6 * WsRandSigned(ref _rngState);
            amp *= 1.0 + 0.16 * WsRandSigned(ref _rngState);
            freq *= 1.0 + cfg.ShapeFrequencyJitter * WsRandSigned(ref _rngState);
            tau *= 1.0 + cfg.ShapeDecayJitter * WsRandSigned(ref _rngState);
        }
        delayS = WsClamp(delayS, 0.0, packetDurationS - 0.0001);
        amp = WsClamp(amp, 0.0, 2.0);
        freq = WsClamp(freq, 500.0, 0.45 * (double)cfg.SampleRateHz);
        tau = WsClamp(tau, 0.00015, 0.020);
        WsAddLobe(p, delayS, amp, freq, tau);
    }

    /*
        Schedule the next Tick/Tock event (ws_schedule_next_event).
          Tick -> Tock uses +beat_error_ms
          Tock -> Tick uses -beat_error_ms
        Adds optional independent timing jitter and optional slow BPH wander.
    */
    private void WsScheduleNextEvent(WatchSynthEventKind kind)
    {
        WatchSynthStreamConfig cfg = _cfg;
        double beatErrorOffsetS = cfg.BeatErrorMs * 1.0e-3;
        double offsetS = (kind == WatchSynthEventKind.Tick) ? +beatErrorOffsetS : -beatErrorOffsetS;
        double jitterS = cfg.TimingJitterUs * 1.0e-6 * WsRandSigned(ref _rngState);
        if (cfg.EnableBphWander != 0)
        {
            _currentBphWanderUs += cfg.BphWanderStepUs * WsRandSigned(ref _rngState);
            _currentBphWanderUs = WsClamp(_currentBphWanderUs, -cfg.BphWanderDepthUs, cfg.BphWanderDepthUs);
        }
        else
        {
            _currentBphWanderUs = 0.0;
        }
        _nextIntervalOffsetUs = offsetS * 1.0e6;
        _nextIntervalJitterUs = jitterS * 1.0e6;
        _nextEventTimeS += _adjustedIntervalS + offsetS + jitterS + _currentBphWanderUs * 1.0e-6;
        _nextEventSampleIndex = (ulong)Llround(_nextEventTimeS * (double)cfg.SampleRateHz);
    }

    /*
        Start a new acoustic packet and return its ground-truth event record (ws_start_packet).
        The generated event time is the A/onset time. The packet includes early A-like
        lobes, optional middle impacts, and one or more C-like lobes near the computed A->C time.
    */
    private WatchSynthStreamEvent WsStartPacket()
    {
        WatchSynthStreamConfig cfg = _cfg;
        WatchSynthActivePacket p = WsAllocPacket();
        WatchSynthStreamEvent e;
        WatchSynthEventKind kind = (_beatIndex & 1u) != 0 ? WatchSynthEventKind.Tock : WatchSynthEventKind.Tick;
        double aToCS = ComputeAToCTimeS(cfg);
        double packetDurationS = aToCS + cfg.PacketTailAfterCS;
        ulong packetSamples = (ulong)Math.Ceiling(packetDurationS * (double)cfg.SampleRateHz) + 2u;
        double gain = cfg.PcmPeakAmplitude;
        double freqScale = 1.0, cGain = 1.0;

        if (cfg.PacketGainVariation > 0.0) gain *= 1.0 + cfg.PacketGainVariation * WsRandSigned(ref _rngState);
        if (cfg.EnableAmplitudeDrift != 0)
        {
            double phase = 2.0 * MPi * _nextEventTimeS / cfg.AmplitudeDriftPeriodS;
            gain *= 1.0 + cfg.AmplitudeDriftDepth * Math.Sin(phase);
        }
        gain = WsClamp(gain, 0.0, 1.5);

        p.Clear(); // memset(p, 0, sizeof(*p))
        p.Active = 1;
        p.StartSampleIndex = _nextEventSampleIndex;
        p.EndSampleIndex = p.StartSampleIndex + packetSamples;
        p.Kind = kind;
        p.Polarity = kind == WatchSynthEventKind.Tick ? 1.0 : -0.94;
        p.PacketGain = gain;

        if (cfg.EnableTickTockSpectralDiff != 0)
        {
            if (kind == WatchSynthEventKind.Tick) { freqScale = 1.04; cGain = 1.00; }
            else { freqScale = 0.93; cGain = 0.91; }
        }
        if (cfg.EnableRealisticPacket != 0)
        {
            // A-like onset cluster: early impulse/fork activity near event time.
            WsAddVariedLobe(p, packetDurationS, 0.00000, 0.22, 2300.0, 0.00085, freqScale);
            WsAddVariedLobe(p, packetDurationS, 0.00028, 0.18, 4100.0, 0.00070, freqScale);
            WsAddVariedLobe(p, packetDurationS, 0.00072, 0.13, 6800.0, 0.00055, freqScale);

            // Middle low-energy impacts/rattle before the main C zone.
            WsAddVariedLobe(p, packetDurationS, 0.00215, 0.16, 5200.0, 0.00085, freqScale);
            WsAddVariedLobe(p, packetDurationS, 0.00305, 0.10, 8800.0, 0.00060, freqScale);

            /*
                C-like locking/banking cluster.
                With C-peak lock enabled, a narrow Gaussian C anchor is placed exactly
                at the computed A->C time. The surrounding ringing lobes are deliberately
                lower so a later C-ring peak doesn't steal the amplitude measurement.
            */
            if (cfg.EnableCPeakLock != 0)
            {
                double post = cfg.PostCLobeScale;
                WsAddVariedLobe(p, packetDurationS, aToCS - 0.00105, 0.12 * cGain, 4700.0, 0.00065, freqScale);
                WsSetCAnchor(p, aToCS, cfg.CPeakAnchorGain * cGain, cfg.CPeakAnchorWidthS);
                WsAddVariedLobe(p, packetDurationS, aToCS + 0.00072, 0.18 * post * cGain, 10300.0, 0.00065, freqScale);
                WsAddVariedLobe(p, packetDurationS, aToCS + 0.00175, 0.10 * post * cGain, 6100.0, 0.00090, freqScale);
                WsAddVariedLobe(p, packetDurationS, aToCS + 0.00320, 0.05 * post * cGain, 2900.0, 0.00150, freqScale);
            }
            else
            {
                WsAddVariedLobe(p, packetDurationS, aToCS - 0.00105, 0.34 * cGain, 4700.0, 0.00110, freqScale);
                WsAddVariedLobe(p, packetDurationS, aToCS, 1.00 * cGain, 7600.0, 0.00185, freqScale);
                WsAddVariedLobe(p, packetDurationS, aToCS + 0.00062, 0.53 * cGain, 10300.0, 0.00120, freqScale);
                WsAddVariedLobe(p, packetDurationS, aToCS + 0.00170, 0.24 * cGain, 6100.0, 0.00180, freqScale);
                WsAddVariedLobe(p, packetDurationS, aToCS + 0.00320, 0.12 * cGain, 2900.0, 0.00300, freqScale);
            }
        }
        else
        {
            WsAddLobe(p, 0.0000, 0.38, 2800.0, 0.0013);
            if (cfg.EnableCPeakLock != 0)
            {
                WsSetCAnchor(p, aToCS, cfg.CPeakAnchorGain, cfg.CPeakAnchorWidthS);
                WsAddLobe(p, aToCS + 0.0008, 0.10 * cfg.PostCLobeScale, 9800.0, 0.0008);
            }
            else
            {
                WsAddLobe(p, aToCS, 1.00, 7600.0, 0.0018);
                WsAddLobe(p, aToCS + 0.0007, 0.36, 9800.0, 0.0010);
            }
        }

        e = default; // memset(&e, 0, sizeof(e))
        e.BeatIndex = _beatIndex;
        e.Kind = kind;
        e.TimeS = _nextEventTimeS;
        e.SampleIndex = _nextEventSampleIndex;
        e.PacketGain = gain;
        e.AToCTimeS = aToCS;
        e.WatchAmplitudeDegrees = cfg.WatchAmplitudeDegrees;
        e.LiftAngleDegrees = cfg.LiftAngleDegrees;
        e.BphWanderUs = _currentBphWanderUs;
        if (_beatIndex > 0)
        {
            e.IntervalFromPreviousUs = (_nextEventTimeS - _lastEventTimeS) * 1.0e6;
            e.AppliedIntervalOffsetUs = _nextIntervalOffsetUs;
            e.TimingJitterUs = _nextIntervalJitterUs;
        }
        _lastEventTimeS = _nextEventTimeS;
        ++_beatIndex;
        WsScheduleNextEvent(kind);
        return e;
    }

    private static double WsPacketSample(WatchSynthActivePacket p, ulong absSample, double sr)
    {
        double y = 0.0;
        if (p.Active == 0) return 0.0;
        if (absSample < p.StartSampleIndex) return 0.0;
        if (absSample >= p.EndSampleIndex) { p.Active = 0; return 0.0; }
        for (int i = 0; i < p.LobeCount; ++i)
        {
            ref readonly WatchSynthLobe l = ref p.Lobes[i];
            double relS = (double)(absSample - p.StartSampleIndex) / sr - l.DelayS;
            if (relS >= 0.0)
            {
                double attack = relS < 0.00012 ? relS / 0.00012 : 1.0;
                double env = Math.Exp(-relS / l.TauS);
                y += p.Polarity * p.PacketGain * l.RelAmp * attack * env * Math.Sin(2.0 * MPi * l.FreqHz * relS);
            }
        }
        if (p.CAnchorEnabled != 0)
        {
            double relS = (double)(absSample - p.StartSampleIndex) / sr - p.CAnchorDelayS;
            double w = p.CAnchorWidthS > 0.0 ? p.CAnchorWidthS : 0.000020;
            double g = Math.Exp(-0.5 * (relS / w) * (relS / w));
            y += p.Polarity * p.PacketGain * p.CAnchorGain * g;
        }
        return y;
    }

    private static double WsResonator(double x, ref double s1, ref double s2, double sr, double f, double q, double gain)
    {
        if (gain == 0.0 || f <= 0.0 || q <= 0.0) return x;
        f = WsClamp(f, 20.0, 0.45 * sr);
        double bw = f / q;
        double r = Math.Exp(-MPi * bw / sr);
        double w = 2.0 * MPi * f / sr;
        double a1 = 2.0 * r * Math.Cos(w);
        double a2 = -(r * r);
        double st = x + a1 * s1 + a2 * s2;
        s2 = s1; s1 = st;

        /*
            Normalize resonator contribution by (1-r). Without this, high sample rates
            make r very close to 1 and the recursive state can grow enough to clip,
            creating artificial late peaks.
        */
        return x + gain * (1.0 - r) * st;
    }

    private static double WsLp(double x, ref double state, double cutoff, double sr)
    {
        double a = Math.Exp(-2.0 * MPi * WsClamp(cutoff, 1.0, 0.45 * sr) / sr);
        state = (1.0 - a) * x + a * state;
        return state;
    }

    private static double WsHp(double x, ref double lpState, double cutoff, double sr)
        => x - WsLp(x, ref lpState, cutoff, sr);

    private double WsNoise()
    {
        WatchSynthStreamConfig cfg = _cfg;
        if (cfg.NoisePeakAmplitude <= 0.0) return 0.0;
        double n = cfg.NoisePeakAmplitude * WsRandSigned(ref _rngState);
        if (cfg.EnableBandlimitedNoise != 0)
        {
            n = WsHp(n, ref _noiseHpLowState, cfg.NoiseLowHz, (double)cfg.SampleRateHz);
            n = WsLp(n, ref _noiseLpState, cfg.NoiseHighHz, (double)cfg.SampleRateHz);
        }
        return n;
    }

    /*
        Fill the caller-provided mono float PCM buffer (watch_synth_stream_fill_f32).

        outPcm:
            Output span. Receives outPcm.Length normalized float PCM samples in [-1.0, +1.0].
        events / eventCapacity:
            Optional event side-channel. Pass an empty span to disable. If too many events
            occur inside the block, EventsDropped is incremented and audio continues normally.
    */
    public WatchSynthStreamFillResult FillF32(Span<float> outPcm, Span<WatchSynthStreamEvent> events)
    {
        WatchSynthStreamFillResult r = default;
        int outCount = outPcm.Length;
        if (outCount == 0) return r;
        int eventCapacity = events.Length;
        bool haveEvents = eventCapacity > 0;
        r.FirstSampleIndex = _absoluteSampleIndex;
        double sr = (double)_cfg.SampleRateHz;
        for (int i = 0; i < outCount; ++i)
        {
            ulong absSample = _absoluteSampleIndex;
            double y = 0.0;
            while (_nextEventSampleIndex <= absSample)
            {
                WatchSynthStreamEvent e = WsStartPacket();
                if (haveEvents)
                {
                    if (r.EventsWritten < eventCapacity) events[r.EventsWritten++] = e;
                    else r.EventsDropped++;
                }
            }
            for (int pidx = 0; pidx < WatchSynthMaxActivePackets; ++pidx)
                y += WsPacketSample(_activePackets[pidx], absSample, sr);
            if (_cfg.EnableSensorResonance != 0)
            {
                y = WsResonator(y, ref _resonator1S1, ref _resonator1S2, sr, _cfg.SensorResonance1Hz, _cfg.SensorResonance1Q, _cfg.SensorResonance1Gain);
                y = WsResonator(y, ref _resonator2S1, ref _resonator2S2, sr, _cfg.SensorResonance2Hz, _cfg.SensorResonance2Q, _cfg.SensorResonance2Gain);
            }
            y += WsNoise();
            outPcm[i] = (float)WsClamp(y, -1.0, 1.0);
            ++_absoluteSampleIndex;
        }
        r.SamplesWritten = outCount;
        r.NextSampleIndex = _absoluteSampleIndex;
        return r;
    }

    /// <summary>
    /// Fill the next contiguous block of samples (PORTING.md contract; audio-only).
    /// Equivalent to FillF32 with the event side-channel disabled.
    /// </summary>
    public void Generate(Span<float> block) => FillF32(block, Span<WatchSynthStreamEvent>.Empty);
}
