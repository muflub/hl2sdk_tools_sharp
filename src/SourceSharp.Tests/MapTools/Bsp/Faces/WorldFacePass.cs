using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Faces;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Options;

using BlockGrid = SourceSharp.MapTools.Bsp.Tree.BlockGrid;
using BrushBspTree = SourceSharp.MapTools.Bsp.Tree.BrushBspTree;
using BuiltTree = SourceSharp.MapTools.Bsp.Tree.BspTree;
using StockLoad = SourceSharp.Tests.MapTools.Bsp.StockLoad;

namespace SourceSharp.Tests.MapTools.Bsp.Faces;

/// <summary>
/// <c>ProcessWorldModel</c> (<c>src/utils/vbsp/vbsp.cpp:242</c>) as far as
/// <c>FixTjuncs</c>, driven over a real <c>.vmf</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only honest gate for the face stage and it needs every lane
/// before it.</b> The numbers <c>MakeFaces</c> and <c>FixTjuncs</c> print are
/// functions of the whole tree: which portals exist, which leaves they join,
/// which brush side each portal chose. Running the face stage over a hand-made
/// tree would test the code and say nothing about parity; running it over the
/// tree the CSG and the portaliser actually build, from the same <c>.vmf</c>
/// stock compiled, is a comparison of like with like.
/// </para>
/// <para>
/// The optimize loop is reproduced because it is not an optimisation in the
/// "same answer, faster" sense: the second pass splits with different planes,
/// because <c>MarkVisibleSides</c> has by then written <c>visible</c> onto the
/// map's brush sides. A single-pass harness builds a DIFFERENT tree and
/// therefore a different number of faces.
/// </para>
/// <para>
/// What is left out, and why it is safe to leave out: <c>WriteGLView</c>
/// (debug output), <c>AssignOccluderAreas</c> and <c>Compute3DSkyboxAreas</c>
/// (they write area numbers that nothing in this stage reads), and
/// <c>PruneNodes</c> (it runs after <c>FixTjuncs</c>).
/// </para>
/// </remarks>
internal sealed class WorldFacePass
{
    private WorldFacePass(
        VbspContext compile,
        MapFile map,
        FaceBuildContext faces,
        IBspTree tree,
        bool sealedMap)
    {
        Compile = compile;
        Map = map;
        Faces = faces;
        Tree = tree;
        Sealed = sealedMap;
    }

    internal VbspContext Compile { get; }

    internal MapFile Map { get; }

    internal FaceBuildContext Faces { get; }

    internal IBspTree Tree { get; }

    internal bool Sealed { get; }

    internal FaceCounters Counters => Faces.Counters;

    /// <summary>Runs the world model's face pass over a catalogue entry.</summary>
    /// <param name="name">The entry name.</param>
    /// <param name="compliance">Which compliance to compile under.</param>
    /// <param name="cancellationToken">Cancels the content reads.</param>
    /// <returns>The finished pass.</returns>
    internal static async Task<WorldFacePass> ForAsync(
        string name,
        ComplianceOptions compliance,
        CancellationToken cancellationToken = default)
    {
        // The compliance reaches the map LOAD as well as the face stage: the
        // plane table the face stage indexes is built there, and
        // StockQuirk.EdgeBevelNormalise decides what is in it.
        (VbspContext compile, MapFile map, _) = await StockLoad
            .LoadAsync(name, compliance, cancellationToken)
            .ConfigureAwait(false);

        FaceMaterialFacts materials = await FaceMaterialFacts
            .PrepareAsync(compile, cancellationToken)
            .ConfigureAwait(false);

        MapEntity world = map.Entities[0];
        int brushStart = world.FirstBrush;
        int brushEnd = world.FirstBrush + world.BrushCount;

        BspBuildContext build = new(compile, map)
        {
            BrushStart = brushStart,
            BrushEnd = brushEnd,
        };

        BspBlockGrid grid = BlockGrid.Clamp(BspBlockGrid.Full, map.Mins, map.Maxs);

        BuiltTree? built = null;
        IBspTree? tree = null;
        bool sealedMap = false;

        // ProcessWorldModel's for (optimize = 0; optimize <= 1; optimize++)
        for (int optimize = 0; optimize <= 1; optimize++)
        {
            built = BlockGrid.BuildWorldPass(build, grid, map.Mins, map.Maxs, out _);

            tree = built;

            TreePortals portals = new(compile.Windings, map.Planes);
            portals.MakeTreePortals(tree);

            FloodResult flood = EntityFlood.FloodEntities(tree, map.Planes, map.Entities);
            sealedMap = flood.Sealed;

            if (flood.Sealed)
            {
                EntityFlood.FillOutside(tree.HeadNode);
            }

            VisibleSides sides = new(compile.Windings, map.Planes, map.BrushSides)
            {
                Diagnostics = compile.Diagnostics,
            };

            sides.MarkVisibleSides(tree, map.Brushes, brushStart, brushEnd, DetailScreen.NoDetail);

            if (!flood.Sealed)
            {
                break;
            }
        }

        AreaFlood areas = new(map.Entities);
        areas.FloodAreas(tree!, compile.Windings);

        BrushBspTree.RemoveAreaPortalBrushes(built!.HeadNode!);

        FaceBuildContext faces = new(compile.Windings, map.Planes, compile.TexInfos, compile.Options)
        {
            Compliance = compliance,
            Diagnostics = compile.Diagnostics,
            Materials = materials,
            EntityNumber = 0,
        };

        FaceBuilder builder = new(faces);
        builder.MakeFaces(tree!.HeadNode);

        DetailFaces details = new(faces, build, map.BrushSides);
        Face? leafFaces = details.MergeDetailTree(
            tree.HeadNode, brushStart, brushEnd, map.Mins, map.Maxs);

        TJunctionFixer fixer = new(faces);
        fixer.FixTjuncs(tree.HeadNode, leafFaces);

        return new WorldFacePass(compile, map, faces, tree, sealedMap);
    }
}
