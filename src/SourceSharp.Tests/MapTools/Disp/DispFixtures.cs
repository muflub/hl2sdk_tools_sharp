using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Options;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// Hand-built displacements for the unit tier: a quad, a height field, and the
/// base face the lump builder needs, with no map and no content.
/// </summary>
/// <remarks>
/// Grid convention, from <c>GenerateDispSurf</c>:
/// vertex <c>(x, y)</c> is index <c>y * side + x</c>, <c>x</c> runs from
/// point 0 towards point 3 and <c>y</c> from point 0 towards point 1. The
/// quads here are wound so that point 0 is the minimum corner, x is +X and y
/// is +Y, and <c>GetNormal</c> is +Z.
/// </remarks>
internal static class DispFixtures
{
    /// <summary>One luxel per 16 units, as <c>lightmapscale 16</c> gives.</summary>
    public const float LuxelsPerUnit = 1.0f / 16.0f;

    /// <summary>
    /// An axis-aligned floor quad from <paramref name="min"/>, <paramref name="sizeX"/>
    /// by <paramref name="sizeY"/>, facing +Z.
    /// </summary>
    public static Vec3[] FloorQuad(Vec3 min, float sizeX, float sizeY) =>
    [
        min,
        new Vec3(min.X, min.Y + sizeY, min.Z),
        new Vec3(min.X + sizeX, min.Y + sizeY, min.Z),
        new Vec3(min.X + sizeX, min.Y, min.Z),
    ];

    /// <summary>A 256-unit floor quad at the origin.</summary>
    public static Vec3[] UnitFloor() => FloorQuad(Vec3.Zero, 256, 256);

    /// <summary>
    /// A displacement whose field is straight up, <paramref name="height"/>
    /// units at grid <c>(x, y)</c>, with the start at <paramref name="start"/>.
    /// </summary>
    public static MapDisplacement Heightfield(
        int power, Vec3 start, Func<int, int, float>? height = null, Func<int, int, float>? alpha = null)
    {
        MapDisplacement disp = new(power) { StartPosition = start };
        int side = (1 << power) + 1;

        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                int i = (y * side) + x;
                disp.FieldVectors[i] = new Vec3(0, 0, 1);
                disp.FieldDistances[i] = height?.Invoke(x, y) ?? 0.0f;
                disp.AlphaValues[i] = alpha?.Invoke(x, y) ?? 0.0f;
            }
        }

        return disp;
    }

    /// <summary>A base face over a winding, with the stock lightmap axes.</summary>
    public static DisplacementFace Face(
        Vec3[] winding,
        int faceIndex = 0,
        int contents = 1,
        Vec3? lightmapU = null,
        Vec3? lightmapV = null) =>
        new(
            faceIndex,
            winding,
            contents,
            lightmapU ?? new Vec3(LuxelsPerUnit, 0, 0),
            lightmapV ?? new Vec3(0, -LuxelsPerUnit, 0),
            [0.25f, 0, 0, 0, 0, -0.25f, 0, 0]);

    /// <summary>
    /// A created core over <paramref name="winding"/>, not yet in any list.
    /// </summary>
    public static CoreDispInfo Core(
        MapDisplacement disp, Vec3[] winding, bool stock = false, bool stockNormalMean = false)
    {
        CoreDispInfo core = new(disp.Power) { ListIndex = 0, StockVertexNormalMean = stockNormalMean };
        core.SetListBase([core]);
        DisplacementLumpBuilder.DispMapToCoreDispInfo(disp, Face(winding), core, stock);
        return core;
    }

    /// <summary>
    /// Runs the whole lump builder over a set of displacements, each on its own
    /// face index, and returns the results with the lumps.
    /// </summary>
    public static (IReadOnlyList<DisplacementResult> Results, DisplacementLumps Lumps) Build(
        IReadOnlyList<(MapDisplacement Disp, Vec3[] Winding)> items,
        ComplianceOptions? compliance = null)
    {
        List<MapDisplacement> disps = [];
        List<DisplacementFace> faces = [];

        for (int i = 0; i < items.Count; i++)
        {
            disps.Add(items[i].Disp);
            faces.Add(Face(items[i].Winding, faceIndex: i));
        }

        DisplacementLumps lumps = new();
        IReadOnlyList<DisplacementResult> results = DisplacementLumpBuilder.Build(
            disps,
            faces,
            new VbspOptions { Compliance = compliance ?? ComplianceOptions.Correct },
            lumps);

        return (results, lumps);
    }

    /// <summary>The flattened index of grid <c>(x, y)</c>.</summary>
    public static int Index(CoreDispInfo core, int x, int y) => (y * core.PostSpacing) + x;

    /// <summary>Bitwise float equality, for facts that promise exact arithmetic.</summary>
    public static bool BitEqual(float a, float b) =>
        BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    /// <summary>Bitwise vector equality.</summary>
    public static bool BitEqual(Vec3 a, Vec3 b) =>
        BitEqual(a.X, b.X) && BitEqual(a.Y, b.Y) && BitEqual(a.Z, b.Z);

    /// <summary>A copy of a lump entry's neighbour table, for comparisons.</summary>
    public static DispNeighbor Edge(DispInfo info, int edge) => info.EdgeNeighbors[edge];
}
