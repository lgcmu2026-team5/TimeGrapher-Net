using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Audio;

internal sealed class LinuxLiveAudioWorker : ILiveAudioWorker
{
    private const int ReplacementStopTimeoutMs = 2000;
    private const int StartupFailureProbeTimeoutMs = 250;
    private const int Channels = MasterAudioBuffer.Channels;
    private const int AlsaDeviceNumberBase = 1_000_000;
    private const int AlsaDeviceNumberStride = 1_000;

    private static readonly Regex SourceLineRegex = new(
        @"(?:^|\s)(?:\*\s*)?(?<id>\d+)\.\s+(?<name>.+?)(?:\s+\[|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AlsaCaptureDeviceRegex = new(
        @"^card\s+(?<card>\d+):\s+(?<cardId>[^\[]+)\[(?<cardName>[^\]]+)\],\s+device\s+(?<device>\d+):\s+(?<deviceName>.+?)(?:\s+\[.*\])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly MasterAudioBuffer _rawAudio;
    private readonly Stopwatch _timer = new();
    private readonly StringBuilder _stderr = new();

    private Process? _process;
    private Thread? _stdoutThread;
    private Thread? _stderrThread;
    private bool _timerStarted;
    private double _lastTime;
    private ulong _frameCount;
    private ulong _sampleCount;
    private float _volume = 1.0f;
    private PcmSampleFormat _sampleFormat = PcmSampleFormat.Float32LittleEndian;
    private string _processErrorPrefix = "pw-record";
    private volatile bool _paused;

    public LinuxLiveAudioWorker(MasterAudioBuffer buffer)
    {
        _rawAudio = buffer;
        _rawAudio.Reset();
    }

    public event Action? DataReady;

    public bool IsPaused => _paused;

    public static IReadOnlyList<LiveAudioDevice> EnumerateInputDevices()
    {
        string status = RunCommand("wpctl", "status");
        IReadOnlyList<LiveAudioDevice> devices = ParseWpctlSources(status);
        if (devices.Count > 0)
        {
            return devices;
        }

        string arecordList = RunCommand("arecord", "-l");
        return ParseAlsaCaptureDevices(arecordList);
    }

    internal static IReadOnlyList<LiveAudioDevice> ParseWpctlSources(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return Array.Empty<LiveAudioDevice>();
        }

        var devices = new List<LiveAudioDevice>();
        bool inSources = false;
        foreach (string rawLine in status.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            string line = rawLine.Trim();
            if (line.Contains("Sources:", StringComparison.Ordinal))
            {
                inSources = true;
                continue;
            }

            if (!inSources)
            {
                continue;
            }

            if (line.Contains("Filters:", StringComparison.Ordinal) ||
                line.Contains("Streams:", StringComparison.Ordinal) ||
                line.Contains("Video", StringComparison.Ordinal) ||
                line.Contains("Settings", StringComparison.Ordinal))
            {
                break;
            }

            Match match = SourceLineRegex.Match(line);
            if (!match.Success ||
                !int.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                continue;
            }

            string name = match.Groups["name"].Value.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            devices.Add(new LiveAudioDevice(id, name));
        }

        return devices;
    }

