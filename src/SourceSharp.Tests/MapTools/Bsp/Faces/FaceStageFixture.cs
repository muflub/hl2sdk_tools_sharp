//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Faces;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;

namespace SourceSharp.Tests.MapTools.Bsp.Faces;

/// <summary>
/// A face stage over nothing but an arena, a plane table and one texinfo, for
/// the facts that test one routine rather than the whole pass.
/// </summary>
internal static class FaceStageFixture
{
    /// <summary>Builds a stage with a single flat texinfo.</summary>
    /// <param name="options">The vbsp options, or null for the defaults.</param>
    /// <param name="compliance">The compliance, or null for Correct.</param>
    /// <returns>The stage.</returns>
    internal static FaceBuildContext Create(
        VbspOptions? options = null,
        ComplianceOptions? compliance = null)
    {
        TexInfoTable texInfos = new();

        // One luxel per world unit on both lightmap axes, so a face's lightmap
        // extent in luxels equals its extent in units and the subdivision
        // threshold is easy to reason about.
        TexInfo flat = default;
        flat.LightmapVecsLuxelsPerWorldUnits[0] = 1f;
        flat.LightmapVecsLuxelsPerWorldUnits[5] = 1f;
        texInfos.Add(flat);

        return new FaceBuildContext(
            new WindingArena(), new PlaneTable(), texInfos, options ?? VbspOptions.Default)
        {
            Compliance = compliance ?? ComplianceOptions.Correct,
        };
    }

    /// <summary>Makes a face carrying a winding over the given points.</summary>
    /// <param name="context">The stage.</param>
    /// <param name="points">The polygon, wound counter-clockwise about +Z.</param>
    /// <param name="planeNumber">The plane number to tag it with.</param>
    /// <returns>The face.</returns>
    internal static Face Face(FaceBuildContext context, ReadOnlySpan<Vec3> points, int planeNumber = 0)
    {
        Face face = context.Faces.Alloc();
        face.Winding = context.Windings.Create(points);
        face.PlaneNumber = planeNumber;
        face.TexInfo = 0;
        face.Contents = 1;
        face.OriginalFace = new MapBrushSide { PlaneNumber = planeNumber, TexInfo = 0 };
        return face;
    }

    /// <summary>A face's winding, as an array.</summary>
    /// <param name="context">The stage.</param>
    /// <param name="face">The face.</param>
    /// <returns>Its points.</returns>
    internal static Vec3[] Points(FaceBuildContext context, Face face) =>
        [.. context.Windings.Points(face.Winding)];

    /// <summary>A face's live vertex numbers, as an array.</summary>
    /// <param name="face">The face.</param>
    /// <returns>Its indices.</returns>
    internal static int[] VertexNumbers(Face face) =>
        [.. face.VertexNumbers[..face.NumPoints]];
}
