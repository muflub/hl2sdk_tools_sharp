using System.Runtime.InteropServices;
using System.Security.Cryptography;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Light;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rad.Light;

/// <summary>
/// The 4c gates on <c>BuildFacelights</c> and <c>PrecompLightmapOffsets</c>:
/// the lightmap LAYOUT stock writes, determinism across thread counts, and a
/// direct-light comparison against stock's <c>-bounce 0</c> lightmaps.
/// </summary>
/// <remarks>
/// <para>
/// The layout is exact: each face's four style bytes and its <c>lightofs</c>
/// are in stock's FACES lump, and the LIGHTING lump's length is
/// <c>PrecompLightmapOffsets</c>' total. A style slot is allocated only when a
/// light of that style actually reaches a sample of the face
/// (<c>lightmap.cpp:2529</c>), so the style bytes gate the PVS test, the cone
/// and falloff early-outs, and the visibility rays together.
/// </para>
/// <para>
/// The luxel VALUES cannot be exact: stock's gather uses reciprocal and
/// reciprocal-square-root estimates throughout and its lightmaps go through the
/// radial filter (4f), which this lane does not own. The direct-light
/// comparison is therefore per FACE -- the average of stock's decoded style-0
/// luxels against the area-weighted average of this port's style-0 samples --
/// with a measured, stated tolerance.
/// </para>
/// </remarks>
public sealed class RadWorldLightingGateTests(ITestOutputHelper output)
{
    public static TheoryData<string> Maps() => RadWorldStockGateTests.Maps();

    [StockVradTheory]
    [MemberData(nameof(Maps))]
    public async Task FaceStylesMatchStock(string name)
    {
        RadWorld world = await StockRadWorld.LightAsync(name, hdr: false);
        DFace[] stock = StockFaces(await StockOutputAsync(name), BspLump.Faces);

        List<string> differences = [];
        for (int f = 0; f < stock.Length; f++)
        {
            for (int k = 0; k < 4; k++)
            {
                byte s = stock[f].Styles[k];
                byte m = world.Layout!.Styles[(f * 4) + k];
                if (s != m)
                {
                    differences.Add($"face {f} slot {k}: stock {s} managed {m}");
                }
            }
        }

        Assert.True(differences.Count == 0, Summarise(differences));
    }

    [StockVradTheory]
    [MemberData(nameof(Maps))]
    public async Task LightOffsetsMatchStock(string name)
    {
        RadWorld world = await StockRadWorld.LightAsync(name, hdr: false);
        DFace[] stock = StockFaces(await StockOutputAsync(name), BspLump.Faces);

        List<string> differences = [];
        for (int f = 0; f < stock.Length; f++)
        {
            if (stock[f].LightOfs != world.Layout!.LightOffsets[f])
            {
                differences.Add($"face {f}: stock {stock[f].LightOfs} managed {world.Layout.LightOffsets[f]}");
            }
        }

        Assert.True(differences.Count == 0, Summarise(differences));
    }

    [StockVradTheory]
    [MemberData(nameof(Maps))]
    public async Task LightingLumpSizeMatchesStock(string name)
    {
        RadWorld world = await StockRadWorld.LightAsync(name, hdr: false);
        BspData stock = await StockOutputAsync(name);
        Assert.Equal(stock[BspLump.Lighting].Length, world.Layout!.LightDataSize);
    }

    [StockVradPrivateTheory]
    [MemberData(nameof(Maps))]
    public async Task HdrLightingLumpSizeMatchesStock(string name)
    {
        RadWorld world = await StockRadWorld.LightAsync(name, hdr: true);
        BspData both = await StockRadWorld.LoadBspAsync(
            StockVradReference.BothOutputFor(name) ?? throw new InvalidOperationException(name));
        Assert.Equal(both[BspLump.LightingHdr].Length, world.Layout!.LightDataSize);
    }

