//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// The axis-aligned scene and the tree built from it.
/// </summary>
public sealed class KdBoxSceneFixture
{
    /// <summary>Loads the box scene and builds the tree.</summary>
    public KdBoxSceneFixture()
    {
        Scene = StockKdScene.Load("kd-boxes");
        Tracer = KdRayTracer.Build(Scene.Triangles);
        Ours = new HitId[Scene.RayCount];
        Tracer.TraceClosest(Scene.Rays, Ours, RayTraceOptions.StockExact);
    }

    /// <summary>The scene, tree and answers stock produced.</summary>
    internal StockKdScene Scene { get; }

    /// <summary>The tree this port built from the same triangles.</summary>
    public KdRayTracer Tracer { get; }

    /// <summary>This port's answers.</summary>
    public HitId[] Ours { get; }
}

/// <summary>
/// The same 4a gate over AXIS-ALIGNED geometry, which is the shape a map's
/// brush faces have.
/// </summary>
/// <remarks>
/// <para>
/// THIS CLASS EXISTS BECAUSE MUTATION TESTING SAID IT HAD TO. Against the
/// general-position scene alone, four mutations of stock's build rules stayed
/// GREEN -- removing the empty-side growing, flipping which way a triangle
/// lying in the splitting plane goes, lowering the depth cap from 21 to 18,
/// and replacing the reciprocal estimate with an exact divide. None of them
/// was a weak fact: each mutation simply never reached its subject, because
/// random triangles in general position never have three vertices sharing a
/// coordinate, never leave one side of a split wholly empty, and never build
/// deep enough to meet the cap.
/// </para>
/// <para>
/// A room of axis-aligned boxes reaches all of that, and it is also what vrad
/// actually feeds this tracer: world brush faces and prop collision hulls, not
/// a cloud of scattered triangles.
/// </para>
/// <para>
/// The positions are jittered, so this is not a lattice in the sense that got
/// a prior lane into trouble -- the boxes do not line up with each other or
/// with any grid. What is wanted here is axis-PARALLEL faces.
/// </para>
/// </remarks>
public sealed class KdRayTracerBoxSceneTests : IClassFixture<KdBoxSceneFixture>
{
    private readonly KdBoxSceneFixture _fixture;

    /// <summary>Takes the shared box scene.</summary>
    /// <param name="fixture">The shared fixture.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is null.</exception>
    public KdRayTracerBoxSceneTests(KdBoxSceneFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
    }

    /// <summary>
    /// The scene really does contain triangles that lie flat in an axis, which
    /// is the precondition for everything else here.
    /// </summary>
    /// <remarks>
    /// Without this the class would be a second copy of the general-position
    /// facts wearing a different name, and the mutations it exists to catch
    /// would go on passing.
    /// </remarks>
    [Fact]
    public void TheSceneHasTrianglesFlatInEveryAxis()
    {
        int[] flat = new int[3];
        foreach (TracedTriangle t in _fixture.Scene.Triangles)
        {
            if (t.V0.X == t.V1.X && t.V1.X == t.V2.X)
            {
                flat[0]++;
            }

            if (t.V0.Y == t.V1.Y && t.V1.Y == t.V2.Y)
            {
                flat[1]++;
            }

            if (t.V0.Z == t.V1.Z && t.V1.Z == t.V2.Z)
            {
                flat[2]++;
            }
        }

        Assert.True(flat[0] > 0, "no triangle is flat in x");
        Assert.True(flat[1] > 0, "no triangle is flat in y");
        Assert.True(flat[2] > 0, "no triangle is flat in z");
    }

    /// <summary>The tree has stock's node count.</summary>
    [Fact]
    public void TheTreeHasTheSameNodeCountAsStocks()
    {
        Assert.Equal(_fixture.Scene.NodeChildren.Length, _fixture.Tracer.NodeCount);
    }

    /// <summary>Every node matches stock's, word for word.</summary>
    [Fact]
    public void EveryNodeMatchesStocksNodeExactly()
    {
        int differing = 0;
        int firstBad = -1;
        for (int i = 0; i < _fixture.Tracer.NodeCount; i++)
        {
            (int children, int splitBits) = _fixture.Tracer.Node(i);
            if (children != _fixture.Scene.NodeChildren[i]
                || splitBits != _fixture.Scene.NodeSplitBits[i])
            {
                differing++;
                if (firstBad < 0)
                {
                    firstBad = i;
                }
            }
        }

        Assert.True(
            differing == 0,
            differing == 0
                ? string.Empty
                : $"{differing} of {_fixture.Tracer.NodeCount} nodes differ; the first is node "
                  + $"{firstBad}");
    }

    /// <summary>The triangle index list matches stock's.</summary>
    [Fact]
    public void TheTriangleIndexListMatchesStocks()
    {
        Assert.Equal(_fixture.Scene.Indices.Length, _fixture.Tracer.IndexCount);

        int differing = 0;
        for (int i = 0; i < _fixture.Tracer.IndexCount; i++)
        {
            if (_fixture.Tracer.Index(i) != _fixture.Scene.Indices[i])
            {
                differing++;
            }
        }

        Assert.Equal(0, differing);
    }

    /// <summary>Every ray hits the same triangle as stock.</summary>
    [Fact]
    public void EveryRayHitsTheSameTriangleAsStock()
    {
        int differing = 0;
        for (int i = 0; i < _fixture.Scene.RayCount; i++)
        {
            if (_fixture.Scene.StockHitId[i] != _fixture.Ours[i].Surface)
            {
                differing++;
            }
        }

        Assert.Equal(0, differing);
    }

    /// <summary>Every hit distance matches stock's, bit for bit.</summary>
    [Fact]
    public void EveryHitDistanceMatchesStockBitForBit()
    {
        int differing = 0;
        for (int i = 0; i < _fixture.Scene.RayCount; i++)
        {
            if (_fixture.Scene.StockHitId[i] < 0)
            {
                continue;
            }

            if (BitConverter.SingleToInt32Bits(_fixture.Ours[i].Fraction)
                != BitConverter.SingleToInt32Bits(_fixture.Scene.StockDistance[i]))
            {
                differing++;
            }
        }

        Assert.Equal(0, differing);
    }

    /// <summary>Most rays hit, so the comparison is not vacuous.</summary>
    [Fact]
    public void StockHitsSomethingOnMostRays()
    {
        int hits = 0;
        for (int i = 0; i < _fixture.Scene.RayCount; i++)
        {
            if (_fixture.Scene.StockHitId[i] >= 0)
            {
                hits++;
            }
        }

        Assert.True(
            hits > _fixture.Scene.RayCount / 4,
            $"stock hit on {hits} of {_fixture.Scene.RayCount} rays");
    }
}
