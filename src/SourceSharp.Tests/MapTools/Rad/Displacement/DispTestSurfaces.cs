using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Rad.Displacement;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.Tests.MapTools.Disp;

namespace SourceSharp.Tests.MapTools.Rad.Displacement;

/// <summary>
/// Hand-built vrad displacement surfaces for the unit tier: a 256-unit floor
/// quad at the origin (point 0 the minimum corner, u along +X, v along +Y,
/// facing +Z) with a height field, lit at one luxel per 16 units.
/// </summary>
internal static class DispTestSurfaces
{
    public static readonly Vec3[] Floor = DispFixtures.UnitFloor();

    public static TexInfo Tex(float unitsPerLuxel = 16.0f)
    {
        TexInfo t = default;
        t.LightmapVecsLuxelsPerWorldUnits[0] = 1.0f / unitsPerLuxel;
        t.LightmapVecsLuxelsPerWorldUnits[5] = -1.0f / unitsPerLuxel;
        t.TextureVecsTexelsPerWorldUnits[0] = 0.25f;
        t.TextureVecsTexelsPerWorldUnits[5] = -0.25f;
        return t;
    }

    public static CoreDispInfo Core(
        Func<int, int, float>? height = null, int power = 2, bool stockNormalMean = false)
    {
        MapDisplacement disp = DispFixtures.Heightfield(power, Floor[0], height);
        return DispFixtures.Core(disp, Floor, stock: false, stockNormalMean: stockNormalMean);
    }

    public static VradDispSurface Surface(
        Func<int, int, float>? height = null,
        int power = 2,
        bool stockNormalMean = false,
        float unitsPerLuxel = 16.0f,
        DirectLightingSettings? settings = null) =>
        VradDispSurface.Create(
            Core(height, power, stockNormalMean), Tex(unitsPerLuxel), settings ?? new DirectLightingSettings(), stockNormalise: false);

    /// <summary>The V-crease p3f2 handed on: a valley along x = 2 of a power-2 grid.</summary>
    public static float Crease(int x, int y) => MathF.Abs(x - 2) * 64.0f;

    public static int Index(VradDispSurface s, int x, int y) => (y * s.Width) + x;

    /// <summary>The u (or v) that lands on grid coordinate <paramref name="grid"/> exactly as DispUVToSurfPoint scales it.</summary>
    public static float UvFor(VradDispSurface s, float grid) => grid / ((float)s.Width - 1.000001f);

    public static bool Near(Vec3 a, Vec3 b, float eps) =>
        MathF.Abs(a.X - b.X) <= eps && MathF.Abs(a.Y - b.Y) <= eps && MathF.Abs(a.Z - b.Z) <= eps;
}
