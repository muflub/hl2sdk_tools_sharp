using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Diagnostics;

namespace SourceSharp.MapTools.Bsp.Write;

/// <summary>
/// <c>EmitBrushes</c> and <c>EmitPlanes</c> (
///): the original map brushes, un-CSG'd, and every plane the map
/// ever created.
/// </summary>
internal static class BrushLumps
{
    /// <summary>
    /// <c>EmitBrushes</c>: every map brush, its original sides, then the axial
    /// planes of its bounding box that it does not already have.
    /// </summary>
    /// <param name="map">The map. Its plane table can grow here.</param>
    /// <param name="state">Where the lumps go.</param>
    /// <remarks>
    /// <para>
    /// <b>This runs before <see cref="EmitPlanes"/> because it can add
    /// planes.</b> The six box planes are looked up with
    /// <c>FindFloatPlane</c>, which creates any that do not exist, and the
    /// plane lump is written afterwards from the whole table.
    /// </para>
    /// <para>
    /// An added box side takes the texinfo of the side written just before it
    /// (<c>dbrushsides[numbrushsides-1].texinfo</c>), which for the second and
    /// later additions is the previous ADDED side; its bevel and dispinfo stay
    /// at the zero of a cleared global.
    /// </para>
    /// </remarks>
    internal static void EmitBrushes(MapFile map, BspWriteState state)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(state);

        state.BrushSides.Clear();
        state.Brushes.Clear();

        for (int bnum = 0; bnum < map.Brushes.Count; bnum++)
        {
            MapBrush b = map.Brushes[bnum];

            DBrush db = default;
            db.Contents = b.Contents;
            db.FirstSide = state.BrushSides.Count;
            db.NumSides = b.SideCount;

            for (int j = 0; j < b.SideCount; j++)
            {
                if (state.BrushSides.Count == WriteLimits.MaxMapBrushSides)
                {
                    throw new MapCompileException(WriteCodes.LimitExceeded, "MAX_MAP_BRUSHSIDES");
                }

                MapBrushSide side = map.BrushSides[b.FirstSide + j];
                DBrushSide cp = default;
                cp.PlaneNum = (ushort)side.PlaneNumber;
                cp.TexInfo = (short)(side.TexInfo == -1 ? map.ClipTexInfo : side.TexInfo);
                cp.Bevel = (short)(side.Bevel ? 1 : 0);
                state.BrushSides.Add(cp);
            }

            // add any axis planes not contained in the brush to bevel off corners
            for (int x = 0; x < 3; x++)
            {
                for (int s = -1; s <= 1; s += 2)
                {
                    // add the plane
                    Vec3 normal = x switch
                    {
                        0 => new Vec3(s, 0f, 0f),
                        1 => new Vec3(0f, s, 0f),
                        _ => new Vec3(0f, 0f, s),
                    };

                    float dist = s == -1 ? -b.Mins[x] : b.Maxs[x];
                    int planeNumber = map.Planes.Find(normal, dist);

                    int i;
                    for (i = 0; i < b.SideCount; i++)
                    {
                        if (map.BrushSides[b.FirstSide + i].PlaneNumber == planeNumber)
                        {
                            break;
                        }
                    }

                    if (i == b.SideCount)
                    {
                        if (state.BrushSides.Count >= WriteLimits.MaxMapBrushSides)
                        {
                            throw new MapCompileException(WriteCodes.LimitExceeded, "MAX_MAP_BRUSHSIDES");
                        }

                        DBrushSide added = default;
                        added.PlaneNum = (ushort)planeNumber;
                        added.TexInfo = state.BrushSides[^1].TexInfo;
                        state.BrushSides.Add(added);
                        db.NumSides++;
                    }
                }
            }

            state.Brushes.Add(db);
        }
    }

    /// <summary>
    /// <c>EmitPlanes</c>: "There is no oportunity to discard planes, because
    /// all of the original brushes will be saved in the map." The lump is the
    /// table, index for index, with each plane's stored type.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="state">Where the lump goes.</param>
    internal static void EmitPlanes(MapFile map, BspWriteState state)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(state);

        state.Planes.Clear();
        for (int i = 0; i < map.Planes.Count; i++)
        {
            Plane mp = map.Planes[i];
            state.Planes.Add(new DPlane
            {
                Normal = mp.Normal,
                Dist = mp.Dist,
                Type = (int)map.Planes.TypeOf(i),
            });
        }
    }
}
