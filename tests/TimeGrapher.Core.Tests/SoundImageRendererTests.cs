using TimeGrapher.Core.Imaging;
using TimeGrapher.Core.Shared;
using TimeGrapher.Core.Sim;
using Xunit;

namespace TimeGrapher.Core.Tests;

public sealed class SoundImageRendererTests
{
    private const uint White = 0xFFFFFFFFu;
    private const uint Black = 0xFF000000u;
    private const uint Red = 0xFFFF0000u;

    private static SoundImageRenderer NewRenderer(PixelBuffer image, uint background, double bph = 0.0)
    {
        var renderer = new SoundImageRenderer();
        bool ok = renderer.Initialize(image, new SoundImageRenderer.Config
        {
            Bph = bph,
            SampleRateHz = 48000.0,
            SoundColor = Red,
            BackgroundColor = background,
            WarmupColumns = 2,
            AnchorColumns = 12,
            Gamma = 0.5f,
            LivePreviewCurrentColumn = true,
        });
        Assert.True(ok);
        return renderer;
    }

    private static int CountWhere(PixelBuffer image, Func<uint, bool> predicate)
        => image.Pixels.Count(predicate);

    [Fact]
    public void Initialize_FillsImageWithBackgroundColor()
    {
        var image = new PixelBuffer(64, 48);
        NewRenderer(image, White);

        Assert.All(image.Pixels, px => Assert.Equal(White, px));
    }

    [Fact]
    public void ProcessSamples_WithoutValidBph_RendersNothing()
    {
        // Sound print is laid out by beat period; with no BPH lock the renderer only
        // counts samples and must not draw (this is why the tab is blank pre-sync).
        var image = new PixelBuffer(64, 48);
        SoundImageRenderer renderer = NewRenderer(image, White, bph: 0.0);

        var synth = new WatchSynthStream(CleanSynth(28800));
        var block = new float[48000]; // 1 s
        synth.Generate(block);
        renderer.ProcessSamples(block);

        Assert.All(image.Pixels, px => Assert.Equal(White, px));
    }

    [Fact]
    public void Recolor_OnBlankImage_RepaintsEveryPixel()
    {
        var image = new PixelBuffer(64, 48);
        SoundImageRenderer renderer = NewRenderer(image, White);

        renderer.Recolor(Black);

        Assert.All(image.Pixels, px => Assert.Equal(Black, px));
    }

    [Fact]
    public void Recolor_AfterRendering_PreservesTraceGeometry()
    {
        var image = new PixelBuffer(200, 80);
        SoundImageRenderer renderer = NewRenderer(image, White, bph: 28800.0);
        renderer.SetBph(28800.0);

        var synth = new WatchSynthStream(CleanSynth(28800));
        var block = new float[4096];
        for (int fed = 0; fed < 48000 * 6; fed += block.Length) // ~6 s
        {
            synth.Generate(block);
            renderer.ProcessSamples(block);
        }

        int drawnBefore = CountWhere(image, px => px != White);
        Assert.True(drawnBefore > 0, "expected the renderer to draw trace pixels once BPH is locked");

        // Capture which pixels carry trace (differ from the current background).
        bool[] traceMask = image.Pixels.Select(px => px != White).ToArray();

        renderer.Recolor(Black);

        // Background pixels must now be the new background; trace pixels must remain
        // trace (the geometry rebuilt from retained bins, only the tint changed).
        for (int i = 0; i < image.Pixels.Length; i++)
        {
            if (traceMask[i])
            {
                Assert.NotEqual(White, image.Pixels[i]);
            }
            else
            {
                Assert.Equal(Black, image.Pixels[i]);
            }
        }
    }

    private static WatchSynthStreamConfig CleanSynth(int bph)
    {
        WatchSynthStreamConfig cfg = WatchSynthStreamConfig.Clean();
        cfg.SampleRateHz = 48000;
        cfg.Bph = bph;
        cfg.PcmPeakAmplitude = 0.40;
        cfg.NoisePeakAmplitude = 0.0;
        return cfg;
    }
}
