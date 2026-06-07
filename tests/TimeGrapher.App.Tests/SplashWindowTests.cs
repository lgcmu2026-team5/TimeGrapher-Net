using TimeGrapher.App.Views;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class SplashWindowTests
{
    [Fact]
    public void FrameSelectionFollowsElapsedThirtyFpsTime()
    {
        Assert.Equal(1, SplashWindow.GetFrameNumberForElapsed(TimeSpan.Zero));
        Assert.Equal(1, SplashWindow.GetFrameNumberForElapsed(TimeSpan.FromSeconds(-1.0)));
        Assert.Equal(2, SplashWindow.GetFrameNumberForElapsed(TimeSpan.FromSeconds(1.0 / 30.0)));
        Assert.Equal(31, SplashWindow.GetFrameNumberForElapsed(TimeSpan.FromSeconds(1.0)));
        Assert.Equal(122, SplashWindow.GetFrameNumberForElapsed(TimeSpan.FromSeconds(122.0 / 30.0)));
        Assert.Equal(122, SplashWindow.GetFrameNumberForElapsed(TimeSpan.FromSeconds(10.0)));
    }
}
