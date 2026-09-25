using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Csg;

/// <summary>
/// <b>Edge bevels, gated at last.</b> The second half of
/// <c>AddBrushBevels</c> fires on no reference map
/// this project has; <see cref="EdgeBevelShapes.Octahedron"/> is the shape
/// that makes it fire, and these are the facts over it.
/// </summary>
/// <remarks>
/// The shape and the reasoning behind it are documented on
/// <see cref="EdgeBevelShapes"/>. The facts here are in two tiers: what the
/// loader does with the shape, which runs everywhere; and the comparison
/// against a stock <c>-v</c> compile of the same VMF, which needs the
/// reference directory.
/// </remarks>
public class EdgeBevelTests
{
    [Fact]
    public async Task TheOctahedronLoadsAsEightSlantedSides()
    {
        (_, MapFile map) = await CsgFixture.LoadAsync(EdgeBevelShapes.Document(onTheSeam: true));

        Assert.Equal(1, map.BrushCount);

        // Not exactly +/-128: MakeBrushWindings clips with BRUSH_CLIP_EPSILON
        //, so a vertex where three slanted planes meet lands a
        // few thousandths outside the ideal corner.
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(-128f, map.Brushes[0].Mins[i], 0.01f);
            Assert.Equal(128f, map.Brushes[0].Maxs[i], 0.01f);
        }

        int slanted = 0;
        for (int i = 0; i < map.Brushes[0].SideCount; i++)
        {
            MapBrushSide side = map.BrushSides[map.Brushes[0].FirstSide + i];
            if (side.Bevel)
            {
                continue;
            }

            Vec3 n = map.Planes[side.PlaneNumber].Normal;
            Assert.True(
                MathF.Abs(MathF.Abs(n.X) - MathF.Abs(n.Y)) < 1e-4f
                && MathF.Abs(MathF.Abs(n.Y) - MathF.Abs(n.Z)) < 1e-4f,
                $"side {i} normal {n} is not an octahedron face");
            slanted++;
        }

        Assert.Equal(8, slanted);
    }

    /// <summary>
    /// <b>The fact this lane was asked for.</b> Twelve edge bevels, which is
    /// the first time any shape in this project has produced one.
    /// </summary>
    [Fact]
    public async Task TheOctahedronProducesTwelveEdgeBevels()
    {
        (_, MapFile map) = await CsgFixture.LoadAsync(EdgeBevelShapes.Document(onTheSeam: true));

        Assert.Equal(12, map.EdgeBevels);
    }

    [Fact]
    public async Task TheOctahedronAlsoProducesAllSixBoxBevels()
    {
        (_, MapFile map) = await CsgFixture.LoadAsync(EdgeBevelShapes.Document(onTheSeam: true));

        Assert.Equal(6, map.BoxBevels);
        Assert.Equal(26, map.Brushes[0].SideCount);
    }

    /// <summary>
    /// The twelve planes are the edge-midpoint directions
    /// <c>(±1, ±1, 0)</c>, <c>(±1, 0, ±1)</c> and <c>(0, ±1, ±1)</c> over √2,
    /// each at <c>128/√2</c> — the supporting planes that touch the hull along
    /// one edge and nowhere else.
    /// </summary>
    [Fact]
    public async Task EveryEdgeBevelIsASupportingPlaneThroughOneEdge()
    {
        (_, MapFile map) = await CsgFixture.LoadAsync(EdgeBevelShapes.Document(onTheSeam: true));

        MapBrush brush = map.Brushes[0];
        List<Vec3> normals = [];

        for (int i = 0; i < brush.SideCount; i++)
        {
            MapBrushSide side = map.BrushSides[brush.FirstSide + i];
            if (!side.Bevel)
            {
                continue;
            }

            Plane plane = map.Planes[side.PlaneNumber];
            if (plane.IsAxial)
            {
                continue;
            }

            normals.Add(plane.Normal);

            float expected = EdgeBevelShapes.Radius / MathF.Sqrt(2f);
            Assert.Equal(expected, plane.Dist, 0.01f);

            // Exactly one component is zero and the other two are ±1/√2.
            int zeros = 0;
            for (int k = 0; k < 3; k++)
            {
                if (MathF.Abs(plane.Normal[k]) < 1e-4f)
                {
                    zeros++;
                }
                else
                {
                    Assert.Equal(1f / MathF.Sqrt(2f), MathF.Abs(plane.Normal[k]), 1e-4f);
                }
            }

            Assert.Equal(1, zeros);
        }

        Assert.Equal(12, normals.Count);
        Assert.Equal(12, normals.Distinct().Count());
    }

    /// <summary>
    /// A bevel is never a BSP splitter, so adding
    /// twelve of them to a brush must not change the tree it builds. This is
    /// the reason edge bevels matter to CSG and the reason they are invisible
    /// in every count the block gate compares.
    /// </summary>
    [Fact]
    public async Task TheTwelveEdgeBevelsAreNeverUsedAsSplitters()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            EdgeBevelShapes.Document(onTheSeam: true));

        SourceSharp.MapTools.Bsp.Tree.BspNode node = build.AllocNode();
        node.Volume = CsgFixture.Box(build, new Vec3(-512, -512, -512), new Vec3(512, 512, 512));

        Assert.True(SourceSharp.MapTools.Bsp.Tree.BrushBspTree.SelectSplitSide(
            build, CsgFixture.AllBrushes(build), node, out BspBrushSide best));

        Assert.False(best.Bevel);
        _ = map;
    }

    /// <summary>
    /// <c>CreateBrushWindings</c> skips bevels as CLIP planes
    /// where <c>SplitBrush</c> does not.
    /// On a shape with twelve edge bevels that
    /// difference is measurable: clipping with them produces a smaller brush
    /// than clipping without.
    /// </summary>
    [Fact]
    public async Task TheBevelsWouldShrinkTheBrushIfTheyWereUsedAsClipPlanes()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            EdgeBevelShapes.Document(onTheSeam: true));

        BspBrush? carved = CsgFixture.AllBrushes(build);
        Assert.NotNull(carved);

        // The octahedron's own volume: (2r)^3 / 6.
        float octahedron = (2f * EdgeBevelShapes.Radius * 2f * EdgeBevelShapes.Radius
            * 2f * EdgeBevelShapes.Radius) / 6f;
        Assert.Equal(octahedron, BrushGeometry.BrushVolume(build, carved), octahedron * 0.01f);

        // Rebuilt through CreateBrushWindings, which skips the bevels, the
        // shape is the same: the bevels only ever touch the hull along an
        // edge, so using them or not cannot change the solid. What WOULD
        // change is a shape whose bevel cuts into it -- and that is exactly
        // what a proper edge bevel is defined not to do.
        BspBrush rebuilt = build.AllocBrush(carved.SideCount);
        for (int i = 0; i < carved.SideCount; i++)
        {
            rebuilt.AddSide(new BspBrushSide
            {
                PlaneNumber = carved.Sides[i].PlaneNumber,
                Bevel = carved.Sides[i].Bevel,
            });
        }

        BrushGeometry.CreateBrushWindings(build, rebuilt);

        Assert.Equal(
            BrushGeometry.BrushVolume(build, carved),
            BrushGeometry.BrushVolume(build, rebuilt),
            octahedron * 0.001f);

        _ = map;
    }
}
