using Avalonia.Controls;
using TimeGrapher.App.Rendering;
using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class SpectrogramFrameConsumerTests
{
    [Fact]
    public void ObserveFrameCachesLatestSpectrogramImageAndResetClearsIt()
    {
        var renderer = new SpectrogramRenderer(new Image(), new Image());
        var consumer = new SpectrogramFrameConsumer(renderer);
        var spectrogramImage = new PixelBuffer(2, 2);
        spectrogramImage.Fill(Argb.Red);

        consumer.ObserveFrame(new AnalysisFrame
        {
            SpectrogramImageUpdated = true,
            SpectrogramImage = spectrogramImage,
        });

        Assert.Same(spectrogramImage, consumer.LatestSpectrogramImage);

        consumer.ObserveFrame(new AnalysisFrame()); // no publish: latest is kept
        Assert.Same(spectrogramImage, consumer.LatestSpectrogramImage);

        consumer.Reset(new AnalysisTabResetContext(48000, 10, 250));

        Assert.Null(consumer.LatestSpectrogramImage);
    }
}
