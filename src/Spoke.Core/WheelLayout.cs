namespace Spoke.Core;

/// <summary>
/// Pure geometry for a radial wheel. All values are in DIPs, centred on (0,0), with y pointing down
/// (screen space). Slice 0 is centred straight up and slices go clockwise.
/// </summary>
public readonly struct WheelLayout
{
    public readonly int Slices;
    public readonly float HubRadius;
    public readonly float InnerRadius;
    public readonly float OuterRadius;

    /// <summary>Angular gap between slices, in radians (applied half on each side).</summary>
    public readonly float Gap;

    public WheelLayout(int slices, float hubRadius = 58f, float innerRadius = 74f, float outerRadius = 170f, float gap = 0.035f)
    {
        if (slices < 2 || slices > 16) throw new ArgumentOutOfRangeException(nameof(slices));
        Slices = slices;
        HubRadius = hubRadius;
        InnerRadius = innerRadius;
        OuterRadius = outerRadius;
        Gap = gap;
    }

    public float Step => MathF.Tau / Slices;

    /// <summary>Angle (radians, screen space: 0 = +x, clockwise positive) of the centre of a slice.</summary>
    public float CenterAngle(int slice) => -MathF.PI / 2f + slice * Step;

    /// <summary>Start and end angle of a slice's filled area (gap removed).</summary>
    public (float Start, float End) SliceAngles(int slice)
    {
        float c = CenterAngle(slice), half = Step / 2f;
        return (c - half + Gap, c + half - Gap);
    }

    /// <summary>How far past the outer ring a point still counts as on the wheel (DIPs): the hover lift.</summary>
    public const float EdgeMargin = 9f;

    /// <summary>True when a point (relative to the wheel centre) lies beyond the ring and its edge margin.</summary>
    public bool IsOutside(float dx, float dy)
    {
        float r = OuterRadius + EdgeMargin;
        return dx * dx + dy * dy > r * r;
    }

    /// <summary>
    /// Which slice a point (relative to the wheel centre) selects. Inside the hub, or outside the ring (plus
    /// <see cref="EdgeMargin"/>), returns -1: only the wheel itself selects, so a stray click elsewhere never
    /// runs an action.
    /// </summary>
    public int HitTest(float dx, float dy)
    {
        if (dx * dx + dy * dy < HubRadius * HubRadius || IsOutside(dx, dy)) return -1;
        // Rotate so that slice 0's centre is at angle 0, then shift by half a step so slice boundaries fall on multiples of Step.
        float a = MathF.Atan2(dy, dx) + MathF.PI / 2f + Step / 2f;
        a %= MathF.Tau;
        if (a < 0) a += MathF.Tau;
        int i = (int)(a / Step);
        return i >= Slices ? Slices - 1 : i;
    }

    /// <summary>Next slice for arrow-key navigation. -1 (nothing hovered) goes to slice 0.</summary>
    public int Step1(int current, int direction)
    {
        if (current < 0) return 0;
        int n = (current + direction) % Slices;
        return n < 0 ? n + Slices : n;
    }

    /// <summary>
    /// Clamp a wheel centre so the whole wheel (radius <paramref name="extent"/>) stays inside a rectangle.
    /// Coordinates are whatever unit the caller uses (physical pixels in the daemon).
    /// </summary>
    public static (int X, int Y) ClampCenter(int x, int y, int extent, int left, int top, int right, int bottom)
    {
        int cx = Math.Clamp(x, left + extent, Math.Max(left + extent, right - extent));
        int cy = Math.Clamp(y, top + extent, Math.Max(top + extent, bottom - extent));
        return (cx, cy);
    }
}

/// <summary>
/// The outer band of options that opens around a slice (subMenus "ring"). Items sit side by side in an arc
/// centred on the parent slice's angle, between <see cref="InnerRadius"/> and <see cref="OuterRadius"/>.
/// Each item spans a fixed arc length, so a 3-item band is short and a 10-item band wraps further round.
/// </summary>
public readonly struct RingLayout
{
    public const int MaxItems = 12;
    /// <summary>Target width of one item along the band's middle line, in DIPs.</summary>
    public const float ItemArc = 66f;
    public const float Width = 70f;
    /// <summary>Space between the wheel's edge and the band.</summary>
    public const float Gap = 10f;

    public readonly int Count;
    public readonly float Center;        // angle of the parent slice (radians, screen space)
    public readonly float InnerRadius, OuterRadius;
    public readonly float Step;          // angle per item
    public readonly float GapAngle;      // half-gap applied on each side of an item

    public RingLayout(int count, float centerAngle, float wheelOuterRadius)
    {
        if (count < 1 || count > MaxItems) throw new ArgumentOutOfRangeException(nameof(count));
        Count = count;
        Center = centerAngle;
        InnerRadius = wheelOuterRadius + Gap;
        OuterRadius = InnerRadius + Width;
        float mid = (InnerRadius + OuterRadius) / 2f;
        // Never wrap all the way round: at most 300° so the band reads as growing out of its slice.
        Step = MathF.Min(ItemArc / mid, MathF.Tau * (300f / 360f) / count);
        GapAngle = 2.2f / mid;
    }

    /// <summary>How far the band reaches from the wheel centre (for the drawing surface and screen clamping).</summary>
    public static float Reach(float wheelOuterRadius) => wheelOuterRadius + Gap + Width;

    public float Start => Center - Step * Count / 2f;

    public float CenterAngle(int item) => Start + (item + 0.5f) * Step;

    public (float Start, float End) ItemAngles(int item)
    {
        float a = Start + item * Step;
        return (a + GapAngle, a + Step - GapAngle);
    }

    /// <summary>The item under a point (relative to the wheel centre), or -1. A little slack on both edges of the band.</summary>
    public int HitTest(float dx, float dy)
    {
        float d2 = dx * dx + dy * dy;
        float r1 = InnerRadius - Gap / 2f, r2 = OuterRadius + WheelLayout.EdgeMargin;
        if (d2 < r1 * r1 || d2 > r2 * r2) return -1;
        float a = MathF.Atan2(dy, dx) - Start;
        a %= MathF.Tau;
        if (a < 0) a += MathF.Tau;
        int i = (int)(a / Step);
        return i < Count ? i : -1;
    }

    /// <summary>Next item for arrow keys, stopping at the ends (the band isn't a loop). -1 goes to the first.</summary>
    public int Step1(int current, int direction)
    {
        if (current < 0) return direction > 0 ? 0 : Count - 1;
        return Math.Clamp(current + direction, 0, Count - 1);
    }
}
