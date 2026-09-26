//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Bsp.Write;

/// <summary>
/// <c>EmitAreaPortals</c>: LUMP_AREAS,
/// LUMP_AREAPORTALS and LUMP_CLIPPORTALVERTS, for the world model only.
/// </summary>
/// <remarks>
/// The geometry half, <c>EmitClipPortalGeometry</c>, is Phase 3c's
/// <see cref="AreaPortalGeometry.ClipPortalGeometry"/>; this is the record
/// half and the numbering, which 3c left to the write stage.
/// </remarks>
internal static class AreaPortalEmitter
{
    /// <summary>Emits the area and area-portal lumps.</summary>
    /// <param name="state">Where the lumps go.</param>
    /// <param name="headNode">The world tree.</param>
    /// <param name="map">The map, whose entities carry the portal numbers and areas.</param>
    /// <param name="areas">The finished area flood.</param>
    /// <param name="windings">The arena holding the portal windings.</param>
    /// <param name="compliance">Decides <see cref="Options.StockQuirk.NodeAreaWrittenBeforeSet"/>.</param>
    /// <param name="diagnostics">Where a suspicious hull is reported.</param>
    internal static void Emit(
        BspWriteState state,
        Tree.BspNode headNode,
        MapFile map,
        AreaFlood areas,
        WindingArena windings,
        Options.ComplianceOptions compliance,
        IList<CompileDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(headNode);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(areas);

        int areaCount = areas.AreaCount;
        if (areaCount > WriteLimits.MaxMapAreas)
        {
            throw new MapCompileException(WriteCodes.LimitExceeded,
                $"Map is split into too many unique areas (max = {WriteLimits.MaxMapAreas})\n"
                + "Probably too many areaportals");
        }

        // numareas = c_areas+1; area 0 is the zeroed error area.
        state.Areas.Clear();
        for (int i = 0; i <= areaCount; i++)
        {
            state.Areas.Add(default);
        }

        // numareaportals = 1: leave 0 as an error
        state.AreaPortals.Clear();
        state.AreaPortals.Add(default);

        // Reset the clip portal vert info.
        state.ClipPortalVerts.Clear();

        // Built on the first areaportal: the tree does not change while they
        // are emitted, so one walk answers them all (AreaPortalIndex).
        AreaPortalIndex? index = null;

        for (int srcArea = 1; srcArea <= areaCount; srcArea++)
        {
            DArea area = default;
            area.FirstAreaPortal = state.AreaPortals.Count;

            foreach (MapEntity e in map.Entities)
            {
                if (e.AreaPortalNumber == 0)
                {
                    continue;
                }

                if (e.PortalAreas[0] != srcArea && e.PortalAreas[1] != srcArea)
                {
                    continue;
                }

                int side = e.PortalAreas[0] == srcArea ? 1 : 0;

                // We're only interested in the portal that divides the two
                // areas. One of the portals that leads into the
                // CONTENTS_AREAPORTAL just bounds the same two areas but the
                // other bounds two different ones.
                areas.Links.TryGetValue(e, out AreaPortalLink link);
                Portal? leading = link.Into0;
                if (leading is not null && leading.FrontNode!.Area == leading.BackNode!.Area)
                {
                    leading = link.Into1;
                }

                if (leading is null)
                {
                    continue;
                }

                DAreaPortal dp = default;
                dp.PortalKey = (ushort)e.AreaPortalNumber;
                dp.OtherArea = (ushort)e.PortalAreas[side];

                int planeNumber = leading.OnNode!.PlaneNumber;
                if (leading.FrontNode!.Area == dp.OtherArea)
                {
                    // Use the flipped version of the plane.
                    planeNumber = (planeNumber & ~1) | (~planeNumber & 1);
                }

                dp.PlaneNum = planeNumber;

                // EmitClipPortalGeometry
                IReadOnlyList<Vec3> hull = AreaPortalGeometry.ClipPortalGeometry(
                    headNode, map.Planes, windings, leading, srcArea, dp.OtherArea, diagnostics,
                    index ??= new AreaPortalIndex(headNode));

                dp.FirstClipPortalVert = (ushort)state.ClipPortalVerts.Count;
                dp.ClipPortalVerts = (ushort)hull.Count;

                if (dp.FirstClipPortalVert + dp.ClipPortalVerts >= WriteLimits.MaxMapPortalVerts)
                {
                    Vec3 p = windings.Points(leading.Winding)[0];
                    throw new MapCompileException(WriteCodes.LimitExceeded,
                        $"MAX_MAP_PORTALVERTS (probably a broken areaportal near {p.X:F1} {p.Y:F1} {p.Z:F1} ");
                }

                state.ClipPortalVerts.AddRange(hull);
                state.AreaPortals.Add(dp);
            }

            area.NumAreaPortals = state.AreaPortals.Count - area.FirstAreaPortal;
            state.Areas[srcArea] = area;
        }

        // SetNodeAreaIndices_R: it lands in the tree, AFTER the node lump was
        // written (StockQuirk.NodeAreaWrittenBeforeSet). Correct copies it in.
        AreaFlood.SetNodeAreaIndices(headNode);

        if (!compliance.Emulates(Options.StockQuirk.NodeAreaWrittenBeforeSet))
        {
            PatchNodeAreas(state, headNode);
        }
    }

    private static void PatchNodeAreas(BspWriteState state, Tree.BspNode node)
    {
        if (node.IsLeaf)
        {
            return;
        }

        DNode n = state.Nodes[node.DiskId];
        n.Area = (short)node.Area;
        state.Nodes[node.DiskId] = n;

        PatchNodeAreas(state, node.Children[0]!);
        PatchNodeAreas(state, node.Children[1]!);
    }
}
