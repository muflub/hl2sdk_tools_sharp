//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Bsp.Structs;

using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// Which of vbsp's displacement products change bytes between
/// <see cref="ComplianceOptions.Correct"/> and <see cref="ComplianceOptions.Stock"/>
/// on the stock catalogue — the per-quirk footprint the Q17 ruling asks to be
/// listed. Writes the table to <c>P3F_CORRECT_REPORT</c> when that is set.
/// </summary>
public sealed class DispCorrectVsStockTests
{
    /// <summary>
    /// DISP_TRIS and the sample positions never change; DISP_VERTS changes only
    /// through <see cref="StockQuirk.DispVertNormalise"/>; LUMP_FACES/TEXINFO only
    /// on a swapped face; the world box only where the surface leaves its puffed quad.
    /// </summary>
    [DispStockFact]
    public void TheCorrectFootprintIsExactlyTheThreeProducts()
    {
        StringBuilder report = new();
        int swappedMaps = 0;

        foreach (string name in DispStockCatalogue.EntryNames)
        {
            DispStockMap map = DispStockMap.Load(name);
            (IReadOnlyList<DisplacementResult> rs, DisplacementLumps ls) = Build(map, ComplianceOptions.Stock);
            (IReadOnlyList<DisplacementResult> rc, DisplacementLumps lc) = Build(map, ComplianceOptions.Correct);

            bool info = !Enumerable.Range(0, rs.Count).All(i => SameInfo(rs[i].Info, rc[i].Info));
            bool verts = !ls.Verts.SequenceEqual(lc.Verts);
            bool tris = !ls.Tris.SequenceEqual(lc.Tris);
            bool samples = !ls.LightmapSamplePositions.SequenceEqual(lc.LightmapSamplePositions);

            List<TexInfo> ts = [.. map.StockTexInfos];
            List<TexInfo> tc = [.. map.StockTexInfos];
            int[] faceTex = [.. map.Faces.Select(f => 0)];
            int[] a = DispVbspHooks.FaceTexInfos(rs, faceTex, ts, ComplianceOptions.Stock);
            int[] b = DispVbspHooks.FaceTexInfos(rc, faceTex, tc, ComplianceOptions.Correct);
            bool facesMove = !a.SequenceEqual(b) || ts.Count != tc.Count;
            swappedMaps += facesMove ? 1 : 0;

            int boundsMove = 0;
            for (int i = 0; i < map.Displacements.Count; i++)
            {
                DispBox s = DisplacementLumpBuilder.ComputeDispInfoBounds(map.Displacements[i], map.Faces[i], ComplianceOptions.Stock);
                DispBox c = DisplacementLumpBuilder.ComputeDispInfoBounds(map.Displacements[i], map.Faces[i], ComplianceOptions.Correct);
                boundsMove += (c.Min.X < s.Min.X || c.Min.Y < s.Min.Y || c.Min.Z < s.Min.Z
                    || c.Max.X > s.Max.X || c.Max.Y > s.Max.Y || c.Max.Z > s.Max.Z) ? 1 : 0;
            }

            report.Append(CultureInfo.InvariantCulture,
                $"{name}: DISPINFO {Y(info)} DISP_VERTS {Y(verts)} DISP_TRIS {Y(tris)} SAMPLES {Y(samples)} "
                + $"FACES+TEXINFO {Y(facesMove)} disp boxes growing past the puffed quad {boundsMove}/{map.Displacements.Count}\n");

            Assert.False(info, $"{name}: DISPINFO moved");
            Assert.False(tris, $"{name}: DISP_TRIS moved");
            Assert.False(samples, $"{name}: sample positions moved");
        }

        string? path = Environment.GetEnvironmentVariable("P3F_CORRECT_REPORT");
        if (!string.IsNullOrEmpty(path))
        {
            File.WriteAllText(path, report.ToString());
        }

        Assert.Equal(1, swappedMaps);
    }

    private static string Y(bool b) => b ? "CHANGES" : "same";

    private static bool SameInfo(DispInfo a, DispInfo b)
    {
        DispInfo x = a;
        DispInfo y = b;
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<DispInfo>(ref x))
            .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<DispInfo>(ref y)));
    }

    private static (IReadOnlyList<DisplacementResult>, DisplacementLumps) Build(DispStockMap map, ComplianceOptions c)
    {
        DisplacementLumps lumps = new();
        IReadOnlyList<DisplacementResult> r = DisplacementLumpBuilder.Build(
            map.Displacements, map.Faces, new VbspOptions { Compliance = c }, lumps);
        return (r, lumps);
    }
}
