//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp.Structs;

using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// The lane's central gate: every displacement lump, element for element,
/// against a stock <c>vbsp</c> compile of the same VMF.
/// </summary>
/// <remarks>
/// <para>
/// <b>Element for element and not byte for byte, and that is forced.</b>
/// <c>ddispinfo_t</c> carries three separate paddings —
/// <c>CDispSubNeighbor</c>'s trailing byte, <c>CDispCornerNeighbors</c>'s
/// trailing byte, and the two bytes after <c>m_iMapFace</c> — and stock never
/// writes any of them. <c>g_dispinfo</c> is a <c>CUtlVector</c> that
/// <c>SetSize</c>s without zeroing, so what lands in the BSP is whatever
/// <c>malloc</c> returned. MEASURED: <c>p3f_disp_p2_flat</c> ships
/// <c>0x006f</c> in that two-byte hole, reproducibly across runs and
/// differently from every other map in the catalogue, which ship zero. A
/// byte-exact gate would be gating the allocator.
/// </para>
/// <para>
/// The same is true one level down. <c>CDispCornerNeighbors::SetInvalid</c>
/// clears only <c>m_nNeighbors</c>, so the four index slots past the count
/// keep whatever the surface was constructed over — which is why the corner
/// comparison here reads the count and then only the live prefix.
/// </para>
/// <para>
/// Every fact selects <see cref="ComplianceOptions.Stock"/>. See
/// <see cref="StockQuirk.DispVertNormalise"/> for the one behaviour that
/// makes a difference, and
/// <see cref="DispVertNormaliseQuirkTests"/> for the fact that isolates it.
/// </para>
/// </remarks>
public sealed class DispStockLumpTests
{
    /// <summary>The catalogue entries, for xUnit's theory data.</summary>
    public static TheoryData<string> Entries => DispStockCatalogue.Entries;

    /// <summary>
    /// The catalogue is not empty, so a green run means something.
    /// </summary>
    /// <remarks>
    /// The check that the other facts in this file cannot make: an empty
    /// <see cref="DispStockCatalogue.EntryNames"/> would make every theory
    /// below pass with no cases at all, and a suite that checked nothing looks
    /// exactly like a suite that checked everything and agreed.
    /// </remarks>
    [DispStockFact]
    public void TheCatalogueHoldsDisplacementMaps()
    {
        IReadOnlyList<string> names = DispStockCatalogue.EntryNames;

        Assert.NotEqual(["none"], names);
        Assert.True(
            names.Count >= 10,
            $"{DispStockCatalogue.Directory} holds {names.Count} compiled displacement maps; "
            + "the recipe in DispStockCatalogue builds sixteen.");
    }

    /// <summary>Every catalogue map actually contains displacements.</summary>
    [DispStockTheory]
    [MemberData(nameof(Entries))]
    public void EveryEntryHasAtLeastOneDisplacement(string name)
    {
        DispStockMap map = DispStockMap.Load(name);

        Assert.NotEmpty(map.StockInfos);
        Assert.Equal(map.StockInfos.Count, map.Displacements.Count);
    }

    /// <summary>
    /// The LUMP_DISP_VERTS lump matches stock's, vertex for vertex and bit for
    /// bit.
    /// </summary>
    /// <remarks>
    /// Bitwise on the floats, through
    /// <see cref="BitConverter.SingleToInt32Bits"/>, because a tolerance here
    /// would hide exactly the difference this lump is most exposed to: the
    /// normalise estimate moves the direction in the last bits and nowhere
    /// else.
    /// </remarks>
    [DispStockTheory]
    [MemberData(nameof(Entries))]
    public void TheDispVertsLumpMatchesStockElementForElement(string name)
    {
        DispStockMap map = DispStockMap.Load(name);
        DisplacementLumps lumps = Build(map, ComplianceOptions.Stock);

        Assert.Equal(map.StockVerts.Count, lumps.Verts.Count);

        for (int i = 0; i < lumps.Verts.Count; i++)
        {
            DispVert stock = map.StockVerts[i];
            DispVert ours = lumps.Verts[i];

            AssertBitEqual(name, $"vert {i} x", stock.Vector.X, ours.Vector.X);
            AssertBitEqual(name, $"vert {i} y", stock.Vector.Y, ours.Vector.Y);
            AssertBitEqual(name, $"vert {i} z", stock.Vector.Z, ours.Vector.Z);
            AssertBitEqual(name, $"vert {i} dist", stock.Dist, ours.Dist);
            AssertBitEqual(name, $"vert {i} alpha", stock.Alpha, ours.Alpha);
        }
    }

