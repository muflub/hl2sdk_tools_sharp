using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// vrad's rebuild of displacements from a compiled BSP:
/// <c>CVRadDispMgr::DispBuilderInit</c> and <c>UnserializeDisps</c>,
/// <c>utils/vrad/vraddisps.cpp:348-468</c>.
/// </summary>
public sealed class DispLightingLoaderTests
{
    private static readonly Vec3[] Floor = DispFixtures.UnitFloor();

    private static readonly Vec3[] Right = DispFixtures.FloorQuad(new Vec3(256, 0, 0), 256, 256);

    /// <summary>The surface handle is the face index: <c>vraddisps.cpp:359</c>.</summary>
    [Fact]
    public void TheSurfaceHandleIsTheFaceIndex()
    {
        CoreDispInfo core = Rebuild(out _, faceIndex: 42);

        Assert.Equal(42, core.Surface.Handle);
    }

    /// <summary>
    /// Every corner normal is the quad's plane normal before the rotation:
    /// <c>vraddisps.cpp:381-386</c>.
    /// </summary>
    [Fact]
    public void TheCornerNormalsAreThePlaneNormal()
    {
        CoreDispInfo core = Rebuild(out _);

        Assert.All(core.Surface.Normals.ToArray(), n => Assert.Equal(0, n.X));
    }

    /// <summary>
    /// The neighbour tables are copied from the lump, not re-found:
    /// <c>SetNeighborData</c>, <c>vraddisps.cpp:407</c>.
    /// </summary>
    [Fact]
    public void TheNeighbourTablesAreCopiedFromTheLump()
    {
        DispInfo info = default;
        for (int e = 0; e < 4; e++)
        {
            info.EdgeNeighbors[e].SubNeighbors[0].Neighbor = DispSubNeighbor.NoNeighbor;
            info.EdgeNeighbors[e].SubNeighbors[1].Neighbor = DispSubNeighbor.NoNeighbor;
        }

        info.EdgeNeighbors[1].SubNeighbors[0].Neighbor = 9;
        info.Power = 2;
        CoreDispInfo core = new(2);

        DispLightingLoader.BuilderInit(core, info, 0, Floor, new Vec3(DispFixtures.LuxelsPerUnit, 0, 0),
            new Vec3(0, -DispFixtures.LuxelsPerUnit, 0), new DispVert[25], new DispTri[32]);

        Assert.Equal(9, core.EdgeNeighbor(1).SubNeighbors[0].Neighbor);
    }

    /// <summary>
    /// Load rebuilds each displacement at its LUMP_DISPINFO index from an
    /// in-memory BSP, with its neighbours' normals sewn
    /// (<c>vraddisps.cpp:427-455</c>).
    /// </summary>
    [Fact]
    public void LoadRebuildsEveryDisplacementAndSewsTheSharedEdge()
    {
        BspData bsp = TwoDisplacementBsp(out _);

        CoreDispInfo[] cores = DispLightingLoader.Load(bsp, ComplianceOptions.Correct);

        Assert.Equal(2, cores.Length);
        Assert.Equal(
            cores[0].Normal(DispFixtures.Index(cores[0], 4, 2)),
            cores[1].Normal(DispFixtures.Index(cores[1], 0, 2)));
    }

    /// <summary>Load reproduces vbsp's displaced vertices from the lumps.</summary>
    [Fact]
    public void LoadReproducesTheDisplacedVertices()
    {
        BspData bsp = TwoDisplacementBsp(out IReadOnlyList<DisplacementResult> results);

        CoreDispInfo[] cores = DispLightingLoader.Load(bsp, ComplianceOptions.Correct);

        for (int i = 0; i < cores[1].Size; i++)
        {
            Assert.True(DispFixtures.BitEqual(results[1].Core.Vert(i), cores[1].Vert(i)), $"vertex {i}");
        }
    }

    private static CoreDispInfo Rebuild(out DisplacementResult result, int faceIndex = 0)
    {
        (IReadOnlyList<DisplacementResult> results, DisplacementLumps lumps) =
            DispFixtures.Build([(DispFixtures.Heightfield(2, Floor[0], (x, y) => x + y), Floor)]);
        result = results[0];
        CoreDispInfo core = new(2);
        core.SetListBase([core]);
        DispLightingLoader.BuilderInit(core, result.Info, faceIndex, Floor,
            new Vec3(DispFixtures.LuxelsPerUnit, 0, 0), new Vec3(0, -DispFixtures.LuxelsPerUnit, 0),
            lumps.Verts.ToArray(), lumps.Tris.ToArray());
        core.Create();
        return core;
    }

    /// <summary>
    /// A BSP holding just what <see cref="DispLightingLoader.Load"/> reads: two
    /// sloped displacements side by side, each on a four-edge face.
    /// </summary>
    private static BspData TwoDisplacementBsp(out IReadOnlyList<DisplacementResult> results)
    {
        (results, DisplacementLumps lumps) = DispFixtures.Build(
        [
            (DispFixtures.Heightfield(2, Floor[0], (x, _) => x * 16), Floor),
            (DispFixtures.Heightfield(2, Right[0], (x, _) => 64 - (x * 8)), Right),
        ]);

        List<Vec3> vertexes = [];
        List<DEdge> edges = [default];
        List<int> surfEdges = [];
        List<DFace> faces = [];
        Vec3[][] windings = [Floor, Right];

        for (int f = 0; f < 2; f++)
        {
            DFace face = default;
            face.FirstEdge = surfEdges.Count;
            face.NumEdges = 4;
            face.TexInfo = 0;
            face.DispInfo = (short)f;

            for (int k = 0; k < 4; k++)
            {
                vertexes.Add(windings[f][k]);
            }

            for (int k = 0; k < 4; k++)
            {
                DEdge e = default;
                e.V[0] = (ushort)((f * 4) + k);
                e.V[1] = (ushort)((f * 4) + ((k + 1) % 4));
                edges.Add(e);
                surfEdges.Add(edges.Count - 1);
            }

            faces.Add(face);
        }

        TexInfo tex = default;
        tex.LightmapVecsLuxelsPerWorldUnits[0] = DispFixtures.LuxelsPerUnit;
        tex.LightmapVecsLuxelsPerWorldUnits[5] = -DispFixtures.LuxelsPerUnit;

        DispInfo[] infos = [results[0].Info, results[1].Info];

        BspData bsp = new();
        bsp[BspLump.DispInfo] = BspStructView.ToLump<DispInfo>(infos, 0);
        bsp[BspLump.DispVerts] = BspStructView.ToLump<DispVert>(lumps.Verts.ToArray(), 0);
        bsp[BspLump.DispTris] = BspStructView.ToLump<DispTri>(lumps.Tris.ToArray(), 0);
        bsp[BspLump.Faces] = BspStructView.ToLump<DFace>(faces.ToArray(), 0);
        bsp[BspLump.Vertexes] = BspStructView.ToLump<Vec3>(vertexes.ToArray(), 0);
        bsp[BspLump.Edges] = BspStructView.ToLump<DEdge>(edges.ToArray(), 0);
        bsp[BspLump.SurfEdges] = BspStructView.ToLump<int>(surfEdges.ToArray(), 0);
        bsp[BspLump.TexInfo] = BspStructView.ToLump<TexInfo>([tex], 0);
        return bsp;
    }
}
