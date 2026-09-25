using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapTools.Rad.Light;

using SurfaceFlags = SourceSharp.MapTools.Materials.SurfaceFlags;

namespace SourceSharp.Tests.MapTools.Rad.Bounce;

/// <summary>
/// The 4c in-memory box (<see cref="LightBox"/>) given a one-cluster
/// visibility lump, so vrad subdivides and bounces it: six 256-unit faces,
/// floor 0, ceiling 1, walls 2-5, all in leaf 0, cluster 0.
/// </summary>
internal static class BounceBox
{
    internal static readonly CompileParallelism One = new() { MaxDegree = 1 };

    internal static LightTestMap Map(bool seesItself = true, SurfaceFlags ceiling = 0, bool light = true)
    {
        LightTestMap map = LightBox.Map(ceiling);
        map.Visibility = LightTestMap.VisLump([[seesItself]]);
        if (light)
        {
            map.Entities.Add(LightTestMap.Entity(
                ("classname", "light"), ("origin", "128 128 128"), ("_light", "255 255 255 200")));
        }

        return map;
    }

    /// <summary>Patches only: <c>RadWorld_Start</c>, no rays.</summary>
    internal static RadWorld Build(LightTestMap map, DirectLightingSettings? settings = null) =>
        LightBox.Build(map, settings ?? LightBox.Settings());

    /// <summary>Patches and direct light: everything before the bounce.</summary>
    internal static async Task<RadWorld> LitAsync(
        LightTestMap map, DirectLightingSettings? settings = null, CompileParallelism? parallelism = null)
    {
        IRayTracer tracer = map.Tracer();
        CompileParallelism p = parallelism ?? One;
        RadWorld world = await RadWorld.StartAsync(
            map.Build(), settings ?? LightBox.Settings(), new TextureLightTable(new(), "box"),
            tracer, p, CancellationToken.None);
        await world.LightFacesAsync(tracer, p, CancellationToken.None);
        return world;
    }

    /// <summary>Lit and bounced.</summary>
    internal static async Task<RadWorld> BouncedAsync(
        LightTestMap map, DirectLightingSettings? settings = null, CompileParallelism? parallelism = null)
    {
        RadWorld world = await LitAsync(map, settings, parallelism);
        await world.BounceAsync(map.Tracer(), parallelism ?? One, CancellationToken.None);
        return world;
    }

    /// <summary>A face's leaf patches, in its list order.</summary>
    internal static List<int> Leaves(RadWorld world, int face)
    {
        List<int> leaves = [];
        for (int p = world.Patches.FacePatches[face]; p != Patch.Invalid; p = world.Patches.At(p).Next)
        {
            if (world.Patches.At(p).Child1 == Patch.Invalid)
            {
                leaves.Add(p);
            }
        }

        return leaves;
    }
}
