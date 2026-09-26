//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;

using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// <see cref="StockQuirk.DispVertNormalise"/> in isolation: the only thing
/// that separates a <see cref="ComplianceOptions.Correct"/> LUMP_DISP_VERTS
/// from stock's, measured on the stock catalogue (the I2 float threshold for
/// this lump, set from the first measurement and frozen here).
/// </summary>
public sealed class DispVertNormaliseQuirkTests
{
    /// <summary>
    /// The frozen I2 threshold: the most a Correct direction component may
    /// differ from stock's, in units in the last place. First measurement
    /// over the 18-map displacement catalogue: worst per map 0-2 ulps on 16 maps, 4
    /// on <c>p3f_half_edge_mixed</c>, 16 on <c>p3f_strip</c> — the large ones
    /// are snapped vertices, whose vector is <c>old * dist + offset</c> and so
    /// carries the estimate's error scaled by the distance into small
    /// components. Frozen at the measured maximum.
    /// </summary>
    public const int MaxUlps = 16;

    /// <summary>The catalogue entries, for xUnit's theory data.</summary>
    public static TheoryData<string> Entries => DispStockCatalogue.Entries;

    /// <summary>
    /// Under Correct, the distances are still stock's bit for bit — the quirk
    /// is in <c>VectorNormalize</c> only, <c>VectorLength</c> is exact.
    /// </summary>
    [DispStockTheory]
    [MemberData(nameof(Entries))]
    public void CorrectDistancesAreStocksBitForBit(string name)
    {
        DispStockMap map = DispStockMap.Load(name);
        DisplacementLumps lumps = Build(map, ComplianceOptions.Correct);

        for (int i = 0; i < lumps.Verts.Count; i++)
        {
            // Snapped vertices have dist 1 in both; the rest carry the length.
            Assert.True(DispFixtures.BitEqual(map.StockVerts[i].Dist, lumps.Verts[i].Dist), $"{name}: vert {i}");
        }
    }

    /// <summary>
    /// Under Correct, every direction component is within
    /// <see cref="MaxUlps"/> of stock's.
    /// </summary>
    [DispStockTheory]
    [MemberData(nameof(Entries))]
    public void CorrectDirectionsAreWithinTheFrozenUlpBound(string name)
    {
        DispStockMap map = DispStockMap.Load(name);
        DisplacementLumps lumps = Build(map, ComplianceOptions.Correct);

        int worst = 0;
        for (int i = 0; i < lumps.Verts.Count; i++)
        {
            worst = Math.Max(worst, Ulps(map.StockVerts[i].Vector.X, lumps.Verts[i].Vector.X));
            worst = Math.Max(worst, Ulps(map.StockVerts[i].Vector.Y, lumps.Verts[i].Vector.Y));
            worst = Math.Max(worst, Ulps(map.StockVerts[i].Vector.Z, lumps.Verts[i].Vector.Z));
        }

        Assert.True(worst <= MaxUlps, $"{name}: worst direction difference {worst} ulps");
    }

    /// <summary>
    /// The quirk is real: across the catalogue, Correct differs from stock in
    /// at least one direction component — otherwise the compliance switch
    /// would be gating nothing.
    /// </summary>
    [DispStockFact]
    public void CorrectDiffersFromStockSomewhere()
    {
        int differing = 0;

        foreach (string name in DispStockCatalogue.EntryNames)
        {
            DispStockMap map = DispStockMap.Load(name);
            DisplacementLumps lumps = Build(map, ComplianceOptions.Correct);

            for (int i = 0; i < lumps.Verts.Count; i++)
            {
                if (!DispFixtures.BitEqual(map.StockVerts[i].Vector, lumps.Verts[i].Vector))
                {
                    differing++;
                }
            }
        }

        Assert.True(differing > 0);
    }

    private static int Ulps(float a, float b)
    {
        int ia = BitConverter.SingleToInt32Bits(a);
        int ib = BitConverter.SingleToInt32Bits(b);
        if ((ia < 0) != (ib < 0))
        {
            return a == b ? 0 : int.MaxValue;
        }

        return Math.Abs(ia - ib);
    }

    private static DisplacementLumps Build(DispStockMap map, ComplianceOptions compliance)
    {
        DisplacementLumps lumps = new();
        DisplacementLumpBuilder.Build(
            map.Displacements, map.Faces, new VbspOptions { Compliance = compliance }, lumps);
        return lumps;
    }
}
