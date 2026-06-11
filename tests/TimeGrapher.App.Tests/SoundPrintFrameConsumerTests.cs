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

    [Fact]
    public void RemapBackgroundSwapsOnlyTheBackgroundIntoACopy()
    {
        var soundImage = new PixelBuffer(2, 2);
        soundImage.Fill(0xFFEEEEEE);
        soundImage.SetPixel(1, 1, Argb.Red); // a trace pixel must survive

        PixelBuffer recolored = SoundPrintFrameConsumer.RemapBackground(
            soundImage, oldBackground: 0xFFEEEEEE, newBackground: 0xFF111111);

        Assert.NotSame(soundImage, recolored); // published pool buffer is not mutated
        Assert.Equal(0xFF111111u, recolored.GetPixel(0, 0));
        Assert.Equal(Argb.Red, recolored.GetPixel(1, 1));
        Assert.Equal(0xFFEEEEEEu, soundImage.GetPixel(0, 0));
    }

    [Fact]
    public void ReRoutedKeptFrameDoesNotRestoreTheStaleBackground()
    {
        var renderer = new SoundPrintRenderer(new Image());
        var consumer = new SoundPrintFrameConsumer(renderer);
        consumer.TryRemapKeptImage(0xFFEEEEEE, out _); // displayed background = light

        var soundImage = new PixelBuffer(2, 2);
        soundImage.Fill(0xFFEEEEEE);
        var keptFrame = new AnalysisFrame
        {
            SoundImageUpdated = true,
            SoundImage = soundImage,
        };
        consumer.ObserveFrame(keptFrame);

        // Theme toggle remaps the kept image into a copy.
        Assert.True(consumer.TryRemapKeptImage(0xFF111111, out PixelBuffer? remapped));
        Assert.Equal(0xFF111111u, remapped!.GetPixel(0, 0));

        // The kept frame re-routes (tab switch / scrub while paused) with the
        // SAME pooled buffer: it must not undo the remap.
        consumer.ObserveFrame(keptFrame);
        Assert.Same(remapped, consumer.LatestSoundImage);

        // A genuinely new publish (different buffer reference) replaces it.
        var next = new PixelBuffer(2, 2);
        consumer.ObserveFrame(new AnalysisFrame { SoundImageUpdated = true, SoundImage = next });
        Assert.Same(next, consumer.LatestSoundImage);
    }

    [Fact]
    public void ApplyThemeWithoutAKeptImageDoesNotBlit()
    {
        // Headless: any blit would throw (WriteableBitmap needs the Avalonia
        // platform), so completing without rendering IS the assertion.
        var renderer = new SoundPrintRenderer(new Image());
        var consumer = new SoundPrintFrameConsumer(renderer);

        consumer.ApplyTheme(Palette(scopeBg: 0xFFEEEEEE));
        consumer.ApplyTheme(Palette(scopeBg: 0xFF111111));

        Assert.Null(consumer.LatestSoundImage);
    }

    private static PlotThemePalette Palette(uint scopeBg) => new(
        SurfaceBg: 0xFF101010,
        ScopeBg: scopeBg,
        ScopeGrid: 0xFF303030,
        TextPrimary: 0xFF000000,
        TraceWave: 0xFF404040,
        TraceTick: 0xFF00AA00,
        TraceTock: 0xFFAA0000);
}
