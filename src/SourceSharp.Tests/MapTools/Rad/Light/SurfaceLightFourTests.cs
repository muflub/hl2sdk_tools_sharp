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
/// The four-wide surface-light gather must give the scalar lane's bits: the
/// same tape record (dots and falloffs as raw float bits) and the same
/// visibility rays in the same order.
/// </summary>
/// <remarks>
/// The tape is compared as integers, so a zero of the wrong sign or a NaN
/// with a different payload fails the fact rather than comparing equal.
/// </remarks>
public sealed class SurfaceLightFourTests
{
    private static readonly LeafInfo[] OneLeaf = [new LeafInfo(0, 0, 0, 0, new Vec3(-1e4f, -1e4f, -1e4f), new Vec3(1e4f, 1e4f, 1e4f), 0, 0)];

    private static DirectLightGatherer Gatherer(DirectLight light, bool scalar, ComplianceOptions? compliance = null)
    {
        LightGeometry g = Geometry.Load(LightBox.Map());
        DirectLightingSettings settings = new() { Compliance = compliance ?? ComplianceOptions.Correct };
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

    private static DirectLight RandomSurfaceLight(Random r)
    {
        // Axial emitters now and then, so that a sample in the emitter's own
        // plane gives a cosine of exactly zero (see the zero-sign fact).
        Vec3 normal = r.Next(3) == 0
            ? new Vec3(0, 0, r.Next(2) == 0 ? 1 : -1)
            : RandomVector(r, 2).Normalise().Normalised;
        DirectLight light = new()
        {
            Type = EmitType.Surface,
            Origin = RandomVector(r, 512),
            Normal = normal,
        };

        if (r.Next(2) == 0)
        {
            light.StartFadeDistance = (float)r.NextDouble() * 200;
            light.EndFadeDistance = light.StartFadeDistance + ((float)r.NextDouble() * 300) - 50;
        }

        return light;
    }

    [Fact]
    public void FourWideMatchesTheScalarLaneBitForBit()
    {
        Random r = new(9191);
        int traced = 0;
        for (int c = 0; c < 4000; c++)
        {
            DirectLight light = RandomSurfaceLight(r);

            int count = r.Next(1, 5);
            SampleGroup group = new() { Count = count, NormalCount = r.Next(2) == 0 ? 1 : BumpBasis.LightmapCount };
            bool[] needed = new bool[SampleGroup.Lanes];
            for (int lane = 0; lane < SampleGroup.Lanes; lane++)
            {
                // Now and then a sample sits on the light itself (a zero
                // distance, so NaN directions), or in the emitter's plane.
                int kind = r.Next(40);
                Vec3 point = RandomVector(r, 512);
                group.Points[lane] = kind == 0
                    ? light.Origin
                    : kind < 4 ? new Vec3(point.X, point.Y, light.Origin.Z) : point;
                for (int n = 0; n < BumpBasis.LightmapCount; n++)
                {
                    group.Normal(n, lane) = RandomVector(r, 2).Normalise().Normalised;
                }

                needed[lane] = r.Next(4) != 0;
            }

            GatherFlags flags = r.Next(8) == 0 ? GatherFlags.IgnoreNormals : GatherFlags.None;
            (int[] scalarTape, Ray[] scalarRays) = Emit(Gatherer(light, scalar: true), group, needed, flags);
            (int[] fourTape, Ray[] fourRays) = Emit(Gatherer(light, scalar: false), group, needed, flags);
            Assert.Equal(scalarTape, fourTape);
            Assert.Equal(scalarRays, fourRays);
            traced += scalarRays.Length;
        }

        // The draw must reach the ray-recording branch, not only the masks.
        Assert.True(traced > 1000, $"only {traced} rays were recorded");
    }

    [Fact]
    public void AnEmitterCosineOfZeroKeepsTheScalarNegativeZero()
    {
        // The sample lies in the emitter's plane, so its cosine to the light
        // is +0 and the scalar lane's unary minus makes it -0, which maxps
        // keeps and the falloff (-0 / dist2) carries onto the tape. A
        // four-wide negation spelled as 0 - x would record +0 instead.
        DirectLight light = new() { Type = EmitType.Surface, Origin = new Vec3(0, 0, 0), Normal = new Vec3(0, 0, 1) };
        SampleGroup group = new() { Count = 1, NormalCount = 1 };
        for (int lane = 0; lane < SampleGroup.Lanes; lane++)
        {
            group.Points[lane] = new Vec3(-64, 0, 0);
            group.Normal(0, lane) = new Vec3(1, 0, 0);
        }

        (int[] scalarTape, _) = Emit(Gatherer(light, scalar: true), group, [true, true, true, true], GatherFlags.None);
        (int[] fourTape, _) = Emit(Gatherer(light, scalar: false), group, [true, true, true, true], GatherFlags.None);

        // Record: kind, normals, count, traced, then lane 0's falloff.
        Assert.Equal(BitConverter.SingleToInt32Bits(-0.0f), scalarTape[4]);
        Assert.Equal(scalarTape, fourTape);
    }

    [Fact]
    public void TheStockEstimatePathStaysScalar()
    {
        // Under stock's estimates the four-wide path must not be taken: its
        // exact divides would move the last bits. With estimates on, the
        // gatherer's own flag makes no difference because both runs take the
        // scalar lane.
        if (!FloatEstimate.IsSupported)
        {
            return;
        }

        Random r = new(77);
        for (int c = 0; c < 200; c++)
        {
            DirectLight light = RandomSurfaceLight(r);
            SampleGroup group = new() { Count = 4, NormalCount = 1 };
            for (int lane = 0; lane < SampleGroup.Lanes; lane++)
            {
                group.Points[lane] = RandomVector(r, 512);
                group.Normal(0, lane) = RandomVector(r, 2).Normalise().Normalised;
            }

            bool[] needed = [true, true, true, true];
            (int[] scalarTape, Ray[] scalarRays) = Emit(Gatherer(light, scalar: true, ComplianceOptions.Stock), group, needed, GatherFlags.None);
            (int[] fourTape, Ray[] fourRays) = Emit(Gatherer(light, scalar: false, ComplianceOptions.Stock), group, needed, GatherFlags.None);
            Assert.Equal(scalarTape, fourTape);
            Assert.Equal(scalarRays, fourRays);
        }
    }
}
