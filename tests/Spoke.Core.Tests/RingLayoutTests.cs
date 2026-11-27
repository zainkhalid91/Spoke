using Spoke.Core;

namespace Spoke.Core.Tests;

public class RingLayoutTests
{
    private const float Up = -MathF.PI / 2f;

    [Fact]
    public void Band_sits_outside_the_wheel()
    {
        var r = new RingLayout(5, Up, 170f);
        Assert.Equal(180f, r.InnerRadius);
        Assert.Equal(250f, r.OuterRadius);
        Assert.Equal(250f, RingLayout.Reach(170f));
    }

    [Fact]
    public void Items_are_centred_on_the_parent_slice()
    {
        var r = new RingLayout(3, Up, 170f);
        Assert.Equal(Up, r.CenterAngle(1), 4);                        // odd count: middle item on the slice
        var even = new RingLayout(4, Up, 170f);
        Assert.Equal(Up, (even.CenterAngle(1) + even.CenterAngle(2)) / 2f, 4);
    }

    [Fact]
    public void HitTest_finds_items_and_misses_the_wheel()
    {
        var r = new RingLayout(3, Up, 170f);
        float mid = (r.InnerRadius + r.OuterRadius) / 2f;
        for (int i = 0; i < 3; i++)
        {
            float a = r.CenterAngle(i);
            Assert.Equal(i, r.HitTest(mid * MathF.Cos(a), mid * MathF.Sin(a)));
        }
        Assert.Equal(-1, r.HitTest(0, -100));          // inside the wheel
        Assert.Equal(-1, r.HitTest(0, 400));           // far away
        Assert.Equal(-1, r.HitTest(0, mid));           // right radius, opposite side: no item there
    }

    [Fact]
    public void Many_items_never_wrap_past_300_degrees()
    {
        var r = new RingLayout(RingLayout.MaxItems, Up, 100f);
        Assert.True(r.Step * r.Count <= MathF.Tau * 300f / 360f + 1e-4f);
    }

    [Theory]
    [InlineData(-1, 1, 0)]
    [InlineData(-1, -1, 4)]
    [InlineData(0, -1, 0)]
    [InlineData(4, 1, 4)]
    [InlineData(2, 1, 3)]
    public void Arrow_keys_stop_at_the_ends(int current, int dir, int expected)
        => Assert.Equal(expected, new RingLayout(5, Up, 170f).Step1(current, dir));

    [Fact]
    public void Too_many_items_is_rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RingLayout(RingLayout.MaxItems + 1, Up, 170f));

    [Theory]
    [InlineData(null, SubMenuStyle.Ring)]
    [InlineData("ring", SubMenuStyle.Ring)]
    [InlineData("Extend", SubMenuStyle.Ring)]
    [InlineData("wheel", SubMenuStyle.Wheel)]
    [InlineData("new", SubMenuStyle.Wheel)]
    public void SubMenus_parses(string? value, SubMenuStyle expected)
    {
        string json = value is null ? "{}" : $$"""{ "subMenus": "{{value}}" }""";
        var r = ConfigLoader.Parse(json);
        Assert.Empty(r.Errors);
        Assert.Equal(expected, r.Appearance.SubMenus);
    }

    [Fact]
    public void SubMenus_rejects_unknown_values()
        => Assert.Contains(ConfigLoader.Parse("""{ "subMenus": "sideways" }""").Errors, e => e.Contains("subMenus"));

    [Fact]
    public void Onyx_is_a_theme()
    {
        var r = ConfigLoader.Parse("""{ "theme": "onyx" }""");
        Assert.Empty(r.Errors);
        Assert.Equal("Onyx", r.Appearance.Theme);
    }
}
