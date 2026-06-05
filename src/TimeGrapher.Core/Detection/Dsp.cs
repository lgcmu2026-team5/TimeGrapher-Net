/* Dsp.cs -- front-end DSP primitives feeding the burst detector.
 * Port of Dsp.h / Dsp.cpp.
 *
 * Two single-pole IIR filters, both designed for streaming use (tiny
 * per-sample state, no buffering, no look-ahead):
 *
 *   TgHpf : single-pole high-pass / DC blocker. Default cutoff 200 Hz.
 *           y[n] = a * (y[n-1] + x[n] - x[n-1]), a = exp(-2*pi*fc/fs).
 *
 *   TgEnvelope : full-wave rectifier + one-pole LPF. Default smoothing
 *                0.15 ms.
 *                env[n] = env[n-1] + alpha * (|x[n]| - env[n-1]),
 *                alpha = 1 - exp(-1/(tau*fs)).
 *
 * All internal state is double (as in the C++ original); inputs/outputs
 * are float.
 */

namespace TimeGrapher.Core.Detection;

/// <summary>
/// Single-pole highpass / DC blocker (tg_hpf_t).
///   y[n] = x[n] - x[n-1] + a * y[n-1],  a = exp(-2*pi*f_c/fs)
/// </summary>
internal sealed class TgHpf
{
    private double _a;       // pole coefficient
    private double _xPrev;   // last input sample
    private double _yPrev;   // last output sample

    // tg_hpf_init
    public TgHpf(double fs, double fc)
    {
        Init(fs, fc);
    }

    public void Init(double fs, double fc)
    {
        if (fc < 1.0) fc = 1.0;
        if (fc > 0.25 * fs) fc = 0.25 * fs;
        _a = Math.Exp(-2.0 * Math.PI * fc / fs);
        Reset();
    }

    // tg_hpf_reset
    public void Reset()
    {
        _xPrev = 0.0;
        _yPrev = 0.0;
    }

    // tg_hpf_process
    public void Process(ReadOnlySpan<float> input, Span<float> output, int n)
    {
        double a = _a;
        double xp = _xPrev;
        double yp = _yPrev;
        for (int i = 0; i < n; ++i)
        {
            double x = (double)input[i];
            double y = x - xp + a * yp;
            output[i] = (float)y;
            xp = x;
            yp = y;
        }
        _xPrev = xp;
        _yPrev = yp;
    }
}

/// <summary>
/// Envelope detector (tg_envelope_t): full-wave rectify + one-pole LPF.
///   y[n] = y[n-1] + alpha * (|x[n]| - y[n-1])
/// </summary>
internal sealed class TgEnvelope
{
    private double _alpha;   // LPF coefficient
    private double _state;   // LPF state

    // tg_envelope_init
    public TgEnvelope(double fs, double smoothingMs)
    {
        Init(fs, smoothingMs);
    }

    public void Init(double fs, double smoothingMs)
    {
        if (smoothingMs <= 0.0) smoothingMs = 0.15;
        double tauSamples = (smoothingMs * 1e-3) * fs;
        if (tauSamples < 1.0) tauSamples = 1.0;
        _alpha = 1.0 - Math.Exp(-1.0 / tauSamples);
        Reset();
    }

    // tg_envelope_reset
    public void Reset()
    {
        _state = 0.0;
    }

    // tg_envelope_process
    public void Process(ReadOnlySpan<float> input, Span<float> output, int n)
    {
        double s = _state;
        double a = _alpha;
        for (int i = 0; i < n; ++i)
        {
            double x = (double)input[i];
            if (x < 0.0) x = -x;
            s += a * (x - s);
            output[i] = (float)s;
        }
        _state = s;
    }
}
