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
