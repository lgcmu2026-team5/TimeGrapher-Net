using TimeGrapher.Core.AudioIo;
using Xunit;

namespace TimeGrapher.Core.Tests;

public sealed class WavProbeTests
{
    [Fact]
    public void TryReadFormatReadsFloatMonoWav()
    {
        using TempWavFile file = TempWavFile.Create(WavProbe.WaveFormatIeeeFloat, channels: 1, sampleRate: 48000, bitsPerSample: 32, dataBytes: 16);

        Assert.True(WavProbe.TryReadFormat(file.Path, out WavFormatInfo info, out string error));
        Assert.Equal("", error);
        Assert.True(info.IsIeeeFloat32Mono);
        Assert.True(WavProbe.IsStandardRateFloatMono(info));
    }

    [Fact]
    public void StandardRateCheckRejectsStereoAndNonStandardRates()
    {
        var stereo = new WavFormatInfo(WavProbe.WaveFormatIeeeFloat, 2, 48000, 384000, 8, 32, 44, 16);
        var nonStandardRate = new WavFormatInfo(WavProbe.WaveFormatIeeeFloat, 1, 44100, 176400, 4, 32, 44, 16);

        Assert.False(WavProbe.IsStandardRateFloatMono(stereo));
        Assert.False(WavProbe.IsStandardRateFloatMono(nonStandardRate));
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(96000)]
    [InlineData(192000)]
    [InlineData(384000)]
    public void PlaybackAcceptanceProfileAcceptsStandardRates(int sampleRate)
    {
        var info = new WavFormatInfo(WavProbe.WaveFormatIeeeFloat, 1, sampleRate, (uint)(sampleRate * 4), 4, 32, 44, 16);

        Assert.True(WavProbe.IsAccepted(info, WavAcceptanceProfile.PlaybackFloatMonoStandardRates));
    }

    [Fact]
    public void PlaybackAcceptanceProfileRejectsInconsistentLayout()
    {
        var badBlockAlign = new WavFormatInfo(WavProbe.WaveFormatIeeeFloat, 1, 48000, 192000, 8, 32, 44, 16);
        var badByteRate = new WavFormatInfo(WavProbe.WaveFormatIeeeFloat, 1, 48000, 123, 4, 32, 44, 16);
        var unalignedData = new WavFormatInfo(WavProbe.WaveFormatIeeeFloat, 1, 48000, 192000, 4, 32, 44, 18);

        Assert.False(WavProbe.IsAccepted(badBlockAlign, WavAcceptanceProfile.PlaybackFloatMonoStandardRates));
        Assert.False(WavProbe.IsAccepted(badByteRate, WavAcceptanceProfile.PlaybackFloatMonoStandardRates));
        Assert.False(WavProbe.IsAccepted(unalignedData, WavAcceptanceProfile.PlaybackFloatMonoStandardRates));
    }

    [Fact]
    public void TryReadFormatReadsExtensibleFloatMonoWav()
    {
        using TempWavFile file = TempWavFile.Create(
            WavProbe.WaveFormatIeeeFloat,
            channels: 1,
            sampleRate: 96000,
            bitsPerSample: 32,
            dataBytes: 16,
            extensible: true);

        Assert.True(WavProbe.TryReadFormat(file.Path, out WavFormatInfo info, out string error));
        Assert.Equal("", error);
        Assert.Equal(WavProbe.WaveFormatIeeeFloat, info.AudioFormat);
        Assert.True(WavProbe.IsAccepted(info, WavAcceptanceProfile.PlaybackFloatMonoStandardRates));
    }

    [Fact]
    public void TryReadFormatRejectsInvalidExtensibleGuid()
    {
        using TempWavFile file = TempWavFile.Create(
            WavProbe.WaveFormatIeeeFloat,
            channels: 1,
            sampleRate: 96000,
            bitsPerSample: 32,
            dataBytes: 16,
            extensible: true,
            validExtensibleGuid: false);

        Assert.False(WavProbe.TryReadFormat(file.Path, out _, out string error));
        Assert.Equal("Invalid WAVE_FORMAT_EXTENSIBLE SubFormat GUID.", error);
    }

    [Fact]
    public void WavFileReaderAcceptanceProfileRejectsNonStandardPlaybackRate()
    {
        using TempWavFile file = TempWavFile.Create(WavProbe.WaveFormatIeeeFloat, channels: 1, sampleRate: 44100, bitsPerSample: 32, dataBytes: 16);

        Assert.Throws<InvalidDataException>(() =>
            WavFileReader.ReadMonoFloat(file.Path, WavAcceptanceProfile.PlaybackFloatMonoStandardRates));
    }

    [Fact]
    public void TryReadFormatRejectsMissingDataChunk()
    {
        using TempWavFile file = TempWavFile.Create(WavProbe.WaveFormatIeeeFloat, channels: 1, sampleRate: 48000, bitsPerSample: 32, dataBytes: null);

        Assert.False(WavProbe.TryReadFormat(file.Path, out _, out string error));
        Assert.Equal("No data chunk found.", error);
    }

    [Fact]
    public void TryReadFormatRejectsTruncatedDataChunk()
    {
        using TempWavFile file = TempWavFile.Create(
            WavProbe.WaveFormatIeeeFloat,
            channels: 1,
            sampleRate: 48000,
            bitsPerSample: 32,
            dataBytes: 16,
            actualDataBytes: 8);

        Assert.False(WavProbe.TryReadFormat(file.Path, out _, out string error));
        Assert.Equal("Invalid data chunk.", error);
    }

    private sealed class TempWavFile : IDisposable
    {
        private TempWavFile(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempWavFile Create(
            ushort format,
            ushort channels,
            int sampleRate,
            ushort bitsPerSample,
            int? dataBytes,
            bool extensible = false,
            bool validExtensibleGuid = true,
            int? actualDataBytes = null)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "timegrapher-wav-probe-" + Guid.NewGuid().ToString("N") + ".wav");
            ushort blockAlign = (ushort)(channels * bitsPerSample / 8);
            uint byteRate = (uint)(sampleRate * blockAlign);
            uint fmtSize = extensible ? 40u : 16u;
            uint riffSize = 4 + 8 + fmtSize + (dataBytes.HasValue ? 8u + (uint)dataBytes.Value : 0u);

            using (FileStream stream = File.Create(path))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                writer.Write(riffSize);
                writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
                writer.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                writer.Write(fmtSize);
                writer.Write(extensible ? WavProbe.WaveFormatExtensible : format);
                writer.Write(channels);
                writer.Write((uint)sampleRate);
                writer.Write(byteRate);
                writer.Write(blockAlign);
                writer.Write(bitsPerSample);
                if (extensible)
                {
                    writer.Write((ushort)22); // cbSize
                    writer.Write(bitsPerSample);
                    writer.Write((uint)0); // channel mask
                    writer.Write(format); // SubFormat GUID first two bytes
                    writer.Write(validExtensibleGuid
                        ? new byte[] { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 }
                        : new byte[14]);
                }

                if (dataBytes.HasValue)
                {
                    writer.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                    writer.Write((uint)dataBytes.Value);
                    writer.Write(new byte[actualDataBytes ?? dataBytes.Value]);
                }
            }

            return new TempWavFile(path);
        }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
