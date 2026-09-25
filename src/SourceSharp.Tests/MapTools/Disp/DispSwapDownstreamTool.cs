using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// The downstream check for <see cref="StockQuirk.DispLightmapSwapDropped"/>:
/// writes a stock-compiled BSP twice through the same writer — once as stock
/// left it, once with every swapped displacement face repointed as Correct
/// does — so stock vrad can be run on both. Runs only when
/// <c>P3F_SWAP_PATCH_DIR</c> names a directory holding <c>in.bsp</c>; writes
/// <c>stock.bsp</c>, <c>correct.bsp</c> and <c>extents.txt</c> there.
/// </summary>
/// <remarks>
/// The extents are <c>CalcFaceExtents</c> over each
/// displacement face with its texinfo: what vrad's texinfo-based
/// world-to-luxel mapping implies, set against the
/// size the face stores (the displacement's own luxel grid). Correct is right
/// when the two agree.
/// </remarks>
public sealed class DispSwapDownstreamTool
{
    private const string DirVariable = "P3F_SWAP_PATCH_DIR";

    /// <summary>Writes the pair and the extents report.</summary>
    [SwapPatchFact]
    public async Task WriteStockAndCorrectBsps()
    {
        string dir = Environment.GetEnvironmentVariable(DirVariable)!;
        BspData stock;
        await using (FileStream s = File.OpenRead(Path.Combine(dir, "in.bsp")))
        {
            stock = await BspFile.LoadAsync(s);
        }

        await using (FileStream s = File.Create(Path.Combine(dir, "stock.bsp")))
        {
            await BspFile.SaveAsync(stock, s);
        }

        DFace[] faces = BspStructView.As<DFace>(stock[BspLump.Faces]).ToArray();
        List<TexInfo> texInfos = [.. BspStructView.As<TexInfo>(stock[BspLump.TexInfo]).ToArray()];
        StringBuilder report = new();

        List<int> dispFaces = [.. Enumerable.Range(0, faces.Length).Where(f => faces[f].DispInfo != -1)];
        foreach (int f in dispFaces)
        {
            // Stock's face texinfo IS the original (DispStockCoverageTests), so
            // Correct's copy is its swap; FaceTexInfos' dedup is one copy per original.
            TexInfo original = texInfos[faces[f].TexInfo];
            texInfos.Add(DisplacementLumpBuilder.SwapLightmapAxes(original));
            int swapped = texInfos.Count - 1;

            (int[] minsO, int[] sizeO) = Extents(stock, faces[f], original);
            (int[] minsS, int[] sizeS) = Extents(stock, faces[f], texInfos[swapped]);

            report.Append(CultureInfo.InvariantCulture,
                $"face {f} stored size {faces[f].LightmapTextureSizeInLuxels[0]}x{faces[f].LightmapTextureSizeInLuxels[1]} "
                + $"| stock texinfo {faces[f].TexInfo} extents {sizeO[0]}x{sizeO[1]} mins {minsO[0]},{minsO[1]} "
                + $"| correct texinfo {swapped} extents {sizeS[0]}x{sizeS[1]} mins {minsS[0]},{minsS[1]}\n");

            faces[f].TexInfo = (short)swapped;
            faces[f].LightmapTextureMinsInLuxels[0] = minsS[0];
            faces[f].LightmapTextureMinsInLuxels[1] = minsS[1];
        }

        stock[BspLump.Faces] = BspStructView.ToLump<DFace>(faces, stock[BspLump.Faces].Version);
        stock[BspLump.TexInfo] = BspStructView.ToLump<TexInfo>(texInfos.ToArray(), stock[BspLump.TexInfo].Version);

        await using (FileStream s = File.Create(Path.Combine(dir, "correct.bsp")))
        {
            await BspFile.SaveAsync(stock, s);
        }

        await File.WriteAllTextAsync(Path.Combine(dir, "extents.txt"), report.ToString());
        Assert.NotEmpty(dispFaces);
    }

    /// <summary><c>CalcFaceExtents</c>, without the error.</summary>
    private static (int[] Mins, int[] Size) Extents(BspData bsp, DFace face, TexInfo tex)
    {
        ReadOnlySpan<Vec3> vertexes = BspStructView.As<Vec3>(bsp[BspLump.Vertexes]);
        ReadOnlySpan<DEdge> edges = BspStructView.As<DEdge>(bsp[BspLump.Edges]);
        ReadOnlySpan<int> surfEdges = BspStructView.As<int>(bsp[BspLump.SurfEdges]);

        float[] mins = [1e24f, 1e24f];
        float[] maxs = [-1e24f, -1e24f];

        for (int i = 0; i < face.NumEdges; i++)
        {
            int e = surfEdges[face.FirstEdge + i];
            Vec3 v = e >= 0 ? vertexes[edges[e].V[0]] : vertexes[edges[-e].V[1]];

            for (int j = 0; j < 2; j++)
            {
                float val = (v.X * tex.LightmapVecsLuxelsPerWorldUnits[(j * 4) + 0])
                    + (v.Y * tex.LightmapVecsLuxelsPerWorldUnits[(j * 4) + 1])
                    + (v.Z * tex.LightmapVecsLuxelsPerWorldUnits[(j * 4) + 2])
                    + tex.LightmapVecsLuxelsPerWorldUnits[(j * 4) + 3];
                mins[j] = MathF.Min(mins[j], val);
                maxs[j] = MathF.Max(maxs[j], val);
            }
        }

        int[] m = new int[2];
        int[] s = new int[2];
        for (int i = 0; i < 2; i++)
        {
            float lo = MathF.Floor(mins[i]);
            float hi = MathF.Ceiling(maxs[i]);
            m[i] = (int)lo;
            s[i] = (int)(hi - lo);
        }

        return (m, s);
    }
}

/// <summary>Skips unless <c>P3F_SWAP_PATCH_DIR</c> is set.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SwapPatchFactAttribute : FactAttribute
{
    /// <summary>Decides at discovery.</summary>
    public SwapPatchFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("P3F_SWAP_PATCH_DIR")))
        {
            Skip = "no P3F_SWAP_PATCH_DIR: the swap downstream tool writes BSPs for a stock vrad run.";
        }
    }
}