    /// <summary>The LUMP_DISP_TRIS lump matches stock's, tag word for tag word.</summary>
    [DispStockTheory]
    [MemberData(nameof(Entries))]
    public void TheDispTrisLumpMatchesStockElementForElement(string name)
    {
        DispStockMap map = DispStockMap.Load(name);
        DisplacementLumps lumps = Build(map, ComplianceOptions.Stock);

        Assert.Equal(map.StockTris.Count, lumps.Tris.Count);

        for (int i = 0; i < lumps.Tris.Count; i++)
        {
            Assert.True(
                map.StockTris[i].Tags == lumps.Tris[i].Tags,
                $"{name}: triangle {i} tags are 0x{map.StockTris[i].Tags:x4} in stock and "
                + $"0x{lumps.Tris[i].Tags:x4} here");
        }
    }

    /// <summary>
    /// Every scalar field of LUMP_DISPINFO matches stock's, padding excluded.
    /// </summary>
    /// <remarks>
    /// <c>Contents</c> is deliberately absent: this lane copies it from the
    /// brush and the brush contents is what the gate FED it, so comparing it
    /// would compare a value with itself. See <see cref="DispStockMap"/>.
    /// </remarks>
    [DispStockTheory]
    [MemberData(nameof(Entries))]
    public void TheDispInfoScalarsMatchStockElementForElement(string name)
    {
        DispStockMap map = DispStockMap.Load(name);
        IReadOnlyList<DisplacementResult> results = BuildResults(map, ComplianceOptions.Stock);

        Assert.Equal(map.StockInfos.Count, results.Count);

        for (int i = 0; i < results.Count; i++)
        {
            DispInfo stock = map.StockInfos[i];
            DispInfo ours = results[i].Info;

            Assert.True(
                stock.StartPosition == ours.StartPosition,
                $"{name}: displacement {i} start position is {stock.StartPosition} in stock "
                + $"and {ours.StartPosition} here");

            AssertEqual(name, i, "DispVertStart", stock.DispVertStart, ours.DispVertStart);
            AssertEqual(name, i, "DispTriStart", stock.DispTriStart, ours.DispTriStart);
            AssertEqual(name, i, "Power", stock.Power, ours.Power);
            AssertEqual(name, i, "MinTess", stock.MinTess, ours.MinTess);
            AssertEqual(name, i, "MapFace", stock.MapFace, ours.MapFace);
            AssertEqual(
                name, i, "LightmapAlphaStart", stock.LightmapAlphaStart, ours.LightmapAlphaStart);
            AssertEqual(
                name,
                i,
                "LightmapSamplePositionStart",
                stock.LightmapSamplePositionStart,
                ours.LightmapSamplePositionStart);
            AssertBitEqual(
                name, $"displacement {i} SmoothingAngle", stock.SmoothingAngle, ours.SmoothingAngle);
        }
    }

    /// <summary>
    /// Every edge neighbour matches stock's: index, orientation and both
    /// spans.
    /// </summary>
    [DispStockTheory]
    [MemberData(nameof(Entries))]
    public void TheEdgeNeighboursMatchStockElementForElement(string name)
    {
        DispStockMap map = DispStockMap.Load(name);
        IReadOnlyList<DisplacementResult> results = BuildResults(map, ComplianceOptions.Stock);

        for (int i = 0; i < results.Count; i++)
        {
            DispInfo stock = map.StockInfos[i];
            DispInfo ours = results[i].Info;

            for (int edge = 0; edge < 4; edge++)
            {
                for (int sub = 0; sub < 2; sub++)
                {
                    DispSubNeighbor s = stock.EdgeNeighbors[edge].SubNeighbors[sub];
                    DispSubNeighbor o = ours.EdgeNeighbors[edge].SubNeighbors[sub];

                    string where = $"{name}: displacement {i} edge {edge} sub {sub}";

                    Assert.True(
                        s.Neighbor == o.Neighbor,
                        $"{where} neighbour is {s.Neighbor} in stock and {o.Neighbor} here");

                    if (!s.IsValid())
                    {
                        // Stock leaves the other three fields of an empty slot
                        // alone, so there is nothing more to compare.
                        continue;
                    }

                    Assert.True(
                        s.NeighborOrientation == o.NeighborOrientation,
                        $"{where} orientation is {s.NeighborOrientation} in stock and "
                        + $"{o.NeighborOrientation} here");
                    Assert.True(
                        s.Span == o.Span,
                        $"{where} span is {s.Span} in stock and {o.Span} here");
                    Assert.True(
                        s.NeighborSpan == o.NeighborSpan,
                        $"{where} neighbour span is {s.NeighborSpan} in stock and "
                        + $"{o.NeighborSpan} here");
                }
            }
        }
    }

