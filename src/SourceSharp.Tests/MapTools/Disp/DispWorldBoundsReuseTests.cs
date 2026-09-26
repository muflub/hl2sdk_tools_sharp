//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Options;
using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// The world-bounds boxes(<c>ComputeDispInfoBounds</c>)
/// taken from the lump build's own cores instead of a second, face-less build
/// (plan 3p). They must be the second build's boxes under both compliances.
/// </summary>
public class DispWorldBoundsReuseTests
{
    [Fact]
    public void TheBuildsBoxesAreComputeDispInfoBoundsUnderCorrect() => AssertSameBoxes(ComplianceOptions.Correct);

    [Fact]
    public void TheBuildsBoxesAreComputeDispInfoBoundsUnderStock() => AssertSameBoxes(ComplianceOptions.Stock);

    private static void AssertSameBoxes(ComplianceOptions compliance)
    {
        (List<MapDisplacement> disps, List<DisplacementFace> faces) = Grid();
        List<DispBox> reused = [];
        DisplacementLumpBuilder.Build(disps, faces, new VbspOptions { Compliance = compliance }, new DisplacementLumps(), null, reused);

        (List<MapDisplacement> fresh, List<DisplacementFace> freshFaces) = Grid();
        List<DispBox> rebuilt = [.. fresh.Select((d, i) => DisplacementLumpBuilder.ComputeDispInfoBounds(d, freshFaces[i], compliance))];

        Assert.Equal(rebuilt.Count, reused.Count);
        for (int i = 0; i < rebuilt.Count; i++)
        {
            Assert.True(DispFixtures.BitEqual(rebuilt[i].Min, reused[i].Min), $"min {i}");
            Assert.True(DispFixtures.BitEqual(rebuilt[i].Max, reused[i].Max), $"max {i}");
        }
    }

    // A 3 x 3 grid of displacements with rotated starts, varied powers and
    // heights, and slanted field vectors, on faces with non-default texture axes.
    private static (List<MapDisplacement>, List<DisplacementFace>) Grid()
    {
        List<MapDisplacement> disps = [];
        List<DisplacementFace> faces = [];
        for (int k = 0; k < 9; k++)
        {
            Vec3 min = new((k % 3) * 256f, (k / 3) * 256f, 8f * k);
            Vec3[] winding = DispFixtures.FloorQuad(min, 256, 256);
            MapDisplacement disp = DispFixtures.Heightfield(2 + (k % 3), winding[k % 4], (x, y) => ((x * 7) + (y * 3) + k) % 29 - 9f);
            for (int v = 0; v < disp.FieldVectors.Length; v++)
            {
                disp.FieldVectors[v] = new Vec3(0.1f * ((v + k) % 3), -0.05f * (v % 2), 1f);
            }

            disps.Add(disp);
            faces.Add(DispFixtures.Face(winding, faceIndex: k));
        }

        return (disps, faces);
    }
}
