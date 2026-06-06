using Avalonia.Controls;
using TimeGrapher.App.Rendering;
using TimeGrapher.App.Tabs;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class SoundPrintFrameConsumerTests
{
    [Fact]
    public void RenderFrameUsesLatestObservedSoundImageWhenActiveFrameHasNoImageUpdate()
    {
        var image = new Image();
        var renderer = new SoundPrintRenderer(image);
        var consumer = new SoundPrintFrameConsumer(renderer);
        var soundImage = new PixelBuffer(2, 2);
        soundImage.Fill(Argb.Red);

        consumer.ObserveFrame(new AnalysisFrame
        {
            SoundImageUpdated = true,
            SoundImage = soundImage,
        });

        Assert.Same(soundImage, consumer.LatestSoundImage);

        consumer.Reset(new AnalysisTabResetContext(48000, 10, 250));

        Assert.Null(consumer.LatestSoundImage);
    }
}
