using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// <c>AddPointToBounds</c> (<c>mathlib_base.cpp:1293</c>) as
/// <c>MakeBrushWindings</c> uses it (<c>map.cpp:652</c>).
/// </summary>
public sealed class BrushBoundsTests
{
    [Fact]
    public void APositiveZeroSeenFirstStaysPositiveWhenANegativeZeroFollows()
    {
        // strict <, so -0 does not replace +0; MathF.Min(+0, -0) would.
        Vec3 mins = MapFile.ClearedMins;
        Vec3 maxs = MapFile.ClearedMaxs;

        MapFile.AddPointToBounds(new Vec3(0f, 0f, 0f), ref mins, ref maxs);
        MapFile.AddPointToBounds(new Vec3(-0f, -0f, -0f), ref mins, ref maxs);

        Assert.False(float.IsNegative(mins.X));
    }

    [Fact]
    public void ANegativeZeroSeenFirstStaysNegative()
    {
        Vec3 mins = MapFile.ClearedMins;
        Vec3 maxs = MapFile.ClearedMaxs;

        MapFile.AddPointToBounds(new Vec3(-0f, 0f, 0f), ref mins, ref maxs);
        MapFile.AddPointToBounds(new Vec3(0f, 0f, 0f), ref mins, ref maxs);

        Assert.True(float.IsNegative(mins.X));
    }

    [Fact]
    public void TheMaximumKeepsTheFirstOfTwoEqualZerosToo()
    {
        Vec3 mins = MapFile.ClearedMins;
        Vec3 maxs = MapFile.ClearedMaxs;

        MapFile.AddPointToBounds(new Vec3(-0f, 0f, 0f), ref mins, ref maxs);
        MapFile.AddPointToBounds(new Vec3(0f, 0f, 0f), ref mins, ref maxs);

        Assert.True(float.IsNegative(maxs.X));
    }
}
