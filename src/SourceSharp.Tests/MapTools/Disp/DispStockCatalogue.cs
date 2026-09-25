using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Disp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// Where this lane's STOCK-compiled displacement maps are, when this machine
/// has any.
/// </summary>
/// <remarks>
/// <para>
/// <b>The p3b catalogue has no displacements.</b> Verified with
/// <c>grep -l dispinfo $VVIS_STOCK_DIR/*.vmf</c>, which matches none of its
/// thirty maps — so this lane cannot reuse it and builds sixteen of its own.
/// </para>
/// <para>
/// The recipe, which is what a reader of a skipped test needs:
/// </para>
/// <code>
/// python3 scratchpad/p3f-genmaps.py /tmp/dispmaps
/// make toolgame
/// for f in /tmp/dispmaps/*.vmf; do
/// wine.../vbsp.exe -v -game "Z:$PWD/tools/mapgame" "Z:$f"
/// done
/// DISP_STOCK_DIR=/tmp/dispmaps dotnet test --filter Disp
/// </code>
/// <para>
/// The sixteen are chosen to reach the branches an equal-sized grid never
/// does: every power on its own; a grid of equal powers; grids of mixed
/// powers, which is what makes <c>SetupAllowedVerts</c> clear any bit at all;
/// a wide displacement whose edge is shared by two smaller ones, which is the
/// only shape producing <c>CORNER_TO_MIDPOINT</c> and
/// <c>MIDPOINT_TO_CORNER</c>; a pair whose start corners differ, which is the
/// only way to get a neighbour orientation other than <c>CCW_0</c>; and a flat
/// pair meeting at one point, for the corner lists.
/// </para>
/// </remarks>
internal static class DispStockCatalogue
{
    /// <summary>The environment variable naming the directory.</summary>
    internal const string DirectoryVariable = "DISP_STOCK_DIR";

    /// <summary>The directory, or null when the variable is not set.</summary>
    internal static string? Directory =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } set
            ? set
            : null;

    /// <summary>Why a fact needing stock output cannot run, or null.</summary>
    /// <returns>A skip reason, or null when the maps are there.</returns>
    internal static string? SkipReason()
    {
        string? directory = Directory;

        if (directory is null)
        {
            return $"no {DirectoryVariable}: this tree has no stock-compiled displacement "
                + "maps. See DispStockCatalogue for the recipe.";
        }

        // Set but WRONG is not a skip: a gate that passes because someone
        // typo'd a path has certified nothing.
        return null;
    }

    /// <summary>The entries that have both a VMF and a compiled BSP.</summary>
    internal static TheoryData<string> Entries
    {
        get
        {
            TheoryData<string> data = [];

            foreach (string name in EntryNames)
            {
                data.Add(name);
            }

            return data;
        }
    }

    /// <summary>The same entries, as plain names, in a stable order.</summary>
    internal static IReadOnlyList<string> EntryNames
    {
        get
        {
            string? directory = Directory;

            if (directory is null || !System.IO.Directory.Exists(directory))
            {
                return ["none"];
            }

            List<string> names = [];

            foreach (string bsp in System.IO.Directory
                .GetFiles(directory, "*.bsp")
                .Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileNameWithoutExtension(bsp);

                if (File.Exists(Path.Combine(directory, name + ".vmf")))
                {
                    names.Add(name);
                }
            }

            return names.Count == 0 ? ["none"] : names;
        }
    }

    /// <summary>The VMF for one entry.</summary>
    /// <param name="name">The entry name.</param>
    /// <returns>The host path.</returns>
    internal static string VmfPath(string name) =>
        Path.Combine(Directory!, name + ".vmf");

    /// <summary>The stock-compiled BSP for one entry.</summary>
    /// <param name="name">The entry name.</param>
    /// <returns>The host path.</returns>
    internal static string BspPath(string name) =>
        Path.Combine(Directory!, name + ".bsp");
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips, visibly, when this tree has no
/// stock-compiled displacement maps.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DispStockFactAttribute : FactAttribute
{
    /// <summary>Decides at discovery whether the maps are there.</summary>
    public DispStockFactAttribute() => Skip = DispStockCatalogue.SkipReason();
}

/// <summary>
/// A <see cref="TheoryAttribute"/> that skips, visibly, when this tree has no
/// stock-compiled displacement maps.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DispStockTheoryAttribute : TheoryAttribute
{
    /// <summary>Decides at discovery whether the maps are there.</summary>
    public DispStockTheoryAttribute() => Skip = DispStockCatalogue.SkipReason();
}

