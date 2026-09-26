//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Faces;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Materials;

using BrushBspTree = SourceSharp.MapTools.Bsp.Tree.BrushBspTree;
using TreeNode = SourceSharp.MapTools.Bsp.Tree.BspNode;
using TreeOperations = SourceSharp.MapTools.Bsp.Tree.TreeOperations;

namespace SourceSharp.MapTools.Bsp.Write;

/// <summary>
/// <c>EmitOccluderBrushes</c> and <c>AssignOccluderAreas</c>
/// LUMP_OCCLUSION.
/// </summary>
/// <remarks>
/// <para>
/// Every <c>func_occluder</c> brush in the map is CSG'd against every other
/// one in ONE private tree, faced and welded, and then each occluder entity
/// keeps the faces that came from its own sides. The welding is real: it runs
/// <c>FixTjuncs</c> on the occluder tree, which emits the polygons' vertices
/// into the map's one vertex lump BEFORE the world model is processed, so an
/// occluder shifts every later vertex index.
/// </para>
/// <para>
/// The entities then lose their brushes (<c>numbrushes = 0</c>,
///) so the model loop skips them.
/// </para>
/// </remarks>
internal sealed class OccluderEmitter
{
    private readonly BspWriteState _state;
    private readonly MapFile _map;
    private readonly BspBuildContext _build;
    private readonly VbspContext _compile;
    private readonly List<int> _occluderEntities = [];

    internal OccluderEmitter(BspWriteState state, MapFile map, BspBuildContext build, VbspContext compile)
    {
        _state = state;
        _map = map;
        _build = build;
        _compile = compile;
    }

    /// <summary><c>EmitOccluderBrushes</c>.</summary>
    internal void Emit()
    {
        _state.Occluders.Clear();
        _state.OccluderPolys.Clear();
        _state.OccluderVertexIndices.Clear();

        TreeNode? head = ClipOccluderBrushes();
        if (head is null)
        {
            return;
        }

        List<Face> faceList = [];
        GenerateOccluderFaceList(head, faceList);

        for (int entityNumber = 1; entityNumber < _map.Entities.Count; ++entityNumber)
        {
            MapEntity e = _map.Entities[entityNumber];
            if (!EntityStage.IsFuncOccluder(e))
            {
                continue;
            }

            // Output only those parts of the occluder tree which are a part of the brush
            int occluder = _state.Occluders.Count;
            DOccluderData data = default;
            data.FirstPoly = _state.OccluderPolys.Count;
            data.Mins = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            data.Maxs = new Vec3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
            data.Flags = 0;
            data.Area = -1;

            // NOTE: If you change the algorithm by which occluder numbers are
            // allocated, then you must also change FixupOnlyEntsOccluderEntities
            e.SetKeyValue("occludernumber", occluder.ToString(CultureInfo.InvariantCulture));
            _occluderEntities.Add(entityNumber);

            List<MapBrushSide> sideList = [];
            for (int i = e.FirstBrush; i < e.FirstBrush + e.BrushCount; ++i)
            {
                MapBrush mb = _map.Brushes[i];
                for (int j = 0; j < mb.SideCount; ++j)
                {
                    sideList.Add(_map.BrushSides[mb.FirstSide + j]);
                }
            }

            for (int i = faceList.Count; --i >= 0;)
            {
                // Skip nodraw surfaces, but not triggers that have been marked as nodraw
                Face f = faceList[i];
                int flags = _compile.TexInfos[f.TexInfo].Flags;
                if ((flags & (int)SurfaceFlags.NoDraw) != 0 && (flags & (int)SurfaceFlags.Trigger) == 0)
                {
                    continue;
                }

                // Only emit faces that appear in the side list of the occluder
                for (int j = sideList.Count; --j >= 0;)
                {
                    if (!ReferenceEquals(sideList[j], f.OriginalFace))
                    {
                        continue;
                    }

                    if (f.NumPoints < 3)
                    {
                        continue;
                    }

                    DOccluderPolyData poly = default;
                    poly.PlaneNum = f.PlaneNumber;
                    poly.VertexCount = f.NumPoints;
                    poly.FirstVertexIndex = _state.OccluderVertexIndices.Count;
                    _state.OccluderPolys.Add(poly);

                    for (int k = 0; k < f.NumPoints; ++k)
                    {
                        _state.OccluderVertexIndices.Add(f.VertexNumbers[k]);

                        Vec3 p = _state.Vertices[f.VertexNumbers[k]];
                        data.Mins = Min(data.Mins, p);
                        data.Maxs = Max(data.Maxs, p);
                    }

                    break;
                }
            }

            data.PolyCount = _state.OccluderPolys.Count - data.FirstPoly;
            _state.Occluders.Add(data);

            // Mark this brush as not having brush geometry so it won't be
            // re-emitted with a brush model
            e.BrushCount = 0;
        }
    }

    /// <summary><c>AssignOccluderAreas</c>: after the world model.</summary>
    /// <param name="worldHead">The world tree.</param>
    /// <param name="diagnostics">Where a straddling occluder is reported.</param>
    internal void AssignAreas(TreeNode worldHead, IList<CompileDiagnostic> diagnostics)
    {
        for (int i = 0; i < _state.Occluders.Count; ++i)
        {
            AssignAreaToOccluder(i, worldHead, crossAreaPortals: false, diagnostics);

            // This can only have happened if the only valid portal out leads into an areaportal
            if (_state.Occluders[i].Area <= 0)
            {
                AssignAreaToOccluder(i, worldHead, crossAreaPortals: true, diagnostics);
            }
        }
    }