    internal static IReadOnlyList<LiveAudioDevice> ParseAlsaCaptureDevices(string arecordList)
    {
        if (string.IsNullOrWhiteSpace(arecordList))
        {
            return Array.Empty<LiveAudioDevice>();
        }

        var devices = new List<LiveAudioDevice>();
        foreach (string rawLine in arecordList.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            string line = rawLine.Trim();
            Match match = AlsaCaptureDeviceRegex.Match(line);
            if (!match.Success ||
                !int.TryParse(match.Groups["card"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int card) ||
                !int.TryParse(match.Groups["device"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int device))
            {
                continue;
            }

            string cardName = match.Groups["cardName"].Value.Trim();
            string deviceName = match.Groups["deviceName"].Value.Trim();
            string displayName = "ALSA hw:" + card.ToString(CultureInfo.InvariantCulture) +
                "," + device.ToString(CultureInfo.InvariantCulture) +
                " " + cardName;
            if (deviceName.Length > 0 && !displayName.Contains(deviceName, StringComparison.Ordinal))
            {
                displayName += " - " + deviceName;
            }

            devices.Add(new LiveAudioDevice(EncodeAlsaDeviceNumber(card, device), displayName));
        }

        return devices;
    }

    public void Start(int deviceNumber, int sampleRate, float volume)
    {
        _volume = volume;
        _paused = false;
        if (_process != null)
        {
            if (!TryStop(TimeSpan.FromMilliseconds(ReplacementStopTimeoutMs)))
            {
                throw new InvalidOperationException("Existing audio capture process did not stop.");
            }
        }

        if (TryDecodeAlsaDeviceNumber(deviceNumber, out int card, out int device))
        {
            StartAlsaCapture(card, device, sampleRate);
            return;
        }

        StartPipeWireCapture(deviceNumber, sampleRate);
    }

    private void StartPipeWireCapture(int deviceNumber, int sampleRate)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pw-record",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--media-category");
        startInfo.ArgumentList.Add("Capture");
        startInfo.ArgumentList.Add("--rate");
        startInfo.ArgumentList.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--channels");
        startInfo.ArgumentList.Add(Channels.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("f32");
        startInfo.ArgumentList.Add("--raw");
        if (deviceNumber > 0)
        {
            startInfo.ArgumentList.Add("--target");
            startInfo.ArgumentList.Add(deviceNumber.ToString(CultureInfo.InvariantCulture));
        }
        startInfo.ArgumentList.Add("-");

        StartProcess(startInfo, PcmSampleFormat.Float32LittleEndian, "pw-record");
    }

    private void StartAlsaCapture(int card, int device, int sampleRate)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "arecord",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-q");
        startInfo.ArgumentList.Add("-D");
        startInfo.ArgumentList.Add(
            "hw:" + card.ToString(CultureInfo.InvariantCulture) + "," + device.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("raw");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("S16_LE");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(Channels.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-r");
        startInfo.ArgumentList.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-");

        StartProcess(startInfo, PcmSampleFormat.Int16LittleEndian, "arecord");
    }

    private void StartProcess(ProcessStartInfo startInfo, PcmSampleFormat sampleFormat, string processName)
    {
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start " + processName + ".");

        _process = process;
        _stderr.Clear();
        _sampleFormat = sampleFormat;
        _processErrorPrefix = processName;
        _stdoutThread = new Thread(() => ReadPcm(process))
        {
            Name = processName + "AudioCaptureRead",
            IsBackground = true,
        };
        _stderrThread = new Thread(() => ReadStderr(process))
        {
            Name = processName + "AudioCaptureErr",
            IsBackground = true,
        };
        _stdoutThread.Start();
        _stderrThread.Start();

        if (process.WaitForExit(StartupFailureProbeTimeoutMs))
        {
            string error = _stderr.ToString().Trim();
            _stdoutThread?.Join(TimeSpan.FromMilliseconds(250));
            _stderrThread?.Join(TimeSpan.FromMilliseconds(250));
            _stdoutThread = null;
            _stderrThread = null;
            process.Dispose();
            _process = null;
            throw new InvalidOperationException(
                error.Length == 0 ? processName + " exited before capture started." : processName + " exited: " + error);
        }
    }

    public void SetVolume(float volume)
    {
        _volume = volume;
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
    }

    public bool TryStop(TimeSpan timeout)
    {
        _paused = false;
        Process? process = Interlocked.Exchange(ref _process, null);
        if (process == null)
        {
            return true;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            if (timeout == Timeout.InfiniteTimeSpan)
            {
                process.WaitForExit();
            }
            else if (!process.WaitForExit(timeout))
            {
                return false;
            }

            _stdoutThread?.Join(TimeSpan.FromMilliseconds(250));
            _stderrThread?.Join(TimeSpan.FromMilliseconds(250));
            _stdoutThread = null;
            _stderrThread = null;
            return true;
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        TryStop(Timeout.InfiniteTimeSpan);
    }

    private void ReadPcm(Process process)
    {
        var pending = new byte[sizeof(float)];
        int pendingCount = 0;
        var readBuffer = new byte[8192];

        try
        {
            Stream stream = process.StandardOutput.BaseStream;
            int bytesPerSample = BytesPerSample(_sampleFormat);
            while (true)
            {
                int read = stream.Read(readBuffer, 0, readBuffer.Length);
                if (read <= 0)
                {
                    return;
                }

                int offset = 0;
                if (pendingCount > 0)
                {
                    int bytesNeeded = bytesPerSample - pendingCount;
                    int bytesToCopy = Math.Min(bytesNeeded, read);
                    Array.Copy(readBuffer, 0, pending, pendingCount, bytesToCopy);
                    pendingCount += bytesToCopy;
                    offset += bytesToCopy;

                    if (pendingCount < bytesPerSample)
                    {
                        continue;
                    }

                    WriteSamples(pending.AsSpan(0, bytesPerSample), _sampleFormat);
                    pendingCount = 0;
                }

                int remaining = read - offset;
                int usableBytes = remaining - (remaining % bytesPerSample);
                if (usableBytes > 0)
                {
                    WriteSamples(readBuffer.AsSpan(offset, usableBytes), _sampleFormat);
                    offset += usableBytes;
                }

                int leftoverBytes = read - offset;
                if (leftoverBytes > 0)
                {
                    Array.Copy(readBuffer, offset, pending, 0, leftoverBytes);
                    pendingCount = leftoverBytes;
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void ReadStderr(Process process)
    {
        try
        {
            while (!process.StandardError.EndOfStream)
            {
                string? line = process.StandardError.ReadLine();
                if (line == null)
                {
                    return;
                }

                if (_stderr.Length < 4096)
                {
                    _stderr.AppendLine(line);
                }

                Console.Error.WriteLine(_processErrorPrefix + ": " + line);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void WriteSamples(ReadOnlySpan<byte> bytes, PcmSampleFormat sampleFormat)
    {
        if (_paused)
        {
            return;
        }

        int bytesPerSample = BytesPerSample(sampleFormat);
        int sampleCount = bytes.Length / bytesPerSample;
        if (sampleCount <= 0)
        {
            DataReady?.Invoke();
            return;
        }

        float volume = _volume;
        Span<float> block = sampleCount <= 4096
            ? stackalloc float[sampleCount]
            : new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            int offset = i * bytesPerSample;
            block[i] = sampleFormat == PcmSampleFormat.Float32LittleEndian
                ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, sizeof(float)))) * volume
                : BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(offset, sizeof(short))) / 32768.0f * volume;
        }

        _rawAudio.WriteSamples(block);
        UpdateStats((ulong)sampleCount);
        DataReady?.Invoke();
    }

    private void UpdateStats(ulong sampleCount)
    {
        if (!_timerStarted)
        {
            _timerStarted = true;
            _timer.Start();
        }

        ++_frameCount;
        _sampleCount += sampleCount;
        double currentTime = _timer.ElapsedMilliseconds / 1000.0;
        if (currentTime - _lastTime > 2)
        {
            double delta = currentTime - _lastTime;
            double fps = _frameCount / delta;
            double sps = _sampleCount / delta;
            double spf = _sampleCount / _frameCount;
            _rawAudio.SetStats(fps, spf, sps);
            _lastTime = currentTime;
            _frameCount = 0;
            _sampleCount = 0;
        }
    }

    private static string RunCommand(string fileName, string argument)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start())
            {
                return "";
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            return process.ExitCode == 0 ? output : "";
        }
        catch
        {
            return "";
        }
    }

    private static int EncodeAlsaDeviceNumber(int card, int device)
    {
        return AlsaDeviceNumberBase + (card * AlsaDeviceNumberStride) + device;
    }

    internal static bool TryDecodeAlsaDeviceNumber(int deviceNumber, out int card, out int device)
    {
        if (deviceNumber < AlsaDeviceNumberBase)
        {
            card = 0;
            device = 0;
            return false;
        }

        int encoded = deviceNumber - AlsaDeviceNumberBase;
        card = encoded / AlsaDeviceNumberStride;
        device = encoded % AlsaDeviceNumberStride;
        return true;
    }

    private static int BytesPerSample(PcmSampleFormat sampleFormat)
    {
        return sampleFormat == PcmSampleFormat.Float32LittleEndian ? sizeof(float) : sizeof(short);
    }

    private enum PcmSampleFormat
    {
        Float32LittleEndian,
        Int16LittleEndian,
    }
}