/// <summary>
/// One stock-compiled map, read back as the inputs this lane needs and the
/// outputs it is judged against.
/// </summary>
/// <remarks>
/// <para>
/// <b>What comes from the stock BSP and is therefore NOT gated.</b> Four
/// things, and naming them is the difference between a gate and a tautology:
/// </para>
/// <list type="number">
/// <item>
/// the base face's four winding points. Stock builds these in
/// <c>MakeBrushWindings</c>, which is the CSG lane's, and
/// <c>EmitFaceVertexes</c> writes the SIDE's own winding through unchanged
/// — so reading them back is reading this lane's
/// input, not its output. The catalogue's brushes are integer-aligned, so
/// <c>GetVertexnum</c>'s snapping is the identity on them;
/// </item>
/// <item>
/// the face's texinfo vectors, which are the texture lane's;
/// </item>
/// <item>
/// the brush contents, which is the material lane's — this lane copies it
/// into <c>ddispinfo_t::contents</c> unchanged, so that ONE field of the
/// comparison is an echo and is excluded from the gate by name;
/// </item>
/// <item>
/// which face each displacement belongs to, which is the face lane's.
/// </item>
/// </list>
/// <para>
/// Everything else — the power, the start position, the field vectors and
/// distances, the alphas and the tags — comes from the VMF through this lane's
/// own reader.
/// </para>
/// </remarks>
internal sealed class DispStockMap
{
    private DispStockMap(
        string name,
        IReadOnlyList<MapDisplacement> displacements,
        IReadOnlyList<DisplacementFace> faces,
        IReadOnlyList<DispInfo> stockInfos,
        IReadOnlyList<DispVert> stockVerts,
        IReadOnlyList<DispTri> stockTris,
        byte[] stockSamplePositions,
        IReadOnlyList<(int U, int V)> stockLightmapSizes,
        IReadOnlyList<Vec3> sideUAxes,
        IReadOnlyList<TexInfo> stockFaceTexInfos,
        IReadOnlyList<TexInfo> stockTexInfos)
    {
        SideUAxes = sideUAxes;
        StockFaceTexInfos = stockFaceTexInfos;
        StockTexInfos = stockTexInfos;
        Name = name;
        Displacements = displacements;
        Faces = faces;
        StockInfos = stockInfos;
        StockVerts = stockVerts;
        StockTris = stockTris;
        StockSamplePositions = stockSamplePositions;
        StockLightmapSizes = stockLightmapSizes;
    }

    public string Name { get; }

    /// <summary>The VMF's displacements, in LUMP_DISPINFO order.</summary>
    public IReadOnlyList<MapDisplacement> Displacements { get; }

    /// <summary>Each one's base face, read back from the stock BSP.</summary>
    public IReadOnlyList<DisplacementFace> Faces { get; }

    /// <summary>Stock's LUMP_DISPINFO.</summary>
    public IReadOnlyList<DispInfo> StockInfos { get; }

    /// <summary>Stock's LUMP_DISP_VERTS.</summary>
    public IReadOnlyList<DispVert> StockVerts { get; }

    /// <summary>Stock's LUMP_DISP_TRIS.</summary>
    public IReadOnlyList<DispTri> StockTris { get; }

    /// <summary>Stock's LUMP_DISP_LIGHTMAP_SAMPLE_POSITIONS.</summary>
    public byte[] StockSamplePositions { get; }

    /// <summary>
    /// Each base face's <c>m_LightmapTextureSizeInLuxels</c>, which this lane
    /// computes and the face lane writes.
    /// </summary>
    public IReadOnlyList<(int U, int V)> StockLightmapSizes { get; }

    /// <summary>Each displacement's VMF side <c>uaxis</c> direction.</summary>
    public IReadOnlyList<Vec3> SideUAxes { get; }

    /// <summary>The texinfo stock's base face actually carries, swapped or not.</summary>
    public IReadOnlyList<TexInfo> StockFaceTexInfos { get; }

    /// <summary>Stock's whole LUMP_TEXINFO, after <c>CompactTexinfos</c>.</summary>
    public IReadOnlyList<TexInfo> StockTexInfos { get; }

    /// <summary>
    /// Loads one catalogue entry: its VMF displacements and its stock lumps.
    /// </summary>
    /// <param name="name">The entry name.</param>
    /// <returns>The pair.</returns>
    public static DispStockMap Load(string name)
    {
        using FileStream stream = File.OpenRead(DispStockCatalogue.BspPath(name));
        BspData bsp = BspFile.LoadAsync(stream).GetAwaiter().GetResult();

        ReadOnlySpan<DispInfo> infos = BspStructView.As<DispInfo>(bsp[BspLump.DispInfo]);
        ReadOnlySpan<DispVert> verts = BspStructView.As<DispVert>(bsp[BspLump.DispVerts]);
        ReadOnlySpan<DispTri> tris = BspStructView.As<DispTri>(bsp[BspLump.DispTris]);
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        ReadOnlySpan<Vec3> vertexes = BspStructView.As<Vec3>(bsp[BspLump.Vertexes]);
        ReadOnlySpan<DEdge> edges = BspStructView.As<DEdge>(bsp[BspLump.Edges]);
        ReadOnlySpan<int> surfEdges = BspStructView.As<int>(bsp[BspLump.SurfEdges]);
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);

