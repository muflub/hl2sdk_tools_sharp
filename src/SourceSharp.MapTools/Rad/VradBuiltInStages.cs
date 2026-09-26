//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Props;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// The stages lanes 4d and 4g built, as <see cref="VradStages.Default"/> runs
/// them.
/// </summary>
internal static class VradBuiltInStages
{
    /// <summary><c>MakeAllScales</c> + <c>BounceLight</c> (4d): <see cref="Light.RadWorld.BounceAsync"/>.</summary>
    internal sealed class Bounce : IVradBounceStage
    {
        public Task BounceAsync(RadPass pass, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(pass);
            return pass.World.BounceAsync(pass.Tracer, pass.Parallelism, cancellationToken);
        }
    }

    /// <summary><c>ComputeDetailPropLighting</c>, 4g's.</summary>
    internal sealed class DetailProps : IVradOtherLightingStage
    {
        public async Task ComputeAsync(RadPass pass, BspData bsp, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(pass);
            ArgumentNullException.ThrowIfNull(bsp);

            GameLumpEntry? entry = Find(bsp, GameLumpId.DetailProps);
            if (entry is null)
            {
                return;
            }

            // No props, no lighting lumps at all.
            DetailPropLump lump = DetailPropLump.Read(entry.Value);
            if (lump.Props.Count == 0)
            {
                return;
            }

            KdRayTracer environment = Environment(pass);
            Vec3[] centres = await DetailPropLighting.LoadModelCentresAsync(lump, pass.Content, cancellationToken)
                .ConfigureAwait(false);
            PropLightSampler sampler = new(
                environment, pass.Options.Compliance, pass.World.Lights.SunAngularExtent, pass.Options.Fast);

            DetailPropLightingResult result = await DetailPropLighting.ComputeAsync(
                pass.Scene(bsp),
                lump,
                centres,
                PropLights.FromDirectLights(pass.World.Lights.Active),
                sampler,
                pass.Options.Compliance,
                pass.Parallelism.MaxDegree,
                cancellationToken).ConfigureAwait(false);
            DetailPropLighting.WriteInto(bsp, lump, result, pass.Hdr);
        }
    }

    /// <summary><c>ComputePerLeafAmbientLighting</c>, 4g's.</summary>
    internal sealed class LeafAmbient : IVradOtherLightingStage
    {
        public async Task ComputeAsync(RadPass pass, BspData bsp, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(pass);
            ArgumentNullException.ThrowIfNull(bsp);

            AmbientScene scene = pass.Scene(bsp);
            DWorldLight[] worldLights = scene.WorldLights.ToArray();
            LeafAmbientResult result = await LeafAmbientBuilder.BuildAsync(
                scene,
                worldLights,
                new LeafAmbientOptions
                {
                    Compliance = pass.Options.Compliance,
                    FastAmbient = pass.Options.FastAmbient,
                    Parallelism = pass.Parallelism.MaxDegree,
                },
                new TracerLineVisibility(Environment(pass), pass.Options.Compliance),
                cancellationToken).ConfigureAwait(false);

            // The index is version 0 and the samples version 1 in every stock
            // map; the world lights go back with DWL_FLAGS_INAMBIENTCUBE as
            // phase 1 left it.
            bsp.SetLump(
                pass.Hdr ? BspLump.LeafAmbientIndexHdr : BspLump.LeafAmbientIndex,
                MemoryMarshal.AsBytes(result.Index.AsSpan()).ToArray(),
                0);
            bsp.SetLump(
                pass.Hdr ? BspLump.LeafAmbientLightingHdr : BspLump.LeafAmbientLighting,
                MemoryMarshal.AsBytes(result.Lighting.AsSpan()).ToArray(),
                1);
            BspLump lights = pass.Hdr ? BspLump.WorldLightsHdr : BspLump.WorldLights;
            bsp.SetLump(lights, MemoryMarshal.AsBytes(worldLights.AsSpan()).ToArray(), bsp[lights].Version);
        }
    }

    /// <summary><c>StaticPropMgr-&gt;ComputeLighting</c>, 4g's.</summary>
    internal sealed class StaticProps : IVradOtherLightingStage
    {
        public async Task ComputeAsync(RadPass pass, BspData bsp, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(pass);
            ArgumentNullException.ThrowIfNull(bsp);

            GameLumpEntry? entry = Find(bsp, GameLumpId.StaticProps);
            if (entry is null)
            {
                return;
            }

            StaticPropLump lump = StaticPropLump.Read(entry.Value);
            IReadOnlyList<StaticPropModel> models = await new StaticPropModelLoader(pass.Content, pass.PropCollision)
                .LoadDictionaryAsync(lump.ModelNames, cancellationToken).ConfigureAwait(false);
            PropLightSampler sampler = new(
                Environment(pass), pass.Options.Compliance, pass.World.Lights.SunAngularExtent, pass.Options.Fast);

            StaticPropLightingResult result = await StaticPropLighting.ComputeAsync(
                pass.Scene(bsp),
                lump,
                models,
                PropLights.FromDirectLights(pass.World.Lights.Active),
                sampler,
                new StaticPropLightingOptions
                {
                    Hdr = pass.Hdr,
                    Indirect = pass.World.Settings.Bounces >= 1,
                    DisableSelfShadowing = pass.Options.DisablePropSelfShadowing,
                    StaticPropIndirectMode = pass.Options.StaticPropIndirectMode,
                    Compliance = pass.Options.Compliance,
                    Parallelism = pass.Parallelism.MaxDegree,
                },
                cancellationToken).ConfigureAwait(false);
            await StaticPropLighting.WriteIntoAsync(bsp, result, cancellationToken).ConfigureAwait(false);
        }
    }

    private static GameLumpEntry? Find(BspData bsp, string code)
    {
        int id = GameLumpId.MakeId(code);
        foreach (GameLumpEntry e in bsp.GameLumps)
        {
            if (e.Id == id)
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>
    /// 4g's samplers trace through <see cref="KdRayTracer.TestLines"/>, which
    /// the batch <see cref="IRayTracer"/> seam does not have.
    /// </summary>
    private static KdRayTracer Environment(RadPass pass) =>
        pass.Tracer as KdRayTracer
            ?? (pass.Tracer as HybridRayTracer)?.CpuTracer
            ?? throw new NotSupportedException(
                $"prop and leaf-ambient lighting need the CPU KD tracer; the compile was given {pass.Tracer.TracerIdentity}");
}
