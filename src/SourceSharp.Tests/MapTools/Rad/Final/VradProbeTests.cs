using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Final;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rad.Final;

/// <summary>
/// A diagnostic, not a gate: <c>P4F_PROBE=map:face:s:t</c> prints every sample
/// that the radial filter splats onto one luxel, own and neighbours', with its
/// weight. Skipped unless the variable is set.
/// </summary>
public sealed class VradProbeTests(ITestOutputHelper output)
{
    [ProbeFact]
    public async Task ProbeOneLuxel()
    {
        string[] p = Environment.GetEnvironmentVariable("P4F_PROBE")!.Split(':');
        string name = p[0];
        int face = int.Parse(p[1], CultureInfo.InvariantCulture);
        int ls = int.Parse(p[2], CultureInfo.InvariantCulture);
        int lt = int.Parse(p[3], CultureInfo.InvariantCulture);

        BspData bsp = await VradStockReference.LoadAsync(VradStockReference.InputFor(name));
        VradOptions options = VradOptions.Default with
        {
            Bounces = 0,
            Compliance = ComplianceOptions.Stock,
            Supersample = Environment.GetEnvironmentVariable("P4F_PROBE_NOEXTRA") is not { Length: > 0 },
        };
        DirectLightingSettings settings = DirectLightingSettings.FromVrad(options, hdr: false);
        IRayTracer tracer = SourceSharp.Tests.MapTools.Rad.Light.StockRadWorld.Tracer(bsp, hdr: false);
        TextureLightTable texLights = new(new SourceSharp.MapFormats.Text.RadLightFile(), name);
        RadWorld world = await RadWorld.StartAsync(bsp, settings, texLights, tracer, CompileParallelism.Default, default);
        await world.LightFacesAsync(tracer, CompileParallelism.Default, default);

        FinalLightContext context = new(world, MacroTextures.None);
        FaceLightInfo info = context.Info(face);
        output.WriteLine($"face {face}: w {info.Width} h {info.Height} neighbours [{string.Join(",", world.Neighbours.Neighbours(face).ToArray())}]");

        void Dump(int f, string label)
        {
            FaceLight? fl = world.FaceLights[f];
            if (fl is null)
            {
                output.WriteLine($"{label} {f}: no facelight");
                return;
            }

            FaceLightInfo l = context.Info(f);
            LightingValue[]? light = fl.LightFor(0, 0);
            for (int k = 0; k < fl.Samples.Length; k++)
            {
                LightSample sample = fl.Samples[k];
                (float minS, float minT) = info.WorldToLuxel(l.LuxelToWorld(sample.MinS, sample.MinT));
                (float maxS, float maxT) = info.WorldToLuxel(l.LuxelToWorld(sample.MaxS, sample.MaxT));
                if (maxS < ls - 1 || minS > ls + 1 || maxT < lt - 1 || minT > lt + 1)
                {
                    continue;
                }

                LuxelRadial one = new();
                one.Reset(info);
                one.AddDirect(sample.Position, minS, minT, maxS, maxT, [light![k]], false, false);
                int i = ls + (lt * info.Width);
                output.WriteLine(
                    $"{label} {f} sample {k} ({sample.S},{sample.T}) area {sample.Area:R} pos {sample.Position} "
                    + $"bounds [{minS:R},{minT:R}]-[{maxS:R},{maxT:R}] light {light![k].Lighting} "
                    + $"weight-on-luxel {one.Weight(i):R} winding {sample.WindingCount}");
            }
        }

        Dump(face, "own");
        foreach (int n in world.Neighbours.Neighbours(face))
        {
            Dump(n, "neighbour");
        }

        FinalLightScratch scratch = new();
        scratch.Radial.Reset(info);
        FinalLightFace.BuildLuxelRadial(context, face, 0, scratch.Radial, scratch);
        int idx = ls + (lt * info.Width);
        output.WriteLine($"radial luxel ({ls},{lt}): weight {scratch.Radial.Weight(idx):R} light {scratch.Radial.Light(0, idx).Lighting}");
        output.WriteLine($"luxel position {world.FaceLights[face]!.Luxels[idx]}");
    }
}