        List<DisplacementFace> baseFaces = [];
        List<(int U, int V)> sizes = [];

        for (int d = 0; d < infos.Length; d++)
        {
            int faceIndex = -1;
            for (int f = 0; f < faces.Length; f++)
            {
                if (faces[f].DispInfo == d)
                {
                    faceIndex = f;
                    break;
                }
            }

            Assert.True(faceIndex >= 0, $"{name}: displacement {d} has no face");

            DFace face = faces[faceIndex];
            Assert.Equal(4, face.NumEdges);

            Vec3[] winding = new Vec3[4];
            for (int k = 0; k < 4; k++)
            {
                int se = surfEdges[face.FirstEdge + k];
                winding[k] = se < 0 ? vertexes[edges[-se].V[1]] : vertexes[edges[se].V[0]];
            }

            TexInfo texInfo = texInfos[face.TexInfo];

            float[] textureVecs = new float[8];
            for (int r = 0; r < 2; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    textureVecs[(r * 4) + c] = texInfo.TextureVecsTexelsPerWorldUnits[(r * 4) + c];
                }
            }

            Vec3 lightU = new(
                texInfo.LightmapVecsLuxelsPerWorldUnits[0], texInfo.LightmapVecsLuxelsPerWorldUnits[1], texInfo.LightmapVecsLuxelsPerWorldUnits[2]);
            Vec3 lightV = new(
                texInfo.LightmapVecsLuxelsPerWorldUnits[4], texInfo.LightmapVecsLuxelsPerWorldUnits[5], texInfo.LightmapVecsLuxelsPerWorldUnits[6]);

            baseFaces.Add(new DisplacementFace(
                faceIndex, winding, infos[d].Contents, lightU, lightV, textureVecs));

            sizes.Add((face.LightmapTextureSizeInLuxels[0], face.LightmapTextureSizeInLuxels[1]));
        }

        VmfDocument document = VmfDocument
            .ParseAsync(File.ReadAllText(DispStockCatalogue.VmfPath(name)))
            .AsTask().GetAwaiter().GetResult();

        List<MapDisplacement> displacements = [];
        List<Vec3> sideUAxes = [];
        foreach (VmfChunk chunk in document.Chunks)
        {
            CollectDisplacements(chunk, displacements, sideUAxes);
        }

        List<TexInfo> stockFaceTexInfos = [];
        for (int d = 0; d < baseFaces.Count; d++)
        {
            stockFaceTexInfos.Add(texInfos[faces[baseFaces[d].FaceIndex].TexInfo]);
        }

        List<TexInfo> allTexInfos = [.. texInfos.ToArray()];

        return new DispStockMap(
            name,
            displacements,
            baseFaces,
            infos.ToArray(),
            verts.ToArray(),
            tris.ToArray(),
            bsp[BspLump.DispLightmapSamplePositions].Data.ToArray(),
            sizes,
            sideUAxes,
            stockFaceTexInfos,
            allTexInfos);
    }

    /// <summary>
    /// Walks the VMF for <c>dispinfo</c> chunks in file order, which is the
    /// order <c>nummapdispinfo</c> counts them in and therefore LUMP_DISPINFO's
    /// order.
    /// </summary>
    private static void CollectDisplacements(VmfChunk chunk, List<MapDisplacement> into, List<Vec3> uAxes)
    {
        foreach (VmfChunk child in chunk.Chunks)
        {
            if (string.Equals(
                    child.Name,
                    VmfDisplacementReader.DispInfoChunk,
                    StringComparison.OrdinalIgnoreCase))
            {
                into.Add(VmfDisplacementReader.Read(child));
                uAxes.Add(ParseAxis(chunk.GetValue("uaxis")));
                continue;
            }

            CollectDisplacements(child, into, uAxes);
        }
    }

    /// <summary>The direction of a VMF <c>"[x y z shift] scale"</c> axis.</summary>
    private static Vec3 ParseAxis(string? value)
    {
        Assert.NotNull(value);
        string[] parts = value.Trim('[').Split([' ', ']'], StringSplitOptions.RemoveEmptyEntries);
        return new Vec3(
            float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
    }
}
