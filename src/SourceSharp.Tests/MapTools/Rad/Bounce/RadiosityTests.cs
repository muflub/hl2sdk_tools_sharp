using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;

using SurfaceFlags = SourceSharp.MapTools.Materials.SurfaceFlags;

namespace SourceSharp.Tests.MapTools.Rad.Bounce;

/// <summary><c>GatherLight</c>, <c>CollectLight</c> and <c>BounceLight</c>.</summary>
public sealed class RadiosityTests
{
    private static async Task<(Radiosity Radiosity, RadWorld World, TransferSet Transfers)> PrepareAsync(
        LightTestMap? map = null, DirectLightingSettings? settings = null, Action<RadWorld>? mutate = null)
    {
        map ??= BounceBox.Map();
        RadWorld world = await BounceBox.LitAsync(map, settings);
        mutate?.Invoke(world);
        using WorkQueue queue = new(BounceBox.One);
        TransferSet transfers = await new VisMatrix(world.BounceContext()).BuildAsync(
            map.Tracer(), queue, CancellationToken.None);
        return (new Radiosity(world.BounceContext(), transfers), world, transfers);
    }

    private static void SetEmit(Radiosity r, RadWorld w) => r.MoveDirectLightToEmission();

    /// <summary>
    /// The first step moves each patch's direct light into its emission and
    /// zeroes its total.
    /// </summary>
    [Fact]
    public async Task TheDirectLightMovesIntoTheEmission()
    {
        (Radiosity r, RadWorld w, _) = await PrepareAsync();
        int j = BounceBox.Leaves(w, 0)[0];
        Vec3 direct = w.Patches.At(j).TotalLight.Flat;
        Assert.True(direct.X > 0);

        r.MoveDirectLightToEmission();

        Assert.Equal(direct, r.EmitLight[j]);
        Assert.Equal(Vec3.Zero, w.Patches.At(j).TotalLight.Flat);
    }

    /// <summary>
    /// A flat patch receives <c>sum(emit * reflectivity * transfer)</c> over
    /// its list, in list order.
    /// </summary>
    [Fact]
    public async Task AFlatPatchSumsEmitTimesReflectivityTimesTransfer()
    {
        (Radiosity r, RadWorld w, TransferSet t) = await PrepareAsync();
        SetEmit(r, w);
        int j = BounceBox.Leaves(w, 0)[0];
        r.GatherLight(j);

        Vec3 sum = Vec3.Zero;
        foreach (Transfer x in t.For(j))
        {
            Vec3 e = r.EmitLight[x.Patch];
            Vec3 refl = w.Patches.At(x.Patch).Reflectivity;
            sum += new Vec3(e.X * refl.X, e.Y * refl.Y, e.Z * refl.Z) * x.Weight;
        }

        Assert.Equal(sum, r.AddLight[j].Flat);
    }

    /// <summary>A flat patch's bump slots stay empty.</summary>
    [Fact]
    public async Task AFlatPatchLeavesTheBumpSlotsEmpty()
    {
        (Radiosity r, RadWorld w, _) = await PrepareAsync();
        SetEmit(r, w);
        int j = BounceBox.Leaves(w, 0)[0];
        r.GatherLight(j);
        Assert.Equal(Vec3.Zero, r.AddLight[j].Bump1);
    }

    /// <summary>
    /// A bumped patch divides out its own cosine and re-applies it per normal
    ///; for the flat slot, whose normal is the patch
    /// normal, that is the flat sum again, give or take rounding.
    /// </summary>
    [Fact]
    public async Task ABumpedPatchsFlatSlotIsTheFlatSum()
    {
        (Radiosity flat, RadWorld wf, _) = await PrepareAsync();
        SetEmit(flat, wf);
        int j = BounceBox.Leaves(wf, 0)[0];
        flat.GatherLight(j);

        (Radiosity bumped, RadWorld wb, _) = await PrepareAsync(mutate: BumpFloor);
        SetEmit(bumped, wb);
        await PrepareNormalsAsync(bumped, wb);
        bumped.GatherLight(j);

        Vec3 a = flat.AddLight[j].Flat;
        Vec3 b = bumped.AddLight[j].Flat;
        Assert.Equal(a.X, b.X, a.X * 1e-4f);
    }

    /// <summary>A bumped patch fills its three bump slots.</summary>
    [Fact]
    public async Task ABumpedPatchFillsItsBumpSlots()
    {
        (Radiosity r, RadWorld w, _) = await PrepareAsync(mutate: BumpFloor);
        SetEmit(r, w);
        await PrepareNormalsAsync(r, w);
        int j = BounceBox.Leaves(w, 0)[0];
        r.GatherLight(j);
        Assert.True(r.AddLight[j].Bump1.X > 0 || r.AddLight[j].Bump2.X > 0 || r.AddLight[j].Bump3.X > 0);
    }