    /// <summary>
    /// Every corner's neighbour list matches stock's: the count, and the live
    /// prefix.
    /// </summary>
    [DispStockTheory]
    [MemberData(nameof(Entries))]
    public void TheCornerNeighboursMatchStockElementForElement(string name)
    {
        DispStockMap map = DispStockMap.Load(name);
        IReadOnlyList<DisplacementResult> results = BuildResults(map, ComplianceOptions.Stock);

        for (int i = 0; i < results.Count; i++)
        {
            DispInfo stock = map.StockInfos[i];
            DispInfo ours = results[i].Info;

            for (int corner = 0; corner < 4; corner++)
            {
                DispCornerNeighbors s = stock.CornerNeighbors[corner];
                DispCornerNeighbors o = ours.CornerNeighbors[corner];

                Assert.True(
                    s.NumNeighbors == o.NumNeighbors,
                    $"{name}: displacement {i} corner {corner} has {s.NumNeighbors} neighbours "
                    + $"in stock and {o.NumNeighbors} here");

                for (int n = 0; n < s.NumNeighbors; n++)
                {
                    Assert.True(
                        s.Neighbors[n] == o.Neighbors[n],
                        $"{name}: displacement {i} corner {corner} neighbour {n} is "
                        + $"{s.Neighbors[n]} in stock and {o.Neighbors[n]} here");
                }
            }
        }
    }

    /// <summary>
    /// The allowed-vertex bit vector matches stock's, word for word.
    /// </summary>
    /// <remarks>
    /// The one output on which a mixed-power map differs from a uniform one at
    /// all. On a grid of equal powers every word is <c>0xFFFFFFFF</c> and this
    /// fact would pass over a completely broken
    /// <c>SetupAllowedVerts</c> — which is why the catalogue carries
    /// <c>grid_mixed</c>, <c>grid_mixed2</c> and <c>half_edge_mixed</c>, and
    /// why <see cref="SomeCatalogueMapClearsAnAllowedVertexBit"/> exists
    /// beside it.
    /// </remarks>
    [DispStockTheory]
    [MemberData(nameof(Entries))]
    public void TheAllowedVertsMatchStockElementForElement(string name)
    {
        DispStockMap map = DispStockMap.Load(name);
        IReadOnlyList<DisplacementResult> results = BuildResults(map, ComplianceOptions.Stock);

        for (int i = 0; i < results.Count; i++)
        {
            for (int w = 0; w < CoreDispInfo.AllowedVertsDWords; w++)
            {
                uint stock = map.StockInfos[i].AllowedVerts[w];
                uint ours = results[i].Info.AllowedVerts[w];

                Assert.True(
                    stock == ours,
                    $"{name}: displacement {i} allowed-verts word {w} is 0x{stock:x8} in stock "
                    + $"and 0x{ours:x8} here");
            }
        }
    }

    /// <summary>
    /// Some catalogue map has a cleared allowed-vertex bit, so that
    /// <see cref="TheAllowedVertsMatchStockElementForElement"/> is not a
    /// comparison of all-ones against all-ones.
    /// </summary>
    [DispStockFact]
    public void SomeCatalogueMapClearsAnAllowedVertexBit()
    {
        int cleared = 0;

        foreach (string name in DispStockCatalogue.EntryNames)
        {
            DispStockMap map = DispStockMap.Load(name);

            foreach (DispInfo info in map.StockInfos)
            {
                for (int w = 0; w < CoreDispInfo.AllowedVertsDWords; w++)
                {
                    cleared += System.Numerics.BitOperations.PopCount(~info.AllowedVerts[w]);
                }
            }
        }

        Assert.True(
            cleared > 0,
            "no catalogue map has a single allowed-vertex bit cleared, so the allowed-verts "
            + "gate is comparing 0xFFFFFFFF against 0xFFFFFFFF on every map.");
    }

