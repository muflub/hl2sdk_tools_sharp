using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;

namespace SourceSharp.Tests.MapTools.Bsp.Csg;

/// <summary>One entry of the BRUSHES lump: <c>dbrush_t</c>.</summary>
/// <param name="Contents">The brush's <c>CONTENTS_*</c> mask.</param>
/// <param name="FirstSide">Its first index in BRUSHSIDES.</param>
/// <param name="SideCount">How many sides it has, bevels included.</param>
internal readonly record struct EmittedBrush(int Contents, int FirstSide, int SideCount);

/// <summary>One entry of the BRUSHSIDES lump: <c>dbrushside_t</c>.</summary>
/// <param name="PlaneNumber">The side's plane index.</param>
/// <param name="TexInfo">The side's texinfo index.</param>
/// <param name="Bevel">Whether it is a bevel.</param>
internal readonly record struct EmittedBrushSide(int PlaneNumber, int TexInfo, bool Bevel);

/// <summary>
/// <c>EmitBrushes</c>, — the minimum of it,
/// here rather than in the library.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 3e owns the real one, and this is deliberately not it.</b> What
/// Phase 3b needs is a way to ask "do the BRUSHES and BRUSHSIDES lumps this
/// map would produce match stock's, after my stage has run" — and the only
/// part of those lumps that this lane decides is what
/// <see cref="SourceSharp.MapTools.Bsp.Csg.AreaportalWaterFixup"/> writes back
/// onto the map brushes. So the emitter lives in the test tree, where it
/// cannot be mistaken for the shipping one, and Phase 3e replacing it is a
/// deletion rather than a merge.
/// </para>
/// <para>
/// Faithful in the three ways the comparison depends on: the axial bevel pass
/// appends up to six planes per brush in <c>x</c>-then-<c>s</c> order,
/// each added side inherits the texinfo of
/// the side BEFORE it in the lump rather than the brush's own,
/// and a texinfo of -1 becomes
/// <c>g_ClipTexinfo</c>.
/// </para>
/// <para>
/// It appends to the plane table, through <c>FindFloatPlane</c>, exactly as
/// stock does — which is why it must run at the END of a compile and not
/// before the CSG.
/// </para>
/// </remarks>
internal static class StockBrushLumps
{
    /// <summary>Emits both lumps from a loaded map.</summary>
    /// <param name="map">The map, after every stage that writes to its brushes.</param>
    /// <returns>The two lumps.</returns>
    public static (List<EmittedBrush> Brushes, List<EmittedBrushSide> Sides) Emit(MapFile map)
    {
        ArgumentNullException.ThrowIfNull(map);

        List<EmittedBrush> brushes = [];
        List<EmittedBrushSide> sides = [];

        for (int bnum = 0; bnum < map.BrushCount; bnum++)
        {
            MapBrush b = map.Brushes[bnum];

            int firstSide = sides.Count;
            int sideCount = b.SideCount;

            for (int j = 0; j < b.SideCount; j++)
            {
                MapBrushSide s = map.BrushSides[b.FirstSide + j];
                int texInfo = s.TexInfo == -1 ? map.ClipTexInfo : s.TexInfo;
                sides.Add(new EmittedBrushSide(s.PlaneNumber, texInfo, s.Bevel));
            }

            for (int x = 0; x < 3; x++)
            {
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    Vec3 normal = x switch
                    {
                        0 => new Vec3(sign, 0f, 0f),
                        1 => new Vec3(0f, sign, 0f),
                        _ => new Vec3(0f, 0f, sign),
                    };

                    float dist = sign == -1 ? -b.Mins[x] : b.Maxs[x];
                    int planeNumber = map.Planes.Find(normal, dist);

                    int i = 0;
                    for (; i < b.SideCount; i++)
                    {
                        if (map.BrushSides[b.FirstSide + i].PlaneNumber == planeNumber)
                        {
                            break;
                        }
                    }

                    if (i != b.SideCount)
                    {
                        continue;
                    }

                    // The texinfo of the PREVIOUS side in the lump, which for
                    // the first added bevel is the brush's last real side and
                    // for later ones is the bevel before it.
                    sides.Add(new EmittedBrushSide(planeNumber, sides[^1].TexInfo, false));
                    sideCount++;
                }
            }

            brushes.Add(new EmittedBrush(b.Contents, firstSide, sideCount));
        }

        return (brushes, sides);
    }

    /// <summary>The BRUSHES lump of a stock-compiled map.</summary>
    /// <param name="name">The catalogue entry.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The entries, in lump order.</returns>
    public static async Task<IReadOnlyList<EmittedBrush>> StockBrushesAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        BspData bsp = await LoadAsync(name, cancellationToken);

        List<EmittedBrush> result = [];
        foreach (DBrush brush in BspStructView.As<DBrush>(bsp[BspLump.Brushes]))
        {
            result.Add(new EmittedBrush(brush.Contents, brush.FirstSide, brush.NumSides));
        }

        return result;
    }

    /// <summary>The BRUSHSIDES lump of a stock-compiled map.</summary>
    /// <param name="name">The catalogue entry.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The entries, in lump order.</returns>
    public static async Task<IReadOnlyList<EmittedBrushSide>> StockBrushSidesAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        BspData bsp = await LoadAsync(name, cancellationToken);

        List<EmittedBrushSide> result = [];
        foreach (DBrushSide side in BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]))
        {
            result.Add(new EmittedBrushSide(side.PlaneNum, side.TexInfo, side.Bevel != 0));
        }

        return result;
    }

    private static async Task<BspData> LoadAsync(string name, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(StockLoad.BspPath(name));
        return await BspFile.LoadAsync(stream, cancellationToken);
    }
}