    /// <summary>
    /// CollectLight adds a leaf's received light to its total and makes it the
    /// next emission.
    /// </summary>
    [Fact]
    public async Task ALeafsReceivedLightBecomesItsEmission()
    {
        (Radiosity r, RadWorld w, _) = await PrepareAsync();
        SetEmit(r, w);
        int j = BounceBox.Leaves(w, 0)[0];
        r.GatherLight(j);
        Vec3 received = r.AddLight[j].Flat;
        Vec3 before = w.Patches.At(j).TotalLight.Flat;

        r.CollectLight();

        Assert.Equal(received, r.EmitLight[j]);
        Assert.Equal(before + received, w.Patches.At(j).TotalLight.Flat);
    }

    /// <summary>CollectLight's return is the sum of every leaf's new emission, in reverse patch order.</summary>
    [Fact]
    public async Task TheBouncesTotalIsTheLeavesEmissionInReverseOrder()
    {
        (Radiosity r, RadWorld w, _) = await PrepareAsync();
        SetEmit(r, w);
        for (int j = 0; j < w.Patches.Count; j++)
        {
            r.GatherLight(j);
        }

        Vec3 expected = Vec3.Zero;
        for (int i = w.Patches.Count - 1; i >= 0; i--)
        {
            ref Patch p = ref w.Patches.At(i);
            if (!p.Sky && p.Child1 == Patch.Invalid)
            {
                expected += r.AddLight[i].Flat;
            }
        }

        Assert.Equal(expected, r.CollectLight());
    }

    /// <summary>A parent's light is its children's, weighted by area.</summary>
    [Fact]
    public async Task AParentIsItsChildrenAreaWeighted()
    {
        // The box splits every patch into equal halves, where swapped weights
        // would still pass; make every first child three times its sibling.
        (Radiosity r, RadWorld w, _) = await PrepareAsync(mutate: UnevenChildren);
        SetEmit(r, w);
        for (int j = 0; j < w.Patches.Count; j++)
        {
            r.GatherLight(j);
        }

        r.CollectLight();

        int parent = Enumerable.Range(0, w.Patches.Count).First(i => w.Patches.At(i).Child1 != Patch.Invalid);
        ref Patch p = ref w.Patches.At(parent);
        ref Patch c1 = ref w.Patches.At(p.Child1);
        ref Patch c2 = ref w.Patches.At(p.Child2);
        float s1 = c1.Area / (c1.Area + c2.Area);
        float s2 = c2.Area / (c1.Area + c2.Area);
        Assert.Equal((c1.TotalLight.Flat * s1) + (c2.TotalLight.Flat * s2), p.TotalLight.Flat);
    }

    /// <summary>A sky patch emits nothing: "sky's never collect light, it is just dropped".</summary>
    [Fact]
    public async Task ASkyPatchEmitsNothing()
    {
        (Radiosity r, RadWorld w, _) = await PrepareAsync(BounceBox.Map(ceiling: SurfaceFlags.Sky));
        SetEmit(r, w);
        for (int j = 0; j < w.Patches.Count; j++)
        {
            r.GatherLight(j);
        }

        r.CollectLight();
        int sky = Enumerable.Range(0, w.Patches.Count).First(i => w.Patches.At(i).Sky);
        Assert.Equal(Vec3.Zero, r.EmitLight[sky]);
    }

    /// <summary>CollectLight clears what was received.</summary>
    [Fact]
    public async Task CollectLightClearsTheReceivedLight()
    {
        (Radiosity r, RadWorld w, _) = await PrepareAsync();
        SetEmit(r, w);
        int j = BounceBox.Leaves(w, 0)[0];
        r.GatherLight(j);
        r.CollectLight();
        Assert.Equal(default, r.AddLight[j]);
    }

    /// <summary>The loop stops after <c>numbounce</c> bounces.</summary>
    [Fact]
    public async Task TheBounceStopsAtTheBounceCount()
    {
        (Radiosity r, _, _) = await PrepareAsync(settings: LightBox.Settings() with { Bounces = 3 });
        using WorkQueue queue = new(BounceBox.One);
        Assert.Equal(3, (await r.BounceAsync(queue, CancellationToken.None)).Count);
    }

    /// <summary>
    /// The loop stops after the first bounce that adds under 1 in all three
    /// channels, that bounce included.
    /// </summary>
    [Fact]
    public async Task TheBounceStopsWhenABounceAddsUnderOne()
    {
        (Radiosity r, _, _) = await PrepareAsync();
        using WorkQueue queue = new(BounceBox.One);
        IReadOnlyList<Vec3> added = await r.BounceAsync(queue, CancellationToken.None);

        Vec3 last = added[^1];
        Assert.True(last.X < 1 && last.Y < 1 && last.Z < 1);
        Assert.All(added.Take(added.Count - 1), e => Assert.True(e.X >= 1 || e.Y >= 1 || e.Z >= 1));
    }

