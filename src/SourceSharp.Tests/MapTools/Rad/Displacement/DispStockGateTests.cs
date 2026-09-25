using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Displacement;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rad.Displacement;

/// <summary>
/// The 4e gates: displacement patches, samples, luxels and direct light
/// against stock vrad on the 18 p3f-t displacement maps
/// (<see cref="StockDispVrad"/>), all under <c>ComplianceOptions.Stock</c>.
/// </summary>
/// <remarks>
/// <para>
/// Counts are exact: "N Displacements", the displacement "Square Feet
/// [Square Inches]" line (which carries stock's
/// estimate-normalised patch areas to two decimals), and the patch counts
/// before and after subdivision, which move with every chop and sliver test in
/// <c>CreateChildPatches</c>. The layout (styles, lightofs, LIGHTING size) is
/// exact for EVERY face. Direct light is compared on every displacement luxel
/// against stock's <c>-bounce 0</c> output through the ported luxel radial,
/// and under <c>-fast</c> sample for luxel.
/// </para>
/// </remarks>
public sealed class DispStockGateTests(ITestOutputHelper output)
{
    public static TheoryData<string> Maps() => StockDispVrad.Maps();

    [StockDispTheory]
    [MemberData(nameof(Maps))]
    public async Task DisplacementCountMatchesStock(string name)
    {
        RadWorld world = await StockDispVrad.LightAsync(name);
        Assert.Equal(StockDispVrad.ReadLog("rad", name).Displacements, world.Statistics.Displacements);
    }

    [StockDispTheory]
    [MemberData(nameof(Maps))]
    public async Task DisplacementSquareInchesMatchStock(string name)
    {
        RadWorld world = await StockDispVrad.LightAsync(name);
        StockDispLog log = StockDispVrad.ReadLog("rad", name);
        float area = world.Statistics.DisplacementArea;
        Assert.Equal(log.SquareInches, area.ToString("F2", CultureInfo.InvariantCulture));
        Assert.Equal(log.SquareFeet, (int)(area / 144.0f));
    }

    [StockDispTheory]
    [MemberData(nameof(Maps))]
    public async Task PatchesBeforeSubdivisionMatchStock(string name)
    {
        RadWorld world = await StockDispVrad.LightAsync(name);
        Assert.Equal(StockDispVrad.ReadLog("rad", name).PatchesBefore, world.Statistics.Subdivision.PatchesBefore);
    }

    [StockDispTheory]
    [MemberData(nameof(Maps))]
    public async Task PatchesAfterSubdivisionMatchStock(string name)
    {
        RadWorld world = await StockDispVrad.LightAsync(name);
        Assert.Equal(StockDispVrad.ReadLog("rad", name).PatchesAfter, world.Statistics.Subdivision.PatchesAfter);
    }

    [StockDispTheory]
    [MemberData(nameof(Maps))]
    public async Task EveryDisplacementFaceIsLit(string name)
    {
        RadWorld world = await StockDispVrad.LightAsync(name);
        Assert.Equal(0, world.Statistics.DeferredDisplacementFaces);
        int dispFaces = world.Geometry.Faces.Count(f => f.DispInfo != -1);
        Assert.Equal(dispFaces, world.FaceLights.Count(fl => fl is not null && world.Geometry.Faces[fl.FaceNum].DispInfo != -1));
    }

    [StockDispTheory]
    [MemberData(nameof(Maps))]
    public async Task DisplacementLuxelAndSampleCountsMatchTheLightmap(string name)
    {
        // Stock prints no sample count; what it writes is the luxel grid
        // (w x h), and a displacement has exactly one sample per luxel.
        RadWorld world = await StockDispVrad.LightAsync(name);
        DFace[] stock = StockDispVrad.Faces(await StockRadWorld.LoadBspAsync(StockDispVrad.Output("rad", name)));
        foreach (FaceLight? fl in world.FaceLights)
        {
            if (fl is null || world.Geometry.Faces[fl.FaceNum].DispInfo == -1)
            {
                continue;
            }

            DFace f = stock[fl.FaceNum];
            int luxels = (f.LightmapTextureSizeInLuxels[0] + 1) * (f.LightmapTextureSizeInLuxels[1] + 1);
            Assert.Equal(luxels, fl.Luxels.Length);
            Assert.Equal(luxels, fl.LuxelNormals.Length);
            Assert.Equal(luxels, fl.Samples.Length);
        }
    }