    /// <summary>
    /// The LUMP_DISP_LIGHTMAP_SAMPLE_POSITIONS lump matches stock's, byte for
    /// byte.
    /// </summary>
    /// <remarks>
    /// This one IS byte-exact, and can be: it is a packed byte stream with no
    /// struct padding anywhere in it. It is also the strictest fact in the
    /// file — a sample's four or five bytes depend on the luxel coordinates,
    /// which depend on the lightmap size, the start corner, the point
    /// rotation and the bilinear walk, and on the quad tree's triangle order.
    /// </remarks>
    [DispStockTheory]
    [MemberData(nameof(Entries))]
    public void TheLightmapSamplePositionsMatchStockByteForByte(string name)
    {
        DispStockMap map = DispStockMap.Load(name);
        DisplacementLumps lumps = Build(map, ComplianceOptions.Stock);

        byte[] ours = [.. lumps.LightmapSamplePositions];

        Assert.True(
            map.StockSamplePositions.Length == ours.Length,
            $"{name}: stock's sample-position lump is {map.StockSamplePositions.Length} bytes "
            + $"and this one is {ours.Length}");

        for (int i = 0; i < ours.Length; i++)
        {
            Assert.True(
                map.StockSamplePositions[i] == ours[i],
                $"{name}: sample-position byte {i} is {map.StockSamplePositions[i]} in stock "
                + $"and {ours[i]} here");
        }
    }

    /// <summary>
    /// The lightmap size this lane computes matches the one stock wrote onto
    /// the base face.
    /// </summary>
    /// <remarks>
    /// Not a lump of this lane's, but the one value it hands to the face lane,
    /// and the value every sample position above is laid out on. Gating it
    /// separately says whether a sample-position mismatch is the size or the
    /// walk.
    /// </remarks>
    [DispStockTheory]
    [MemberData(nameof(Entries))]
    public void TheLightmapSizesMatchStock(string name)
    {
        DispStockMap map = DispStockMap.Load(name);
        IReadOnlyList<DisplacementResult> results = BuildResults(map, ComplianceOptions.Stock);

        for (int i = 0; i < results.Count; i++)
        {
            Assert.True(
                map.StockLightmapSizes[i].U == results[i].LightmapSizeU &&
                map.StockLightmapSizes[i].V == results[i].LightmapSizeV,
                $"{name}: displacement {i} lightmap size is "
                + $"{map.StockLightmapSizes[i]} in stock and "
                + $"({results[i].LightmapSizeU}, {results[i].LightmapSizeV}) here");
        }
    }

    /// <summary>Builds one map's lumps under a compliance policy.</summary>
    internal static DisplacementLumps Build(DispStockMap map, ComplianceOptions compliance)
    {
        DisplacementLumps lumps = new();
        DisplacementLumpBuilder.Build(
            map.Displacements,
            map.Faces,
            new VbspOptions { Compliance = compliance },
            lumps);

        return lumps;
    }

    /// <summary>Builds one map's lumps and keeps the per-displacement results.</summary>
    internal static IReadOnlyList<DisplacementResult> BuildResults(
        DispStockMap map, ComplianceOptions compliance)
    {
        DisplacementLumps lumps = new();

        return DisplacementLumpBuilder.Build(
            map.Displacements,
            map.Faces,
            new VbspOptions { Compliance = compliance },
            lumps);
    }

    private static void AssertEqual(string map, int index, string field, long stock, long ours)
    {
        Assert.True(
            stock == ours,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{map}: displacement {index} {field} is {stock} in stock and {ours} here"));
    }

    private static void AssertBitEqual(string map, string what, float stock, float ours)
    {
        int a = BitConverter.SingleToInt32Bits(stock);
        int b = BitConverter.SingleToInt32Bits(ours);

        Assert.True(
            a == b,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{map}: {what} is {stock:R} (0x{a:x8}) in stock and {ours:R} (0x{b:x8}) here"));
    }
}