    [StockVradPrivateTheory]
    [InlineData("l1_long_corridor")]
    [InlineData("l2_detail_and_hint_in_a_corridor")]
    [InlineData("p4c_texlights")]
    public async Task OneThreadAndManyThreadsProduceTheSameBytes(string name)
    {
        RadWorld one = await StockRadWorld.LightAsync(
            name, hdr: false, parallelism: new CompileParallelism { MaxDegree = 1 });
        RadWorld many = await StockRadWorld.LightAsync(
            name, hdr: false, parallelism: new CompileParallelism { MaxDegree = 8 });

        Assert.Equal(Digest(one), Digest(many));
    }

    [StockVradPrivateTheory]
    [MemberData(nameof(Maps))]
    public async Task DirectLightAgreesWithStockBounceZeroOnFlatLuxels(string name)
    {
        string? b0 = StockVradReference.DirectOnlyOutputFor(name);
        Assert.NotNull(b0);
        BspData stock = await StockRadWorld.LoadBspAsync(b0);
        RadWorld world = await StockRadWorld.LightAsync(
            name, hdr: false, options: SourceSharp.MapTools.Options.VradOptions.Default with { Bounces = 0 });

        List<double> errors = CompareFlatLuxels(stock, world, out int dark, out int exact);
        errors.Sort();
        int n = errors.Count;
        double median = n > 0 ? errors[n / 2] : 0;
        double p99 = n > 0 ? errors[Math.Min(n - 1, (int)(n * 0.99))] : 0;
        double over2 = n > 0 ? errors.Count(e => e > 0.02) / (double)n : 0;
        output.WriteLine(
            $"{name}: {n} flat lit luxels ({dark} dark on both sides), relative error "
            + $"median {median:P3}, p99 {p99:P3}, over 2%: {over2:P3}; "
            + $"byte-identical once encoded: {exact}");

        // Measured on the whole corpus (p4c-findings.md). The error is taken
        // AFTER allowing for ColorRGBExp32's truncated mantissas, so what is
        // left is stock's reciprocal and rsqrt ESTIMATES against this port's
        // exact arithmetic.
        Assert.True(median < 0.005, $"median luxel error {median:P3}");
        Assert.True(over2 < 0.01, $"{over2:P3} of flat luxels are more than 2% off");
    }

