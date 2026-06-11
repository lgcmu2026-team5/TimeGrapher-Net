using TimeGrapher.Core.Shared;
using TimeGrapher.Platform.WindowsAudio;
using Xunit;

namespace TimeGrapher.Platform.WindowsAudio.Tests;

/// <summary>
/// Pins the TryStop pending-thread contract the run lifecycle's Stopping/retry
/// flow depends on — the Windows twin of the Linux worker's stop/teardown
/// tests. The InstallCaptureForTests seam replaces the blocking device
/// teardown with a controllable delegate, so no audio hardware is needed.
/// </summary>
public sealed class AudioCaptureWorkerTests
{
    [Fact]
    public void TryStop_TimeoutLeavesWorkerRestoppable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var worker = new AudioCaptureWorker(new MasterAudioBuffer(48000));
        using var release = new ManualResetEventSlim(initialState: false);
        worker.InstallCaptureForTests(() => release.Wait());

        // The blocked teardown must time out, not report success.
        Assert.False(worker.TryStop(TimeSpan.FromMilliseconds(100)));

        release.Set();

        // The retry joins the same teardown and completes it.
        Assert.True(worker.TryStop(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void TryStop_RetryWaitsForThePendingTeardownInsteadOfReportingSuccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var worker = new AudioCaptureWorker(new MasterAudioBuffer(48000));
        using var release = new ManualResetEventSlim(initialState: false);
        worker.InstallCaptureForTests(() => release.Wait());

        Assert.False(worker.TryStop(TimeSpan.FromMilliseconds(100)));

        // _audioInput is already cleared by the first attempt; a retry must
        // join the still-blocked teardown thread, not succeed on the null field.
        Assert.False(worker.TryStop(TimeSpan.FromMilliseconds(100)));

        release.Set();
        Assert.True(worker.TryStop(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void TryStop_AlsoStopsACaptureStartedSinceTheTimedOutStop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var worker = new AudioCaptureWorker(new MasterAudioBuffer(48000));
        using var firstRelease = new ManualResetEventSlim(initialState: false);
        int teardowns = 0;
        worker.InstallCaptureForTests(() =>
        {
            Interlocked.Increment(ref teardowns);
            firstRelease.Wait();
        });

        Assert.False(worker.TryStop(TimeSpan.FromMilliseconds(100)));
        firstRelease.Set();

        // A new capture begins while the old teardown is still pending; the
        // next stop must fall through and tear that one down too.
        worker.InstallCaptureForTests(() => Interlocked.Increment(ref teardowns));

        Assert.True(worker.TryStop(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, teardowns);
    }

    [Fact]
    public void TryStop_WithoutActiveCaptureReportsSuccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var worker = new AudioCaptureWorker(new MasterAudioBuffer(48000));

        Assert.True(worker.TryStop(TimeSpan.FromSeconds(1)));
    }
}