    [StockDispTheory]
    [MemberData(nameof(Maps))]
    public async Task FaceStylesMatchStock(string name)
    {
        RadWorld world = await StockDispVrad.LightAsync(name);
        DFace[] stock = StockDispVrad.Faces(await StockRadWorld.LoadBspAsync(StockDispVrad.Output("rad", name)));
        List<string> diffs = [];
        for (int f = 0; f < stock.Length; f++)
        {
            for (int k = 0; k < 4; k++)
            {
                if (stock[f].Styles[k] != world.Layout!.Styles[(f * 4) + k])
                {
                    diffs.Add($"face {f} slot {k}: stock {stock[f].Styles[k]} managed {world.Layout.Styles[(f * 4) + k]}");
                }
            }
        }

        Assert.True(diffs.Count == 0, string.Join("; ", diffs.Take(12)));
    }

    [StockDispTheory]
    [MemberData(nameof(Maps))]
    public async Task LightOffsetsMatchStock(string name)
    {
        RadWorld world = await StockDispVrad.LightAsync(name);
        DFace[] stock = StockDispVrad.Faces(await StockRadWorld.LoadBspAsync(StockDispVrad.Output("rad", name)));
        Assert.Equal(stock.Select(f => f.LightOfs).ToArray(), world.Layout!.LightOffsets);
    }

    [StockDispTheory]
    [MemberData(nameof(Maps))]
    public async Task LightingLumpSizeMatchesStock(string name)
    {
        RadWorld world = await StockDispVrad.LightAsync(name);
        BspData stock = await StockRadWorld.LoadBspAsync(StockDispVrad.Output("rad", name));
        Assert.Equal(stock[BspLump.Lighting].Length, world.Layout!.LightDataSize);
    }

    [StockDispTheory]
    [MemberData(nameof(Maps))]
    public async Task DirectLightAgreesWithStockBounceZeroOnDisplacementLuxels(string name)
    {
        RadWorld world = await StockDispVrad.LightAsync(name, VradOptions.Default with { Bounces = 0 });
        BspData stock = await StockRadWorld.LoadBspAsync(StockDispVrad.Output("b0", name));
        (List<double> errors, int exact, int dark) = CompareRadialLuxels(stock, world);

        errors.Sort();
        int n = errors.Count;
        double median = n > 0 ? errors[n / 2] : 0;
        double over2 = n > 0 ? errors.Count(e => e > 0.02) / (double)n : 0;
        double worst = n > 0 ? errors[^1] : 0;
        output.WriteLine(
            $"{name}: {n} lit displacement luxels ({dark} dark on both sides), median {median:P3}, "
            + $"over 2%: {over2:P3}, worst {worst:P3}; byte-identical once encoded: {exact}");

        Assert.True(n > 0, "no displacement luxel was compared");
        Assert.True(median < 0.005, $"median luxel error {median:P3}");
        Assert.True(over2 < 0.01, $"{over2:P3} of displacement luxels are more than 2% off");
    }

    [StockDispTheory]
    [MemberData(nameof(Maps))]
    public async Task FastDirectLightAgreesWithStockOnDisplacementLuxels(string name)
    {
        // -fast: FinalLightFace copies the sample straight into the luxel
        //, so this compares the gather itself, the black
        // last row and column (StockQuirk.DispFastSamplesPastEdge) included.
        RadWorld world = await StockDispVrad.LightAsync(name, VradOptions.Default with { Bounces = 0, Fast = true });
        BspData stock = await StockRadWorld.LoadBspAsync(StockDispVrad.Output("fastb0", name));
        (List<double> errors, int exact, int dark, int zeroBoth, int zeroStockOnly) = CompareFastLuxels(stock, world);

        errors.Sort();
        int n = errors.Count;
        double median = n > 0 ? errors[n / 2] : 0;
        double over2 = n > 0 ? errors.Count(e => e > 0.02) / (double)n : 0;
        output.WriteLine(
            $"{name}: {n} lit luxels, median {median:P3}, over 2%: {over2:P3}; exact {exact}; "
            + $"black in both {zeroBoth}, black only in stock {zeroStockOnly}, dark {dark}");

        Assert.Equal(0, zeroStockOnly);
        Assert.True(zeroBoth > 0, "stock's black edge was not reproduced");
        Assert.True(median < 0.005, $"median luxel error {median:P3}");
        Assert.True(over2 < 0.01, $"{over2:P3} of luxels are more than 2% off");
    }

