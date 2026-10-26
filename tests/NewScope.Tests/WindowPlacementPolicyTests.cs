using NewScope.Services;

namespace NewScope.Tests;

public class WindowPlacementPolicyTests
{
    [Fact]
    public void KeepsPositionInsideWorkArea()
    {
        var area = new WindowWorkArea(0, 0, 1920, 1040);

        var position = WindowPlacementPolicy.Clamp(100, 200, 1200, 800, area);

        Assert.Equal((100, 200), position);
    }

    [Fact]
    public void PushesOffscreenWindowBackIntoWorkArea()
    {
        var area = new WindowWorkArea(1920, 0, 1920, 1040);

        var position = WindowPlacementPolicy.Clamp(4000, -200, 1200, 800, area);

        Assert.Equal((2640, 0), position);
    }

    [Fact]
    public void HandlesWindowLargerThanWorkArea()
    {
        var area = new WindowWorkArea(-1920, 0, 1920, 1080);

        var position = WindowPlacementPolicy.Clamp(-5000, 5000, 2500, 1500, area);

        Assert.Equal((-1920, 0), position);
    }

    [Fact]
    public void ContainsUsesHalfOpenBounds()
    {
        var area = new WindowWorkArea(100, 200, 300, 400);

        Assert.True(area.Contains(100, 200));
        Assert.True(area.Contains(399, 599));
        Assert.False(area.Contains(400, 600));
    }
}