    /// <summary><c>ClipOccluderBrushes</c>.</summary>
    private TreeNode? ClipOccluderBrushes()
    {
        // Create a list of all occluder brushes in the level
        List<MapBrush> brushes = [];
        foreach (MapEntity e in _map.Entities)
        {
            if (!EntityStage.IsFuncOccluder(e))
            {
                continue;
            }

            for (int i = e.FirstBrush; i < e.FirstBrush + e.BrushCount; ++i)
            {
                brushes.Add(_map.Brushes[i]);
            }
        }

        if (brushes.Count == 0)
        {
            return null;
        }

        Vec3 mins = new(WriteConstants.MinCoordInteger, WriteConstants.MinCoordInteger, WriteConstants.MinCoordInteger);
        Vec3 maxs = new(WriteConstants.MaxCoordInteger, WriteConstants.MaxCoordInteger, WriteConstants.MaxCoordInteger);

        BspBrush? list = BrushCsg.MakeBspBrushList(_build, brushes, mins, maxs);
        if (!_compile.Options.NoCsg)
        {
            list = BrushCsg.ChopBrushes(_build, list);
        }

        Tree.BspTree tree = BrushBspTree.BrushBsp(_build, list, mins, maxs);

        TreePortals portals = new(_compile.Windings, _map.Planes);
        portals.MakeTreePortals(tree);

        VisibleSides sides = new(_compile.Windings, _map.Planes, _map.BrushSides)
        {
            Diagnostics = _compile.Diagnostics,
        };
        sides.MarkVisibleSides(tree, brushes);

        // MakeFaces runs with entity_num left at num_entities by the loop
        // above, so the brush-model plane rule of FaceFromPortal applies.
        _state.Faces.EntityNumber = _map.Entities.Count;
        new FaceBuilder(_state.Faces).MakeFaces(tree.HeadNode!);

        // NOTE: This will output the occluder face vertices + planes
        new TJunctionFixer(_state.Faces).FixTjuncs(tree.HeadNode!, null);

        return tree.HeadNode;
    }

    /// <summary><c>GenerateOccluderFaceList</c>.</summary>
    private void GenerateOccluderFaceList(TreeNode node, List<Face> faces)
    {
        if (node.IsLeaf)
        {
            return;
        }

        for (Face? f = _state.Faces.Lists.FacesOf(node); f is not null; f = f.Next)
        {
            faces.Add(f);
        }

        GenerateOccluderFaceList(node.Children[0]!, faces);
        GenerateOccluderFaceList(node.Children[1]!, faces);
    }

    /// <summary><c>AssignAreaToOccluder</c>.</summary>
    private void AssignAreaToOccluder(
        int occluder, TreeNode head, bool crossAreaPortals, IList<CompileDiagnostic> diagnostics)
    {
        DOccluderData data = _state.Occluders[occluder];
        int entityNumber = _occluderEntities[occluder];

        for (int j = 0; j < data.PolyCount; ++j)
        {
            DOccluderPolyData poly = _state.OccluderPolys[data.FirstPoly + j];
            for (int k = 0; k < poly.VertexCount; ++k)
            {
                int vertex = _state.OccluderVertexIndices[poly.FirstVertexIndex + k];
                TreeNode node = TreeOperations.NodeForPoint(_build, head, _state.Vertices[vertex]);

                SetOccluderArea(occluder, node.Area, entityNumber, diagnostics);

                for (Portal? p = node.Portals; p is not null;)
                {
                    int other = ReferenceEquals(p.FrontNode, node) ? 1 : 0;
                    Portal? next = p.NextAt(1 - other);

                    if (p.OnNode is null)
                    {
                        p = next;
                        continue; // edge of world
                    }

                    // Don't cross over area portals for the area check
                    IBspNode? otherNode = p.NodeAt(other);
                    if (!crossAreaPortals && (otherNode!.Contents & (int)BrushContents.AreaPortal) != 0)
                    {
                        p = next;
                        continue;
                    }

                    int adjacentArea = otherNode?.Area ?? 0;
                    SetOccluderArea(occluder, adjacentArea, entityNumber, diagnostics);
                    p = next;
                }
            }
        }
    }

    /// <summary><c>SetOccluderArea</c>.</summary>
    private void SetOccluderArea(int occluder, int area, int entityNumber, IList<CompileDiagnostic> diagnostics)
    {
        DOccluderData data = _state.Occluders[occluder];
        if (data.Area <= 0)
        {
            data.Area = area;
            _state.Occluders[occluder] = data;
        }
        else if (area != 0 && data.Area != area)
        {
            string name = _map.Entities[entityNumber].ValueForKey("targetname");
            diagnostics.Add(new CompileDiagnostic(
                WriteCodes.OccluderStraddlesAreas,
                DiagnosticSeverity.Warning,
                $"Occluder \"{name}\" straddles multiple areas. This is invalid!",
                new MapLocation(EntityId: entityNumber)));
        }
    }

    private static Vec3 Min(Vec3 a, Vec3 b) => new(
        a.X < b.X ? a.X : b.X, a.Y < b.Y ? a.Y : b.Y, a.Z < b.Z ? a.Z : b.Z);

    private static Vec3 Max(Vec3 a, Vec3 b) => new(
        a.X > b.X ? a.X : b.X, a.Y > b.Y ? a.Y : b.Y, a.Z > b.Z ? a.Z : b.Z);
}