    /// <summary>
    /// Style-0 flat-lightmap luxels where stock's lightmap is FLAT -- the luxel
    /// and its eight neighbours within 1% -- against this port's sample in the
    /// same cell. On a flat patch of lightmap the radial filter (4f, not this
    /// lane) returns the sample it was given, so the comparison sees the gather
    /// alone; at a shadow edge it blends, and those luxels are excluded.
    /// </summary>
    private static List<double> CompareFlatLuxels(BspData stock, RadWorld world, out int dark, out int exact)
    {
        exact = 0;
        DFace[] faces = StockFaces(stock, BspLump.Faces);
        ReadOnlySpan<byte> lighting = stock[BspLump.Lighting].Data.Span;
        List<double> errors = [];
        dark = 0;

        for (int f = 0; f < faces.Length; f++)
        {
            FaceLight? fl = world.FaceLights[f];
            if (fl is null || fl.IsDisplacementDeferred || faces[f].LightOfs < 0 || faces[f].Styles[0] != 0)
            {
                continue;
            }

            int w = faces[f].LightmapTextureSizeInLuxels[0] + 1;
            int h = faces[f].LightmapTextureSizeInLuxels[1] + 1;
            double[] s = new double[w * h];
            double[] quantum = new double[w * h];
            for (int j = 0; j < w * h; j++)
            {
                int at = faces[f].LightOfs + (j * 4);
                double scale = Math.Pow(2.0, (sbyte)lighting[at + 3]);
                s[j] = (lighting[at] + lighting[at + 1] + lighting[at + 2]) * scale;

                // VectorToColorRGBExp32 TRUNCATES each channel's mantissa
                // (color_conversion.cpp:524-526): up to one step low, per channel.
                quantum[j] = 3 * scale;
            }

            double?[] m = new double?[w * h];
            Vec3[] mv = new Vec3[w * h];
            LightingValue[] light = fl.LightFor(0, 0)!;
            for (int i = 0; i < fl.Samples.Length; i++)
            {
                LightSample sample = fl.Samples[i];
                m[sample.S + (sample.T * w)] = light[i].Intensity();
                mv[sample.S + (sample.T * w)] = light[i].Lighting;
            }

            for (int t = 1; t < h - 1; t++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int j = x + (t * w);
                    if (m[j] is not double mine)
                    {
                        continue;
                    }

                    double v = s[j];
                    bool flat = true;
                    for (int dt = -1; dt <= 1 && flat; dt++)
                    {
                        for (int dx = -1; dx <= 1 && flat; dx++)
                        {
                            flat = Math.Abs(s[j + dx + (dt * w)] - v) <= 0.01 * Math.Max(v, 1.0);
                        }
                    }

                    if (!flat)
                    {
                        continue;
                    }

                    if (v < 1.0 && mine < 1.0)
                    {
                        dark++;
                        continue;
                    }

                    errors.Add(Math.Max(0.0, Math.Abs(mine - v) - quantum[j]) / Math.Max(v, 1.0));
                    int at = faces[f].LightOfs + (j * 4);
                    if (Encode(mv[j]) == BitConverter.ToUInt32(lighting.Slice(at, 4)))
                    {
                        exact++;
                    }
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// <c>VectorToColorRGBExp32</c> (<c>color_conversion.cpp:566</c>), for
    /// comparing bytes. 4f owns the real encoder; this is the test's copy.
    /// </summary>
    private static uint Encode(Vec3 v)
    {
        float max = v.X > v.Y ? (v.X > v.Z ? v.X : v.Z) : (v.Y > v.Z ? v.Y : v.Z);
        int exponent = max == 0.0f ? 0 : (int)((BitConverter.SingleToUInt32Bits(max) & 0x7F800000) >> 23) - (7 + 127);
        float scalar = BitConverter.UInt32BitsToSingle((uint)(127 - exponent) << 23);
        byte r = (byte)(int)(v.X * scalar);
        byte g = (byte)(int)(v.Y * scalar);
        byte b = (byte)(int)(v.Z * scalar);
        return r | ((uint)g << 8) | ((uint)b << 16) | ((uint)(byte)(sbyte)exponent << 24);
    }

    private static async Task<BspData> StockOutputAsync(string name) =>
        await StockRadWorld.LoadBspAsync(
            StockVradReference.StockOutputFor(name) ?? throw new InvalidOperationException(name));

    private static DFace[] StockFaces(BspData bsp, BspLump lump) =>
        MemoryMarshal.Cast<byte, DFace>(bsp[lump].Data.Span).ToArray();

    private static string Summarise(List<string> differences) =>
        $"{differences.Count} differences; first: " + string.Join("; ", differences.Take(12));

    private static string Digest(RadWorld world)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (FaceLight? fl in world.FaceLights)
        {
            if (fl is null)
            {
                hash.AppendData([0]);
                continue;
            }

            hash.AppendData(MemoryMarshal.AsBytes(fl.Samples.AsSpan()));
            hash.AppendData(MemoryMarshal.AsBytes(fl.Luxels.AsSpan()));
            hash.AppendData(fl.Styles);
            foreach (LightingValue[]? values in fl.Light)
            {
                if (values is not null)
                {
                    hash.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));
                }
            }
        }

        foreach (ref readonly var patch in world.Patches.AsSpan())
        {
            Vec3[] v = [patch.DirectLight, patch.TotalLight.Flat, patch.SampleLight];
            hash.AppendData(MemoryMarshal.AsBytes(v.AsSpan()));
            hash.AppendData(BitConverter.GetBytes(patch.SampleArea));
        }

        hash.AppendData(world.Layout!.Styles);
        hash.AppendData(MemoryMarshal.AsBytes(world.Layout.LightOffsets.AsSpan()));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
