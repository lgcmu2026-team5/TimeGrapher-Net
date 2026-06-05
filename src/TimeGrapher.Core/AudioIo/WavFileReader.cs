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
/// Minimal RIFF/WAVE reader. Supports PCM 16/24/32-bit integer and 32-bit IEEE float.
/// Chunk-walks the file looking for "fmt " and "data" (mirrors PlaybackWorker's chunk scan).
/// Throws on failure.
/// </summary>
public static class WavFileReader
{
    private const ushort WaveFormatPcm = 1;        // PCM
    private const ushort WaveFormatIeeeFloat = 3;  // IEEE float
    private const ushort WaveFormatExtensible = 0xFFFE;

    public static WavData ReadMonoFloat(string filePath)
    {
        byte[] bytes = File.ReadAllBytes(filePath);

        if (bytes.Length < 12)
            throw new InvalidDataException("WavFileReader: file too small to be a WAV");

        // RIFF header.
        if (bytes[0] != (byte)'R' || bytes[1] != (byte)'I' || bytes[2] != (byte)'F' || bytes[3] != (byte)'F')
            throw new InvalidDataException("WavFileReader: missing RIFF id");
        if (bytes[8] != (byte)'W' || bytes[9] != (byte)'A' || bytes[10] != (byte)'V' || bytes[11] != (byte)'E')
            throw new InvalidDataException("WavFileReader: missing WAVE id");

        ushort audioFormat = 0;
        ushort numChannels = 0;
        uint sampleRate = 0;
        ushort bitsPerSample = 0;
        bool haveFmt = false;

        int dataOffset = -1;
        uint dataSize = 0;

        // Walk chunks starting right after "WAVE" (offset 12).
        int pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            uint chunkId = ReadU32(bytes, pos);
            uint chunkSize = ReadU32Le(bytes, pos + 4);
            int chunkDataStart = pos + 8;

            // "fmt " = 0x666d7420, "data" = 0x64617461 (big-endian id read for comparison).
            if (chunkId == 0x666d7420u) // "fmt "
            {
                if (chunkDataStart + 16 > bytes.Length)
                    throw new InvalidDataException("WavFileReader: truncated fmt chunk");

                audioFormat = ReadU16Le(bytes, chunkDataStart + 0);
                numChannels = ReadU16Le(bytes, chunkDataStart + 2);
                sampleRate = ReadU32Le(bytes, chunkDataStart + 4);
                // byteRate @ +8, blockAlign @ +12
                bitsPerSample = ReadU16Le(bytes, chunkDataStart + 14);

                // WAVE_FORMAT_EXTENSIBLE: real format is in the SubFormat GUID's first 2 bytes.
                if (audioFormat == WaveFormatExtensible && chunkSize >= 40 &&
                    chunkDataStart + 26 <= bytes.Length)
                {
                    // cbSize @ +16, then 22-byte extension; SubFormat GUID starts @ +24.
                    audioFormat = ReadU16Le(bytes, chunkDataStart + 24);
                }
                haveFmt = true;
            }
            else if (chunkId == 0x64617461u) // "data"
            {
                dataOffset = chunkDataStart;
                dataSize = chunkSize;
                if (dataOffset + (long)dataSize > bytes.Length)
                    dataSize = (uint)(bytes.Length - dataOffset);
                break;
            }

            // Chunks are word-aligned: advance by chunkSize (+1 pad if odd).
            long advance = 8L + chunkSize + (chunkSize & 1);
            if (advance <= 0) break;
            pos += (int)advance;
        }

        if (!haveFmt)
            throw new InvalidDataException("WavFileReader: no fmt chunk found");
        if (dataOffset < 0)
            throw new InvalidDataException("WavFileReader: no data chunk found");
        if (numChannels == 0)
            throw new InvalidDataException("WavFileReader: zero channels");

        int channels = numChannels;
        int bytesPerSample = bitsPerSample / 8;
        if (bytesPerSample <= 0)
            throw new InvalidDataException("WavFileReader: invalid bitsPerSample");

        int frameStride = bytesPerSample * channels;
        if (frameStride <= 0)
            throw new InvalidDataException("WavFileReader: invalid frame stride");

        int frameCount = (int)(dataSize / (uint)frameStride);
        var samples = new float[frameCount];

        // Decode channel 0 of each frame to float [-1, 1].
        for (int f = 0; f < frameCount; f++)
        {
            int s = dataOffset + f * frameStride; // start of channel 0 in this frame
            samples[f] = audioFormat switch
            {
                WaveFormatIeeeFloat when bitsPerSample == 32 => BitConverter.Int32BitsToSingle(
                    (int)ReadU32Le(bytes, s)),
                WaveFormatIeeeFloat when bitsPerSample == 64 => (float)BitConverter.Int64BitsToDouble(
                    (long)ReadU64Le(bytes, s)),
                WaveFormatPcm when bitsPerSample == 16 => DecodePcm16(bytes, s),
                WaveFormatPcm when bitsPerSample == 24 => DecodePcm24(bytes, s),
                WaveFormatPcm when bitsPerSample == 32 => DecodePcm32(bytes, s),
                _ => throw new InvalidDataException(
                    $"WavFileReader: unsupported format (audioFormat={audioFormat}, bits={bitsPerSample})")
            };
        }

        return new WavData { SampleRate = (int)sampleRate, Samples = samples };
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

    // Big-endian 4-byte read used only for FourCC comparison.
    private static uint ReadU32(byte[] b, int o)
        => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

    private static ushort ReadU16Le(byte[] b, int o)
        => (ushort)(b[o] | (b[o + 1] << 8));

    private static uint ReadU32Le(byte[] b, int o)
        => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    private static ulong ReadU64Le(byte[] b, int o)
        => ReadU32Le(b, o) | ((ulong)ReadU32Le(b, o + 4) << 32);
}
