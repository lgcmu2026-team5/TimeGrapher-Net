using System;
using System.IO;

namespace TimeGrapher.Core.AudioIo;

/// <summary>
/// Decoded mono float WAV data (samples in [-1, 1]; channel 0 if the source was multi-channel).
/// </summary>
public sealed class WavData
{
    public int SampleRate;
    public float[] Samples = Array.Empty<float>();
}

/// <summary>
/// Minimal RIFF/WAVE reader. Supports PCM 16/24/32-bit integer and IEEE float.
/// Format probing is delegated to <see cref="WavProbe"/> so playback and verification
/// share the same chunk-walking and acceptance logic.
/// Throws on failure.
/// </summary>
public static class WavFileReader
{
    private const ushort WaveFormatPcm = 1;        // PCM
    private const ushort WaveFormatIeeeFloat = 3;  // IEEE float

    public static WavData ReadMonoFloat(string filePath, WavAcceptanceProfile? acceptanceProfile = null)
    {
        if (!WavProbe.TryReadFormat(filePath, out WavFormatInfo formatInfo, out string error))
        {
            throw new InvalidDataException("WavFileReader: " + error);
        }

        if (acceptanceProfile != null && !WavProbe.IsAccepted(formatInfo, acceptanceProfile))
        {
            throw new InvalidDataException("WavFileReader: WAV format rejected by acceptance profile");
        }

        if (formatInfo.NumChannels == 0)
            throw new InvalidDataException("WavFileReader: zero channels");

        int channels = formatInfo.NumChannels;
        int bytesPerSample = formatInfo.BytesPerSample;
        if (bytesPerSample <= 0)
            throw new InvalidDataException("WavFileReader: invalid bitsPerSample");

        int frameStride = formatInfo.BlockAlign > 0 ? formatInfo.BlockAlign : bytesPerSample * channels;
        if (frameStride <= 0)
            throw new InvalidDataException("WavFileReader: invalid frame stride");

        byte[] bytes = File.ReadAllBytes(filePath);
        long dataOffsetLong = formatInfo.DataOffset;
        long dataEnd = formatInfo.DataOffset + formatInfo.DataSize;
        int dataOffset = checked((int)dataOffsetLong);
        uint dataSize = (uint)Math.Max(0, dataEnd - dataOffsetLong);
        int frameCount = (int)(dataSize / (uint)frameStride);
        var samples = new float[frameCount];

        // Decode channel 0 of each frame to float [-1, 1].
        for (int f = 0; f < frameCount; f++)
        {
            int s = dataOffset + f * frameStride; // start of channel 0 in this frame
            samples[f] = formatInfo.AudioFormat switch
            {
                WaveFormatIeeeFloat when formatInfo.BitsPerSample == 32 => BitConverter.Int32BitsToSingle(
                    (int)ReadU32Le(bytes, s)),
                WaveFormatIeeeFloat when formatInfo.BitsPerSample == 64 => (float)BitConverter.Int64BitsToDouble(
                    (long)ReadU64Le(bytes, s)),
                WaveFormatPcm when formatInfo.BitsPerSample == 16 => DecodePcm16(bytes, s),
                WaveFormatPcm when formatInfo.BitsPerSample == 24 => DecodePcm24(bytes, s),
                WaveFormatPcm when formatInfo.BitsPerSample == 32 => DecodePcm32(bytes, s),
                _ => throw new InvalidDataException(
                    $"WavFileReader: unsupported format (audioFormat={formatInfo.AudioFormat}, bits={formatInfo.BitsPerSample})")
            };
        }

        return new WavData { SampleRate = formatInfo.SampleRate, Samples = samples };
    }

    private static float DecodePcm16(byte[] b, int o)
    {
        short v = (short)ReadU16Le(b, o);
        return v / 32768.0f;
    }

    private static float DecodePcm24(byte[] b, int o)
    {
        // 24-bit little-endian signed -> sign-extend to int32.
        int v = b[o] | (b[o + 1] << 8) | (b[o + 2] << 16);
        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
        return v / 8388608.0f;
    }

    private static float DecodePcm32(byte[] b, int o)
    {
        int v = (int)ReadU32Le(b, o);
        return v / 2147483648.0f;
    }

    private static ushort ReadU16Le(byte[] b, int o)
        => (ushort)(b[o] | (b[o + 1] << 8));

    private static uint ReadU32Le(byte[] b, int o)
        => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    private static ulong ReadU64Le(byte[] b, int o)
        => ReadU32Le(b, o) | ((ulong)ReadU32Le(b, o + 4) << 32);
}
