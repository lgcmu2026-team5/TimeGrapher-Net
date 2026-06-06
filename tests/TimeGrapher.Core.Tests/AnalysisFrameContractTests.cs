using TimeGrapher.Core.Analysis;
using TimeGrapher.Core.AudioIo;
using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Shared;
using TimeGrapher.Core.Sim;
using Xunit;

namespace TimeGrapher.Core.Tests;

public sealed class AnalysisFrameContractTests
{
    [Fact]
    public void AnalysisWorkerPublishesScopeSeriesAsReplaceSnapshots()
    {
        const int sampleRate = 48000;
        var buffer = new MasterAudioBuffer(sampleRate);
        var worker = new AnalysisWorker(buffer, new AnalysisWorker.Config
        {
            SampleRate = sampleRate,
            SoundImageWidth = 8,
            SoundImageHeight = 8,
        });

        AnalysisFrame? capturedFrame = null;
        worker.AnalysisFrameReady += frame => capturedFrame = frame;

        var samples = new float[4096];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.25 * Math.Sin(i / 12.0));
        }
        buffer.WriteSamples(samples);

        worker.HandleInputData();

        AnalysisFrame frame = Assert.IsType<AnalysisFrame>(capturedFrame);
        GraphSeriesFrame pcm = Assert.Single(frame.ScopeSeries, series => series.Id == AnalysisGraphSeries.ScopePcm);
        GraphSeriesFrame threshold = Assert.Single(frame.ScopeSeries, series => series.Id == AnalysisGraphSeries.ScopeThreshold);

        Assert.True(pcm.Replace);
        Assert.True(threshold.Replace);
        Assert.True(pcm.X.Count > 0);
        Assert.Equal(pcm.X.Count, pcm.Y.Count);
        Assert.Equal(threshold.X.Count, threshold.Y.Count);
    }

    [Fact]
    public void AnalysisWorkerBoundsScopeSnapshotPointCount()
    {
        const int sampleRate = 48000;
        const int pointBudget = 8;
        var buffer = new MasterAudioBuffer(sampleRate);
        var worker = new AnalysisWorker(buffer, new AnalysisWorker.Config
        {
            SampleRate = sampleRate,
            SoundImageWidth = 8,
            SoundImageHeight = 8,
            ScopeSnapshotPointBudget = pointBudget,
        });

        AnalysisFrame? capturedFrame = null;
        worker.AnalysisFrameReady += frame => capturedFrame = frame;

        var samples = new float[4096];
        Array.Fill(samples, 0.1f);
        buffer.WriteSamples(samples);

        worker.HandleInputData();

        AnalysisFrame frame = Assert.IsType<AnalysisFrame>(capturedFrame);
        GraphSeriesFrame pcm = Assert.Single(frame.ScopeSeries, series => series.Id == AnalysisGraphSeries.ScopePcm);

        Assert.True(pcm.X.Count <= pointBudget);
    }

    [Fact]
    public void AnalysisWorkerPublishesTelemetryAndMetricsForSyntheticStream()
    {
        const int sampleRate = 48000;
        var buffer = new MasterAudioBuffer(sampleRate);
        var worker = new AnalysisWorker(buffer, new AnalysisWorker.Config
        {
            SampleRate = sampleRate,
            AveragingPeriod = 2,
            SoundImageWidth = 8,
            SoundImageHeight = 8,
            ScopeSnapshotPointBudget = 256,
        });

        AnalysisFrame? capturedFrame = null;
        worker.AnalysisFrameReady += frame => capturedFrame = frame;

        WatchSynthStreamConfig synthConfig = WatchSynthStreamConfig.Clean();
        synthConfig.SampleRateHz = sampleRate;
        synthConfig.Bph = 21600;
        synthConfig.PcmPeakAmplitude = 0.40;
        var synth = new WatchSynthStream(synthConfig);

        var block = new float[4096];
        int remaining = sampleRate * 10;
        while (remaining > 0)
        {
            int slice = Math.Min(block.Length, remaining);
            Span<float> span = block.AsSpan(0, slice);
            synth.Generate(span);
            buffer.WriteSamples(span);
            worker.HandleInputData();
            remaining -= slice;
        }

        AnalysisFrame frame = Assert.IsType<AnalysisFrame>(capturedFrame);

        Assert.True(frame.PendingSamples > 0);
        Assert.True(frame.ProcessingElapsedMs >= 0);
        Assert.Contains(frame.RateSeries, series => series.Id == AnalysisGraphSeries.RateTic && series.Replace);
        Assert.Contains(frame.RateSeries, series => series.Id == AnalysisGraphSeries.RateToc && series.Replace);
        Assert.True(frame.MetricsUpdate.ResultsUpdated);
        Assert.False(string.IsNullOrWhiteSpace(frame.MetricsUpdate.ResultsText));
    }

    [Fact]
    public void MasterAudioBufferSnapshotReadsStatsUnderTheBufferLock()
    {
        var buffer = new MasterAudioBuffer(48000);
        buffer.WriteSamples(new float[] { 0.1f, 0.2f, 0.3f });
        buffer.SetStats(50, 960, 48000);

        MasterAudioBufferSnapshot snapshot = buffer.GetSnapshot();

        Assert.Equal<ulong>(3, snapshot.TotalSamplesWritten);
        Assert.Equal(50, snapshot.Fps);
        Assert.Equal(960, snapshot.Spf);
        Assert.Equal(48000, snapshot.Sps);
        Assert.Equal(48000 * MasterAudioBuffer.SecondsOfBuffer, snapshot.NumberOfAudioSamples);
    }

    [Fact]
    public void MasterAudioBufferCopiesAnalysisBlocksAndReportsOverrun()
    {
        const int sampleRate = 10;
        var buffer = new MasterAudioBuffer(sampleRate);
        var samples = Enumerable.Range(0, sampleRate * MasterAudioBuffer.SecondsOfBuffer + 5)
            .Select(i => (float)i)
            .ToArray();
        buffer.WriteSamples(samples);
        MasterAudioBufferSnapshot snapshot = buffer.GetSnapshot();

        var destination = new float[16];
        MasterAudioBufferReadResult read = buffer.CopyAnalysisSamples(destination, snapshot.TotalSamplesWritten);

        Assert.Equal(destination.Length, read.SamplesCopied);
        Assert.True(read.InputOverrun);
        Assert.Equal<ulong>(5, read.InputSamplesDropped);
        Assert.Equal(5, destination[0]);
        Assert.Equal<ulong>((ulong)destination.Length, buffer.AnalysisLastTotalSamplesWritten - read.InputSamplesDropped);
    }

    [Fact]
    public void MasterAudioBufferCopyIsBoundedBySourceSnapshot()
    {
        var buffer = new MasterAudioBuffer(10);
        buffer.WriteSamples(new float[] { 1, 2, 3 });
        MasterAudioBufferSnapshot snapshot = buffer.GetSnapshot();
        buffer.WriteSamples(new float[] { 4, 5 });

        var destination = new float[8];
        MasterAudioBufferReadResult firstRead = buffer.CopyAnalysisSamples(destination, snapshot.TotalSamplesWritten);
        Assert.Equal(3, firstRead.SamplesCopied);
        Assert.Equal(new float[] { 1, 2, 3 }, destination.Take(3).ToArray());

        MasterAudioBufferSnapshot nextSnapshot = buffer.GetSnapshot();
        MasterAudioBufferReadResult secondRead = buffer.CopyAnalysisSamples(destination, nextSnapshot.TotalSamplesWritten);
        Assert.Equal(2, secondRead.SamplesCopied);
        Assert.Equal(new float[] { 4, 5 }, destination.Take(2).ToArray());
    }

    [Fact]
    public void PlaybackWorkerReportsFailedCompletionForMissingFile()
    {
        var buffer = new MasterAudioBuffer(48000);
        var worker = new PlaybackWorker(buffer, 48000);
        using var done = new ManualResetEventSlim();
        PlaybackCompletionReason? reason = null;
        worker.DoneReadingFile += completionReason =>
        {
            reason = completionReason;
            done.Set();
        };

        worker.Start(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav"));

        Assert.True(done.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(PlaybackCompletionReason.Failed, reason);
        Assert.True(worker.TryStop(TimeSpan.FromSeconds(2)));
    }
}