    [StockDispFact]
    public async Task OneThreadAndEightThreadsProduceTheSameDisplacementBytes()
    {
        foreach (string name in (string[])["p3f_grid_mixed", "p3f_p3_bump", "p3f_p4_random"])
        {
            RadWorld one = await StockDispVrad.LightAsync(name, parallelism: new CompileParallelism { MaxDegree = 1 });
            RadWorld many = await StockDispVrad.LightAsync(name, parallelism: new CompileParallelism { MaxDegree = 8 });
            Assert.Equal(Digest(one), Digest(many));
        }
    }

    [StockDispFact]
    public async Task CorrectFastModeHasNoBlackEdge()
    {
        RadWorld world = await StockDispVrad.LightAsync(
            "p3f_p2_flat", VradOptions.Default with { Bounces = 0, Fast = true }, compliance: ComplianceOptions.Correct);
        FaceLight fl = world.FaceLights.First(f => f is not null && world.Geometry.Faces[f.FaceNum].DispInfo != -1)!;
        DFace face = world.Geometry.Faces[fl.FaceNum];
        int w = face.LightmapTextureSizeInLuxels[0] + 1;
        int h = face.LightmapTextureSizeInLuxels[1] + 1;
        LightingValue[] light = fl.LightFor(0, 0)!;
        for (int t = 0; t < h; t++)
        {
            Assert.True(light[(t * w) + w - 1].Intensity() > 0.0f, $"last column row {t} is black");
        }

        for (int s = 0; s < w; s++)
        {
            Assert.True(light[((h - 1) * w) + s].Intensity() > 0.0f, $"last row column {s} is black");
        }
    }

    private static (List<double> Errors, int Exact, int Dark) CompareRadialLuxels(BspData stock, RadWorld world)
    {
        DFace[] faces = StockDispVrad.Faces(stock);
        ReadOnlySpan<byte> lighting = stock[BspLump.Lighting].Data.Span;
        DispRadialContext ctx = world.DisplacementRadialContext();
        List<double> errors = [];
        int exact = 0;
        int dark = 0;
        Span<LightingValue> lb = stackalloc LightingValue[4];

        for (int f = 0; f < faces.Length; f++)
        {
            FaceLight? fl = world.FaceLights[f];
            if (fl is null || faces[f].DispInfo == -1 || faces[f].LightOfs < 0 || faces[f].Styles[0] != 0)
            {
                continue;
            }

            bool bump = (world.Geometry.TexInfos[faces[f].TexInfo].Flags & (int)SurfaceFlags.BumpLight) != 0;
            int bumps = bump ? 4 : 1;
            DispRadialMap radial = DispRadial.BuildLuxelRadial(ctx, f, 0, bump);
            for (int j = 0; j < fl.Luxels.Length; j++)
            {
                if (!DispRadial.SampleRadial(radial, j, lb, bumps, patch: false))
                {
                    lb[0] = default;
                }

                //: style k's bump b is at lightofs + (k * bumps + b) * luxels * 4.
                for (int b = 0; b < bumps; b++)
                {
                    int at = faces[f].LightOfs + (b * fl.Luxels.Length * 4) + (j * 4);
                    Vec3 s = StockDispVrad.Decode(lighting, at);
                    double sv = s.X + s.Y + s.Z;
                    double mv = lb[b].Intensity();
                    double quantum = 3 * Math.Pow(2.0, (sbyte)lighting[at + 3]);
                    if (sv < 1.0 && mv < 1.0)
                    {
                        dark++;
                        continue;
                    }

                    errors.Add(Math.Max(0.0, Math.Abs(mv - sv) - quantum) / Math.Max(sv, 1.0));
                    if (StockDispVrad.Encode(lb[b].Lighting) == BitConverter.ToUInt32(lighting.Slice(at, 4)))
                    {
                        exact++;
                    }
                }
            }
        }

        return (errors, exact, dark);
    }

