using System;
using System.IO;
using TimeGrapher.Core.Shared;

namespace TimeGrapher.Core.AudioIo;

public sealed record WavFormatInfo(
    ushort AudioFormat,
    ushort NumChannels,
    int SampleRate,
    uint ByteRate,
    ushort BlockAlign,
    ushort BitsPerSample,
    long DataOffset,
    uint DataSize)
{
    public bool IsIeeeFloat32 => AudioFormat == WavProbe.WaveFormatIeeeFloat &&
                                 BitsPerSample == 32;

    public bool IsIeeeFloat32Mono => IsIeeeFloat32 && NumChannels == 1;

    public int BytesPerSample => BitsPerSample / 8;

    public ushort ExpectedBlockAlign =>
        NumChannels == 0 || BytesPerSample <= 0 ? (ushort)0 : (ushort)(NumChannels * BytesPerSample);

    public uint ExpectedByteRate =>
        SampleRate <= 0 ? 0 : (uint)(SampleRate * ExpectedBlockAlign);

    public bool HasConsistentBlockAlign => ExpectedBlockAlign != 0 && BlockAlign == ExpectedBlockAlign;

    public bool HasConsistentByteRate => ExpectedByteRate != 0 && ByteRate == ExpectedByteRate;

    public bool HasAlignedData => BlockAlign != 0 && DataSize % BlockAlign == 0;
}

public sealed record WavAcceptanceProfile(
    bool RequireIeeeFloat32,
    bool RequireMono,
    IReadOnlySet<int>? AcceptedSampleRates)
{
    public static WavAcceptanceProfile PlaybackFloatMonoStandardRates { get; } =
        new(
            RequireIeeeFloat32: true,
            RequireMono: true,
            AcceptedSampleRates: AudioSampleRates.StandardSet);
}

public static class WavProbe
{
    public const ushort WaveFormatPcm = 1;
    public const ushort WaveFormatIeeeFloat = 3;
    public const ushort WaveFormatExtensible = 0xFFFE;

    private static readonly byte[] WaveSubFormatGuidTail =
    {
        0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00,
        0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71,
    };

    public static bool TryReadFormat(string filePath, out WavFormatInfo info, out string error)
    {
        info = new WavFormatInfo(0, 0, 0, 0, 0, 0, 0, 0);
        error = "";

        try
        {
            using FileStream file = File.OpenRead(filePath);
            using var reader = new BinaryReader(file);

            if (file.Length < 12)
            {
                error = "File is too small to be a WAV file.";
                return false;
            }

            if (ReadFourCc(reader) != "RIFF")
            {
                error = "Missing RIFF header.";
                return false;
            }

            _ = reader.ReadUInt32();
            if (ReadFourCc(reader) != "WAVE")
            {
                error = "Missing WAVE header.";
                return false;
            }

            ushort audioFormat = 0;
            ushort numChannels = 0;
            int sampleRate = 0;
            uint byteRate = 0;
            ushort blockAlign = 0;
            ushort bitsPerSample = 0;
            long dataOffset = 0;
            uint dataSize = 0;
            bool haveFmt = false;
            bool haveData = false;

            while (file.Position + 8 <= file.Length)
            {
                string chunkId = ReadFourCc(reader);
                uint chunkSize = reader.ReadUInt32();
                long chunkStart = file.Position;

                if (chunkId == "fmt ")
                {
                    if (chunkSize < 16 || chunkStart + chunkSize > file.Length)
                    {
                        error = "Invalid fmt chunk.";
                        return false;
                    }

                    audioFormat = reader.ReadUInt16();
                    numChannels = reader.ReadUInt16();
                    sampleRate = (int)reader.ReadUInt32();
                    byteRate = reader.ReadUInt32();
                    blockAlign = reader.ReadUInt16();
                    bitsPerSample = reader.ReadUInt16();

                    if (audioFormat == WaveFormatExtensible && chunkSize >= 40)
                    {
                        file.Position = chunkStart + 24;
                        if (!TryReadExtensibleSubFormat(reader, out audioFormat))
                        {
                            error = "Invalid WAVE_FORMAT_EXTENSIBLE SubFormat GUID.";
                            return false;
                        }
                    }

                    haveFmt = true;
                }
                else if (chunkId == "data")
                {
                    if (chunkStart + chunkSize > file.Length)
                    {
                        error = "Invalid data chunk.";
                        return false;
                    }

                    dataOffset = chunkStart;
                    dataSize = chunkSize;
                    haveData = true;
                }

                long next = chunkStart + chunkSize + (chunkSize & 1);
                if (next < file.Position)
                {
                    error = "Invalid WAV chunk size.";
                    return false;
                }
                file.Position = Math.Min(next, file.Length);

                if (haveFmt && haveData)
                {
                    break;
                }
            }

            if (!haveFmt)
            {
                error = "No fmt chunk found.";
                return false;
            }

            if (!haveData)
            {
                error = "No data chunk found.";
                return false;
            }

            info = new WavFormatInfo(audioFormat, numChannels, sampleRate, byteRate, blockAlign, bitsPerSample, dataOffset, dataSize);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool IsStandardRateFloatMono(WavFormatInfo info)
    {
        return IsAccepted(info, WavAcceptanceProfile.PlaybackFloatMonoStandardRates);
    }

    public static bool IsAccepted(WavFormatInfo info, WavAcceptanceProfile profile)
    {
        if (profile.RequireIeeeFloat32 && !info.IsIeeeFloat32)
        {
            return false;
        }

        if (profile.RequireMono && info.NumChannels != 1)
        {
            return false;
        }

        if (!info.HasConsistentBlockAlign ||
            !info.HasConsistentByteRate ||
            !info.HasAlignedData ||
            info.DataSize == 0)
        {
            return false;
        }

        return profile.AcceptedSampleRates == null || profile.AcceptedSampleRates.Contains(info.SampleRate);
    }

    private static bool TryReadExtensibleSubFormat(BinaryReader reader, out ushort audioFormat)
    {
        audioFormat = reader.ReadUInt16();
        byte[] tail = reader.ReadBytes(WaveSubFormatGuidTail.Length);
        return tail.Length == WaveSubFormatGuidTail.Length &&
               tail.AsSpan().SequenceEqual(WaveSubFormatGuidTail);
    }

    private static string ReadFourCc(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        return bytes.Length == 4
            ? new string(new[] { (char)bytes[0], (char)bytes[1], (char)bytes[2], (char)bytes[3] })
            : "";
    }
}
