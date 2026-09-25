using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// <c>TraceLeafBrushes</c> on the committed fixture,
/// against the first leaf that holds an opaque axis-aligned box brush.
/// </summary>
public sealed class LeafBrushTraceTests : IClassFixture<AmbientFixture>
{
    private readonly AmbientScene _scene;
    private readonly int _leaf;
    private readonly Vec3 _min;
    private readonly Vec3 _max;

    /// <summary>Finds the leaf and its brush's box.</summary>
    /// <param name="fixture">The loaded map.</param>
    public LeafBrushTraceTests(AmbientFixture fixture)
    {
        _scene = fixture.Ldr;
        (_leaf, _min, _max) = FirstBoxLeaf(_scene);
    }

    private Vec3 Centre => new((_min.X + _max.X) * 0.5f, (_min.Y + _max.Y) * 0.5f, (_min.Z + _max.Z) * 0.5f);

    private static (int Leaf, Vec3 Min, Vec3 Max) FirstBoxLeaf(AmbientScene scene)
    {
        for (int l = 0; l < scene.Leaves.Length; l++)
        {
            DLeaf leaf = scene.Leaves[l];
            if (leaf.NumLeafBrushes != 1)
            {
                continue;
            }

            DBrush brush = scene.Brushes[scene.LeafBrushes[leaf.FirstLeafBrush]];
            if ((brush.Contents & LeafBrushTrace.MaskOpaque) == 0)
            {
                continue;
            }

            float[] lo = [float.MinValue, float.MinValue, float.MinValue];
            float[] hi = [float.MaxValue, float.MaxValue, float.MaxValue];
            bool box = true;
            for (int s = 0; s < brush.NumSides && box; s++)
            {
                DPlane p = scene.Planes[scene.BrushSides[brush.FirstSide + s].PlaneNum];
                float[] n = [p.Normal.X, p.Normal.Y, p.Normal.Z];
                int axis = Array.FindIndex(n, v => v != 0);
                box = axis >= 0 && n.Count(v => v != 0) == 1;
                if (box && n[axis] > 0)
                {
                    hi[axis] = p.Dist;
                }
                else if (box)
                {
                    lo[axis] = -p.Dist;
                }
            }

            if (box && lo.All(v => v != float.MinValue) && hi.All(v => v != float.MaxValue))
            {
                return (l, new Vec3(lo[0], lo[1], lo[2]), new Vec3(hi[0], hi[1], hi[2]));
            }
        }

        throw new InvalidOperationException("the fixture has no single-box leaf");
    }

    [Fact]
    public void ARayStartingInsideTheBrushStartsSolidAtFractionZero()
    {
        LeafBrushHit hit = LeafBrushTrace.Trace(_leaf, Centre, Centre + new Vec3(0, 0, 1), _scene);
        Assert.True(hit.StartSolid);
        Assert.Equal(0.0f, hit.Fraction);
    }

    [Fact]
    public void ARayThatMissesTheBrushIsNotBlocked()
    {
        Vec3 above = new(Centre.X, Centre.Y, _max.Z + 100);
        LeafBrushHit hit = LeafBrushTrace.Trace(_leaf, above, above + new Vec3(50, 0, 0), _scene);
        Assert.Equal(1.0f, hit.Fraction);
        Assert.False(hit.StartSolid);
    }

    [Fact]
    public void ARayIntoTheTopFaceStopsJustAboveItWithTheFacesNormal()
    {
        Vec3 start = new(Centre.X, Centre.Y, _max.Z + 10);
        Vec3 end = new(Centre.X, Centre.Y, _max.Z - 10);
        LeafBrushHit hit = LeafBrushTrace.Trace(_leaf, start, end, _scene);

        // The entry is pulled back by DIST_EPSILON (1/32): (10 - 1/32) / 20.
        Assert.Equal((10.0f - LeafBrushTrace.DistEpsilonSingle) / 20.0f, hit.Fraction, 5);
        Assert.Equal(new Vec3(0, 0, 1), hit.Normal);
    }
}
