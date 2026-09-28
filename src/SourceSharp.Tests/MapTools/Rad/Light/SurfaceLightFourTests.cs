//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Light;

/// <summary>
/// The four-wide surface-light gather must give the scalar lane's bits
/// (<c>GatherSampleStandardLightSSE</c>'s <c>emit_surface</c> case): the same
/// tape, the same rays in the same order.
/// </summary>
public sealed class SurfaceLightFourTests
{
    private static readonly LeafInfo[] OneLeaf = [new LeafInfo(0, 0, 0, 0, new Vec3(-1e4f, -1e4f, -1e4f), new Vec3(1e4f, 1e4f, 1e4f), 0, 0)];

    private static DirectLightGatherer Gatherer(DirectLight light, bool scalar, ComplianceOptions compliance)
    {
        LightGeometry g = Geometry.Load(LightBox.Map());
        DirectLightingSettings settings = new() { Compliance = compliance };
        return new DirectLightGatherer([light], 0, settings, new CompiledBspTree(g), OneLeaf, SkyCameras.None)
        {
            ScalarStandardLights = scalar,
        };
    }

    private static Vec3 RandomVector(Random r, float scale) =>
        new((float)(r.NextDouble() - 0.5) * scale, (float)(r.NextDouble() - 0.5) * scale, (float)(r.NextDouble() - 0.5) * scale);

    private static (int[] Tape, Ray[] Rays) Emit(DirectLightGatherer g, SampleGroup group, bool[] needed, GatherFlags flags)
    {
        LightRayLog rays = new();
        g.Emit(g.Lights[0], group, needed, rays, new GatherOutput(), flags, 0.0f);
        int[] tape = new int[rays.Tape.Length];
        for (int i = 0; i < tape.Length; i++)
        {
            tape[i] = rays.Tape.ReadInt();
        }

        return (tape, rays.VisibilityRays().ToArray());
    }

    private static DirectLight RandomLight(Random r)
    {
        DirectLight light = new()
        {
            Type = EmitType.Surface,
            Origin = RandomVector(r, 512),
            Normal = RandomVector(r, 2).Normalise().Normalised,
        };

        if (r.Next(2) == 0)
        {
            light.StartFadeDistance = (float)r.NextDouble() * 200;
            light.EndFadeDistance = light.StartFadeDistance + ((float)r.NextDouble() * 300) - 50;
        }

        return light;
    }

    private static SampleGroup RandomGroup(Random r, DirectLight light, out bool[] needed)
    {
        int count = r.Next(1, 5);
        SampleGroup group = new() { Count = count, NormalCount = r.Next(2) == 0 ? 1 : BumpBasis.LightmapCount };
        needed = new bool[SampleGroup.Lanes];
        for (int lane = 0; lane < SampleGroup.Lanes; lane++)
        {
            // Now and then a sample sits on the light itself (a zero
            // distance), or faces exactly across the ray (a zero dot).
            group.Points[lane] = r.Next(40) == 0 ? light.Origin : RandomVector(r, 512);
            for (int n = 0; n < BumpBasis.LightmapCount; n++)
            {
                group.Normal(n, lane) = RandomVector(r, 2).Normalise().Normalised;
            }

            if (r.Next(30) == 0)
            {
                Vec3 toLight = light.Origin - group.Points[lane];
                group.Normal(0, lane) = new Vec3(toLight.Y, -toLight.X, 0);
            }

            needed[lane] = r.Next(4) != 0;
        }

        return group;
    }

    [Fact]
    public void FourWideMatchesTheScalarLaneBitForBit()
    {
        Random r = new(9191);
        int traced = 0;
        int perpendicular = 0;
        for (int c = 0; c < 4000; c++)
        {
            DirectLight light = RandomLight(r);
            SampleGroup group = RandomGroup(r, light, out bool[] needed);
            GatherFlags flags = r.Next(8) == 0 ? GatherFlags.IgnoreNormals : GatherFlags.None;

            (int[] scalarTape, Ray[] scalarRays) = Emit(Gatherer(light, scalar: true, ComplianceOptions.Correct), group, needed, flags);
            (int[] fourTape, Ray[] fourRays) = Emit(Gatherer(light, scalar: false, ComplianceOptions.Correct), group, needed, flags);
            Assert.Equal(scalarTape, fourTape);
            Assert.Equal(scalarRays, fourRays);

            traced += fourRays.Length;
            for (int lane = 0; lane < group.Count; lane++)
            {
                perpendicular += Vec3.Dot(light.Origin - group.Points[lane], group.Normal(0, lane)) == 0.0f ? 1 : 0;
            }
        }

        // Rays were traced, and some normals lay exactly across the ray.
        Assert.True(traced > 1000, $"{traced} rays");
        Assert.True(perpendicular > 0, "no lane faced exactly across its ray");
    }

    /// <summary>
    /// Under stock's estimates the scalar lane is the only path, whichever the
    /// switch says: the four-wide one divides exactly.
    /// </summary>
    [Fact]
    public void TheEstimatePathStaysScalar()
    {
        if (!FloatEstimate.IsSupported)
        {
            return;
        }

        Random r = new(77);
        for (int c = 0; c < 500; c++)
        {
            DirectLight light = RandomLight(r);
            SampleGroup group = RandomGroup(r, light, out bool[] needed);
            (int[] scalarTape, Ray[] scalarRays) = Emit(Gatherer(light, scalar: true, ComplianceOptions.Stock), group, needed, GatherFlags.None);
            (int[] fourTape, Ray[] fourRays) = Emit(Gatherer(light, scalar: false, ComplianceOptions.Stock), group, needed, GatherFlags.None);
            Assert.Equal(scalarTape, fourTape);
            Assert.Equal(scalarRays, fourRays);
        }
    }
}
