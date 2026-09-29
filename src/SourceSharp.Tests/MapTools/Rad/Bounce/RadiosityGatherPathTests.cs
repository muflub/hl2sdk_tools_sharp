//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Bounce;

/// <summary>
/// The gather's two paths for a bumped receiver -- one transfer at a time,
/// and four transfers' geometry at once on the exact side -- give the same
/// bits, and the dense per-patch arrays the gather reads follow the
/// emission from bounce to bounce.
/// </summary>
/// <remarks>
/// Results are compared as raw float bits: <see cref="Vec3"/> equality is
/// <see cref="float.Equals(float)"/>, which calls -0 and +0 equal.
/// </remarks>
public sealed class RadiosityGatherPathTests
{
    private static async Task<(RadWorld World, TransferSet Transfers)> PrepareAsync(bool stock = false)
    {
        LightTestMap map = BounceBox.Map();
        RadWorld world = await BounceBox.LitAsync(map, LightBox.Settings(stock));

        // Every face bumped, so every receiver takes the bumped gather.
        for (int p = 0; p < world.Patches.Count; p++)
        {
            world.Patches.At(p).NeedsBumpmap = true;
        }

        using WorkQueue queue = new(BounceBox.One);
        TransferSet transfers = await new VisMatrix(world.BounceContext()).BuildAsync(
            map.Tracer(), queue, CancellationToken.None);
        return (world, transfers);
    }

    private static async Task<Radiosity> ReadyAsync(RadWorld world, TransferSet transfers, bool scalar)
    {
        Radiosity r = new(world.BounceContext(), transfers) { ScalarBumpedGather = scalar };
        using WorkQueue queue = new(BounceBox.One);
        await queue.RunAsync(world.Patches.Count, (i, _) => r.PrepareNormals(i), null, CancellationToken.None);
        r.MoveDirectLightToEmission();
        return r;
    }

    private static int[] Bits(Vec3 v) =>
        [BitConverter.SingleToInt32Bits(v.X), BitConverter.SingleToInt32Bits(v.Y), BitConverter.SingleToInt32Bits(v.Z)];

    private static int[] Bits(BumpLights b) => [.. Bits(b.Flat), .. Bits(b.Bump1), .. Bits(b.Bump2), .. Bits(b.Bump3)];

    private static void AssertSameAddLight(Radiosity expected, Radiosity actual, int count)
    {
        for (int j = 0; j < count; j++)
        {
            Assert.Equal(Bits(expected.AddLight[j]), Bits(actual.AddLight[j]));
        }
    }

    [Fact]
    public async Task FourTransfersAtOnceMatchOneAtATimeBitForBit()
    {
        (RadWorld ws, TransferSet ts) = await PrepareAsync();
        (RadWorld wf, TransferSet tf) = await PrepareAsync();
        Radiosity scalar = await ReadyAsync(ws, ts, scalar: true);
        Radiosity four = await ReadyAsync(wf, tf, scalar: false);

        // Lists of every length mod 4 must occur, or the short last block is
        // not exercised.
        HashSet<int> tails = [];
        int count = ws.Patches.Count;
        for (int bounce = 0; bounce < 3; bounce++)
        {
            for (int j = 0; j < count; j++)
            {
                scalar.GatherLight(j);
                four.GatherLight(j);
                tails.Add(ts.CountFor(j) % 4);
            }

            AssertSameAddLight(scalar, four, count);
            Assert.Equal(Bits(scalar.CollectLight()), Bits(four.CollectLight()));
        }

        Assert.Equal(4, tails.Count);
    }

    [Fact]
    public async Task AZeroLengthDirectionIsTheZeroVectorOnBothPaths()
    {
        // A shooter at the receiver's own origin: Vec3.Normalise answers the
        // zero vector, so the removed cosine is 1/0 and every dot is 0, which
        // the accumulation skips. The four-wide path must not divide 0 by 0.
        (RadWorld ws, TransferSet ts) = await PrepareAsync();
        (RadWorld wf, TransferSet tf) = await PrepareAsync();
        int j = Enumerable.Range(0, ws.Patches.Count).First(p => ts.CountFor(p) >= 6);
        int shooter = ts.For(j)[2].Patch;
        ws.Patches.At(shooter).Origin = ws.Patches.At(j).Origin;
        wf.Patches.At(shooter).Origin = wf.Patches.At(j).Origin;

        Radiosity scalar = await ReadyAsync(ws, ts, scalar: true);
        Radiosity four = await ReadyAsync(wf, tf, scalar: false);
        scalar.GatherLight(j);
        four.GatherLight(j);

        Assert.Equal(Bits(scalar.AddLight[j]), Bits(four.AddLight[j]));
        Assert.All(Bits(four.AddLight[j]), b => Assert.False(float.IsNaN(BitConverter.Int32BitsToSingle(b))));
    }

    [Fact]
    public async Task TheStockNormaliseKeepsTheScalarPath()
    {
        // Stock's normalise is an estimate; the four-wide path divides
        // exactly, so taking it under stock would move the last bits.
        if (!FloatEstimate.IsSupported)
        {
            return;
        }

        (RadWorld ws, TransferSet ts) = await PrepareAsync(stock: true);
        (RadWorld wf, TransferSet tf) = await PrepareAsync(stock: true);
        Radiosity scalar = await ReadyAsync(ws, ts, scalar: true);
        Radiosity other = await ReadyAsync(wf, tf, scalar: false);
        for (int j = 0; j < ws.Patches.Count; j++)
        {
            scalar.GatherLight(j);
            other.GatherLight(j);
        }

        AssertSameAddLight(scalar, other, ws.Patches.Count);
    }

    [Fact]
    public async Task EachBounceGathersTheEmissionTheLastOneCollected()
    {
        // The gather reads each shooter's emit * reflectivity from a dense
        // array formed where the emission is written. After a CollectLight it
        // must be the new emission's product, not the direct light's.
        LightTestMap map = BounceBox.Map();
        RadWorld w = await BounceBox.LitAsync(map);
        using WorkQueue queue = new(BounceBox.One);
        TransferSet t = await new VisMatrix(w.BounceContext()).BuildAsync(map.Tracer(), queue, CancellationToken.None);
        Radiosity r = new(w.BounceContext(), t);
        r.MoveDirectLightToEmission();
        for (int j = 0; j < w.Patches.Count; j++)
        {
            r.GatherLight(j);
        }

        r.CollectLight();
        int receiver = BounceBox.Leaves(w, 0)[0];
        r.GatherLight(receiver);

        Vec3 sum = Vec3.Zero;
        foreach (Transfer x in t.For(receiver))
        {
            Vec3 e = r.EmitLight[x.Patch];
            Vec3 refl = w.Patches.At(x.Patch).Reflectivity;
            sum += new Vec3(e.X * refl.X, e.Y * refl.Y, e.Z * refl.Z) * x.Weight;
        }

        Assert.True(sum.X > 0);
        Assert.Equal(Bits(sum), Bits(r.AddLight[receiver].Flat));
    }
}
