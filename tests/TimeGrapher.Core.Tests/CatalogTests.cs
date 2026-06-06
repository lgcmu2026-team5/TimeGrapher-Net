using TimeGrapher.Core.Detection;
using TimeGrapher.Core.Shared;
using Xunit;

namespace TimeGrapher.Core.Tests;

public sealed class CatalogTests
{
    [Fact]
    public void ManualAutoBphAddsAutoEntryBeforeManualRates()
    {
        Assert.Equal(0, BphCatalog.ManualAutoBph[0]);
        Assert.Equal(BphCatalog.ManualBph.Count + 1, BphCatalog.ManualAutoBph.Count);
        Assert.Equal(BphCatalog.ManualBph[0], BphCatalog.ManualAutoBph[1]);
    }

    [Fact]
    public void ManualBphCatalogUsesDetectorRates()
    {
        Assert.Contains(17258, BphCatalog.ManualBph);
        Assert.Contains(17786, BphCatalog.ManualBph);
        Assert.DoesNotContain(11258, BphCatalog.ManualBph);
        Assert.DoesNotContain(17186, BphCatalog.ManualBph);
    }

    [Fact]
    public void StandardSampleRatesAreSharedByPlaybackAndCapture()
    {
        Assert.Equal(new[] { 48000, 96000, 192000, 384000 }, AudioSampleRates.Standard);
        foreach (int rate in AudioSampleRates.Standard)
        {
            Assert.Contains(rate, AudioSampleRates.StandardSet);
        }
    }
}
