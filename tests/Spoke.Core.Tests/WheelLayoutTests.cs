using Spoke.Core;

namespace Spoke.Core.Tests;

public class WheelLayoutTests
{
    private readonly WheelLayout _w8 = new(8);

    [Theory]
    [InlineData(0, -100, 0)]    // straight up
    [InlineData(100, 0, 2)]     // right
    [InlineData(0, 100, 4)]     // down
    [InlineData(-100, 0, 6)]    // left
    [InlineData(80, -80, 1)]    // up-right
    [InlineData(-80, -80, 7)]   // up-left
    public void HitTest_picks_slice_by_angle(float dx, float dy, int expected)
        => Assert.Equal(expected, _w8.HitTest(dx, dy));

    [Fact]
    public void HitTest_inside_hub_is_none() => Assert.Equal(-1, _w8.HitTest(10, 10));

    [Fact]
    public void HitTest_past_the_ring_is_none() => Assert.Equal(-1, _w8.HitTest(1500, 3));

    [Fact]
    public void HitTest_edge_margin_still_selects()
    {
        float r = _w8.OuterRadius;
        Assert.Equal(2, _w8.HitTest(r + WheelLayout.EdgeMargin - 1, 0));
        Assert.Equal(-1, _w8.HitTest(r + WheelLayout.EdgeMargin + 1, 0));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    public void Every_slice_centre_hits_its_own_slice(int n)
    {
        var w = new WheelLayout(n);
        for (int i = 0; i < n; i++)
        {
            float a = w.CenterAngle(i);
            Assert.Equal(i, w.HitTest(MathF.Cos(a) * 120, MathF.Sin(a) * 120));
        }
    }

    [Fact]
    public void Slice_boundaries_split_between_neighbours()
    {
        // Just either side of the boundary between slice 0 and 1 (at -90° + 22.5°).
        float b = -MathF.PI / 2 + MathF.PI / 8;
        Assert.Equal(0, _w8.HitTest(MathF.Cos(b - 0.01f) * 120, MathF.Sin(b - 0.01f) * 120));
        Assert.Equal(1, _w8.HitTest(MathF.Cos(b + 0.01f) * 120, MathF.Sin(b + 0.01f) * 120));
    }

    [Theory]
    [InlineData(-1, 1, 0)]
    [InlineData(0, 1, 1)]
    [InlineData(7, 1, 0)]
    [InlineData(0, -1, 7)]
    public void Arrow_navigation_wraps(int cur, int dir, int expected) => Assert.Equal(expected, _w8.Step1(cur, dir));

    [Fact]
    public void Clamp_keeps_wheel_on_screen()
    {
        Assert.Equal((300, 300), WheelLayout.ClampCenter(10, 5, 300, 0, 0, 1920, 1080));
        Assert.Equal((1620, 780), WheelLayout.ClampCenter(1919, 1079, 300, 0, 0, 1920, 1080));
        Assert.Equal((900, 500), WheelLayout.ClampCenter(900, 500, 300, 0, 0, 1920, 1080));
        // second monitor at negative coordinates
        Assert.Equal((-1620, 300), WheelLayout.ClampCenter(-1900, 0, 300, -1920, 0, 0, 1080));
    }
}
