using TimeGrapher.Core.AudioIo;
using TimeGrapher.Core.Shared;
using TimeGrapher.Core.Sim;
using Xunit;

namespace TimeGrapher.Core.Tests;

public sealed class WorkerPauseTests
{
    [Fact]
    public void PlaybackWorkerPauseStopsWritingSamplesUntilResumed()
    {
        string path = CreatePlaybackWav(sampleRate: 48000, seconds: 2);
        var buffer = new MasterAudioBuffer(48000);

        try
        {
            using var worker = new PlaybackWorker(buffer, 48000);
            using var paused = new ManualResetEventSlim();
            using var resumed = new ManualResetEventSlim();
            int dataReadyCount = 0;

            worker.DataReady += () =>
            {
                int count = Interlocked.Increment(ref dataReadyCount);
                if (count == 1)
                {
                    worker.SetPaused(true);
                    paused.Set();
                }
                else if (count > 1)
                {
                    resumed.Set();
                }
            };

            Assert.True(worker.Start(path));
            Assert.True(paused.Wait(TimeSpan.FromSeconds(2)));
            ulong pausedAt = buffer.GetSnapshot().TotalSamplesWritten;

            Assert.True(worker.IsPaused);
            Thread.Sleep(150);
            Assert.Equal(pausedAt, buffer.GetSnapshot().TotalSamplesWritten);

            worker.SetPaused(false);

            Assert.False(worker.IsPaused);
            Assert.True(resumed.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(buffer.GetSnapshot().TotalSamplesWritten > pausedAt);
            Assert.True(worker.TryStop(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SimWorkerPauseStopsWritingSamplesUntilResumed()
    {
        var buffer = new MasterAudioBuffer(48000);
        using var worker = new SimWorker(buffer, 48000);
        using var paused = new ManualResetEventSlim();
        using var resumed = new ManualResetEventSlim();
        int dataReadyCount = 0;

        worker.DataReady += () =>
        {
            int count = Interlocked.Increment(ref dataReadyCount);
            if (count == 1)
            {
                worker.SetPaused(true);
                paused.Set();
            }
            else if (count > 1)
            {
                resumed.Set();
            }
        };

        Assert.True(worker.Start(WatchSynthStreamConfig.Clean()));
        Assert.True(paused.Wait(TimeSpan.FromSeconds(2)));
        ulong pausedAt = buffer.GetSnapshot().TotalSamplesWritten;

        Assert.True(worker.IsPaused);
        Thread.Sleep(150);
        Assert.Equal(pausedAt, buffer.GetSnapshot().TotalSamplesWritten);

        worker.SetPaused(false);

        Assert.False(worker.IsPaused);
        Assert.True(resumed.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(buffer.GetSnapshot().TotalSamplesWritten > pausedAt);
        Assert.True(worker.TryStop(TimeSpan.FromSeconds(2)));
    }

    private static string CreatePlaybackWav(int sampleRate, int seconds)
    {
        string path = Path.Combine(Path.GetTempPath(), "timegrapher-playback-pause-" + Guid.NewGuid().ToString("N") + ".wav");
        int sampleCount = sampleRate * seconds;
        float[] samples = new float[sampleCount];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)Math.Sin(i * 0.01);
        }

        using var writer = new WavStreamWriter();
        Assert.True(writer.Open(path, sampleRate, channels: 1));
        Assert.True(writer.Write(samples));
        Assert.True(writer.Close());
        return path;
    }
}