/// <summary>
/// A diagnostic: <c>P4F_PROBE_SAMPLE=map:face:sample</c> prints every light's
/// direct contribution to one sample, as the initial gather computes it.
/// </summary>
public sealed class VradSampleProbeTests(ITestOutputHelper output)
{
    [SampleProbeFact]
    public async Task ProbeOneSample()
    {
        string[] p = Environment.GetEnvironmentVariable("P4F_PROBE_SAMPLE")!.Split(':');
        string name = p[0];
        int face = int.Parse(p[1], CultureInfo.InvariantCulture);
        int sampleIndex = int.Parse(p[2], CultureInfo.InvariantCulture);

        BspData bsp = await VradStockReference.LoadAsync(VradStockReference.InputFor(name));
        VradOptions options = VradOptions.Default with { Bounces = 0, Compliance = ComplianceOptions.Stock };
        DirectLightingSettings settings = DirectLightingSettings.FromVrad(options, hdr: false);
        KdRayTracer tracer = (KdRayTracer)SourceSharp.Tests.MapTools.Rad.Light.StockRadWorld.Tracer(bsp, hdr: false);
        TextureLightTable texLights = new(new SourceSharp.MapFormats.Text.RadLightFile(), name);
        RadWorld world = await RadWorld.StartAsync(bsp, settings, texLights, tracer, CompileParallelism.Default, default);
        await world.LightFacesAsync(tracer, CompileParallelism.Default, default);

        FaceLight fl = world.FaceLights[face]!;
        LightSample sample = fl.Samples[sampleIndex];
        FinalLightContext context = new(world, MacroTextures.None);
        FaceLightInfo info = context.Info(face);
        output.WriteLine($"face {face} sample {sampleIndex} pos {sample.Position} flat {info.IsFlat} normal {info.FaceNormal} final {fl.LightFor(0, 0)![sampleIndex].Lighting}");

        SampleGroup group = new() { Count = 1, NormalCount = 1 };
        for (int lane = 0; lane < SampleGroup.Lanes; lane++)
        {
            group.Points[lane] = sample.Position + info.FaceNormal;
            group.Normal(0, lane) = info.IsFlat ? info.FaceNormal : sample.Normal;
            group.Clusters[lane] = world.Tree.ClusterFromPoint(sample.Position);
        }

        bool[] needed = [true, false, false, false];
        int index = 0;
        foreach (DirectLight dl in world.Gatherer.Lights)
        {
            bool pvs = LightVisibility.PvsCheck(dl.Pvs, group.Clusters[0]);
            if (!pvs)
            {
                index++;
                continue;
            }

            LightRayLog rays = new() { StockRays = true };
            GatherOutput gather = new();
            world.Gatherer.Gather(dl, group, needed, rays, gather);
            Ray[] vis = rays.VisibilityRays().ToArray();
            Ray[] sky = rays.SkyRays().ToArray();
            HitId[] visHits = new HitId[vis.Length];
            HitId[] skyHits = new HitId[sky.Length];
            tracer.TraceClosest(vis, visHits, RayTraceOptions.StockExact);
            tracer.TraceClosest(sky, skyHits, RayTraceOptions.StockExact);
            ulong[] bits = new ulong[(vis.Length + 63) / 64 + 1];
            for (int i = 0; i < vis.Length; i++)
            {
                if (LightRayLog.IsBlocking(visHits[i]))
                {
                    bits[i >> 6] |= 1UL << (i & 63);
                }
            }

            rays.BeginReplay(bits, 0, skyHits, 0);
            gather.Clear();
            world.Gatherer.Gather(dl, group, needed, rays, gather);
            float fx = gather.Dot[0] * gather.Falloff[0];
            if (fx != 0 || vis.Length > 0)
            {
                string hit = vis.Length > 0 ? $"vis hit {visHits[0].Surface} frac {visHits[0].Fraction:R}" : string.Empty;
                output.WriteLine(
                    $"light {index} {dl.Type} style {dl.Style} origin {dl.Origin} dot {gather.Dot[0]:R} falloff {gather.Falloff[0]:R} "
                    + $"-> {(dl.Intensity * fx)} {hit}");
            }

            index++;
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SampleProbeFactAttribute : FactAttribute
{
    public SampleProbeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("P4F_PROBE_SAMPLE") is not { Length: > 0 }
            || VradStockReference.SkipReason() is not null)
        {
            Skip = "a diagnostic: set P4F_PROBE_SAMPLE=map:face:sample";
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ProbeFactAttribute : FactAttribute
{
    public ProbeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("P4F_PROBE") is not { Length: > 0 }
            || VradStockReference.SkipReason() is not null)
        {
            Skip = "a diagnostic: set P4F_PROBE=map:face:s:t";
        }
    }
}