    /// <summary>
    /// After the bounce a patch's total holds BOUNCED light only: the direct
    /// light was moved out first.
    /// </summary>
    [Fact]
    public async Task TheTotalHoldsOnlyBouncedLight()
    {
        (Radiosity r, RadWorld w, _) = await PrepareAsync(settings: LightBox.Settings() with { Bounces = 1 });
        int j = BounceBox.Leaves(w, 0)[0];
        using WorkQueue queue = new(BounceBox.One);
        await r.BounceAsync(queue, CancellationToken.None);

        // One bounce: the total is what the patch received, and it received
        // exactly the new emission.
        Assert.Equal(r.EmitLight[j], w.Patches.At(j).TotalLight.Flat);
    }

    /// <summary>No bounces asked for, no bounce.</summary>
    [Fact]
    public async Task ZeroBouncesIsNoBounce()
    {
        (Radiosity r, _, _) = await PrepareAsync(settings: LightBox.Settings() with { Bounces = 0 });
        using WorkQueue queue = new(BounceBox.One);
        Assert.Empty(await r.BounceAsync(queue, CancellationToken.None));
    }

    /// <summary>
    /// <c>PreGetBumpNormalsForDisp</c>: texture and lightmap axes that agree
    /// are used as they are, normalised.
    /// </summary>
    [Fact]
    public void AlignedDisplacementAxesAreUsedAsTheyAre()
    {
        TexInfo tex = Axes(new(2, 0, 0), new(0, -4, 0), new(0.5f, 0, 0), new(0, -0.5f, 0));
        Vec3 normal = new(0, 0, 1);
        (Vec3 u, Vec3 v) = Radiosity.PreGetBumpNormalsForDisp(in tex, ref normal, false);
        Assert.Equal((new Vec3(1, 0, 0), new Vec3(0, -1, 0), new Vec3(0, 0, 1)), (u, v, normal));
    }

    /// <summary>
    /// Axes more than <c>acos(0.999)</c> apart are re-expressed through
    /// <c>ConcatTransforms(light, tex)</c>: column 0 is light * texU.
    /// </summary>
    [Fact]
    public void MisalignedDisplacementAxesAreConverted()
    {
        // Texture axes rotated 90 degrees against the lightmap's.
        TexInfo tex = Axes(new(0, 1, 0), new(-1, 0, 0), new(1, 0, 0), new(0, 1, 0));
        Vec3 normal = new(0, 0, 1);
        (Vec3 u, Vec3 v) = Radiosity.PreGetBumpNormalsForDisp(in tex, ref normal, false);

        // Columns of [lightU lightV n] = identity, times texU, texV, n.
        Assert.Equal((new Vec3(0, 1, 0), new Vec3(-1, 0, 0), new Vec3(0, 0, 1)), (u, v, normal));
    }

    private static TexInfo Axes(Vec3 texU, Vec3 texV, Vec3 lightU, Vec3 lightV)
    {
        TexInfo tex = default;
        tex.TextureVecsTexelsPerWorldUnits[0] = texU.X;
        tex.TextureVecsTexelsPerWorldUnits[1] = texU.Y;
        tex.TextureVecsTexelsPerWorldUnits[2] = texU.Z;
        tex.TextureVecsTexelsPerWorldUnits[4] = texV.X;
        tex.TextureVecsTexelsPerWorldUnits[5] = texV.Y;
        tex.TextureVecsTexelsPerWorldUnits[6] = texV.Z;
        tex.LightmapVecsLuxelsPerWorldUnits[0] = lightU.X;
        tex.LightmapVecsLuxelsPerWorldUnits[1] = lightU.Y;
        tex.LightmapVecsLuxelsPerWorldUnits[2] = lightU.Z;
        tex.LightmapVecsLuxelsPerWorldUnits[4] = lightV.X;
        tex.LightmapVecsLuxelsPerWorldUnits[5] = lightV.Y;
        tex.LightmapVecsLuxelsPerWorldUnits[6] = lightV.Z;
        return tex;
    }

    private static void UnevenChildren(RadWorld w)
    {
        for (int i = 0; i < w.Patches.Count; i++)
        {
            if (w.Patches.At(i).Child1 != Patch.Invalid)
            {
                w.Patches.At(w.Patches.At(i).Child1).Area *= 3;
            }
        }
    }

    private static void BumpFloor(RadWorld w)
    {
        for (int p = w.Patches.FacePatches[0]; p != Patch.Invalid; p = w.Patches.At(p).Next)
        {
            w.Patches.At(p).NeedsBumpmap = true;
        }
    }

    private static async Task PrepareNormalsAsync(Radiosity r, RadWorld w)
    {
        using WorkQueue queue = new(BounceBox.One);
        await queue.RunAsync(w.Patches.Count, (i, _) => r.PrepareNormals(i), null, CancellationToken.None);
    }
}