    private static (List<double> Errors, int Exact, int Dark, int ZeroBoth, int ZeroStockOnly) CompareFastLuxels(
        BspData stock, RadWorld world)
    {
        DFace[] faces = StockDispVrad.Faces(stock);
        ReadOnlySpan<byte> lighting = stock[BspLump.Lighting].Data.Span;
        List<double> errors = [];
        int exact = 0, dark = 0, zeroBoth = 0, zeroStockOnly = 0;

        for (int f = 0; f < faces.Length; f++)
        {
            FaceLight? fl = world.FaceLights[f];
            if (fl is null || faces[f].DispInfo == -1 || faces[f].LightOfs < 0 || faces[f].Styles[0] != 0)
            {
                continue;
            }

            for (int bump = 0; bump < fl.NormalCount; bump++)
            {
                LightingValue[] light = fl.LightFor(0, bump)!;
                for (int j = 0; j < light.Length; j++)
                {
                    int at = faces[f].LightOfs + (bump * light.Length * 4) + (j * 4);
                    Vec3 s = StockDispVrad.Decode(lighting, at);
                    double sv = s.X + s.Y + s.Z;
                    double mv = light[j].Intensity();
                    if (sv == 0.0)
                    {
                        if (mv == 0.0)
                        {
                            zeroBoth++;
                        }
                        else
                        {
                            zeroStockOnly++;
                        }

                        continue;
                    }

                    double quantum = 3 * Math.Pow(2.0, (sbyte)lighting[at + 3]);
                    if (sv < 1.0 && mv < 1.0)
                    {
                        dark++;
                        continue;
                    }

                    errors.Add(Math.Max(0.0, Math.Abs(mv - sv) - quantum) / Math.Max(sv, 1.0));
                    if (StockDispVrad.Encode(light[j].Lighting) == BitConverter.ToUInt32(lighting.Slice(at, 4)))
                    {
                        exact++;
                    }
                }
            }
        }

        return (errors, exact, dark, zeroBoth, zeroStockOnly);
    }

    internal static string Digest(RadWorld world)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        DispRadialContext ctx = world.DisplacementRadialContext();
        foreach (FaceLight? fl in world.FaceLights)
        {
            if (fl is null)
            {
                hash.AppendData([0]);
                continue;
            }

            hash.AppendData(MemoryMarshal.AsBytes(fl.Samples.AsSpan()));
            hash.AppendData(MemoryMarshal.AsBytes(fl.Luxels.AsSpan()));
            hash.AppendData(MemoryMarshal.AsBytes(fl.LuxelNormals.AsSpan()));
            hash.AppendData(fl.Styles);
            foreach (LightingValue[]? values in fl.Light)
            {
                if (values is not null)
                {
                    hash.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));
                }
            }

            if (world.Geometry.Faces[fl.FaceNum].DispInfo != -1 && fl.Styles[0] != 255)
            {
                DispRadialMap r = DispRadial.BuildLuxelRadial(ctx, fl.FaceNum, 0, fl.NormalCount > 1);
                hash.AppendData(MemoryMarshal.AsBytes(r.Weight.AsSpan()));
                foreach (LightingValue[] l in r.Light)
                {
                    hash.AppendData(MemoryMarshal.AsBytes(l.AsSpan()));
                }

                DispRadialMap pr = DispRadial.BuildPatchRadial(ctx, fl.FaceNum, fl.NormalCount > 1);
                hash.AppendData(MemoryMarshal.AsBytes(pr.Weight.AsSpan()));
            }
        }

        foreach (ref readonly Patch patch in world.Patches.AsSpan())
        {
            Vec3[] v = [patch.Origin, patch.Normal, patch.DirectLight, patch.TotalLight.Flat, patch.SampleLight];
            hash.AppendData(MemoryMarshal.AsBytes(v.AsSpan()));
            hash.AppendData(BitConverter.GetBytes(patch.SampleArea));
            hash.AppendData(BitConverter.GetBytes(patch.Area));
        }

        hash.AppendData(world.Layout!.Styles);
        hash.AppendData(MemoryMarshal.AsBytes(world.Layout.LightOffsets.AsSpan()));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
