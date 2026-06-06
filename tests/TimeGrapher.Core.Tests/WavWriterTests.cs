using TimeGrapher.Core.AudioIo;
using Xunit;

namespace TimeGrapher.Core.Tests;

public sealed class WavWriterTests
{
    [Fact]
    public void QueuedWavStreamWriterWritesFloatMonoWavAndCloses()
    {
        string path = Path.Combine(Path.GetTempPath(), "timegrapher-queued-writer-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using var writer = new QueuedWavStreamWriter(queueCapacity: 2);
            Assert.True(writer.Open(path, sampleRate: 48000, channels: 1));
            Assert.True(writer.Write(new float[] { 0.1f, -0.2f, 0.3f, -0.4f }));
            Assert.True(writer.Close());

            WavData data = WavFileReader.ReadMonoFloat(path, WavAcceptanceProfile.PlaybackFloatMonoStandardRates);

            Assert.Equal(48000, data.SampleRate);
            Assert.Equal(new[] { 0.1f, -0.2f, 0.3f, -0.4f }, data.Samples);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
