//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Compare;

/// <summary>
/// Turns a piece of geometry into a text key that does not depend on the index
/// order the compiler happened to assign.
/// </summary>
/// <remarks>
/// <para>
/// Text rather than a hash, for two reasons. The report has to be able to SAY
/// what is in one map and not the other -- the port's plan §2 asks for the
/// items, not a count -- and a hash collision in a comparison instrument would
/// be a false "identical", which is the one failure mode a check like this must
/// not have.
/// </para>
/// <para>
/// Every number is written in the invariant culture. A comma decimal separator
/// would make two identical maps compare unequal across locales, and this
/// project has already been bitten by exactly that class of bug in its VMF
/// parser.
/// </para>
/// </remarks>
internal static class CanonicalKey
{
    /// <summary>
    /// One float, snapped to a multiple of <paramref name="epsilon"/> when the
    /// caller set one.
    /// </summary>
    internal static string Float(float value, float? epsilon)
    {
        if (epsilon is { } eps && eps > 0.0f)
        {
            // Snap to the nearest multiple, then print the MULTIPLE rather than
            // the snapped value, so the key text cannot itself reintroduce a
            // rounding difference at the last printed digit.
            double multiple = Math.Round(value / (double)eps, MidpointRounding.ToEven);

            // -0 and 0 are the same multiple; normalise so they print alike.
            if (multiple == 0.0)
            {
                multiple = 0.0;
            }

            return multiple.ToString("R", CultureInfo.InvariantCulture);
        }

        // "G9" round-trips a float exactly. Normalise negative zero, which is
        // == 0.0f but prints as "-0".
        float normalised = value == 0.0f ? 0.0f : value;
        return normalised.ToString("G9", CultureInfo.InvariantCulture);
    }

    /// <summary>One vector, as three canonical floats.</summary>
    internal static string Vector(Vec3 v, float? epsilon) =>
        string.Concat(Float(v.X, epsilon), ",", Float(v.Y, epsilon), ",", Float(v.Z, epsilon));

    /// <summary>
    /// A ring of vertices, rotated so it starts at its own smallest vertex.
    /// </summary>
    /// <param name="ring">The vertices, in winding order.</param>
    /// <param name="epsilon">The caller's epsilon, or null.</param>
    /// <returns>The canonical ring text.</returns>
    /// <remarks>
    /// <para>
    /// A face's vertex ring is a CYCLE: the same polygon, wound the same way,
    /// can start at any of its vertices, and which one it starts at is decided
    /// by the edge the compiler emitted first. Rotating to the lexicographically
    /// smallest vertex removes that freedom while keeping the winding, so a
    /// face whose ring was rotated compares equal and a face that was REVERSED
    /// -- which is a different surface, facing the other way -- still does not.
    /// </para>
    /// <para>
    /// Ties are broken by continuing round the ring from each candidate start,
    /// so a degenerate face with a repeated vertex still gets one answer rather
    /// than an arbitrary one.
    /// </para>
    /// </remarks>
    internal static string Ring(IReadOnlyList<Vec3> ring, float? epsilon)
    {
        if (ring.Count == 0)
        {
            return "[]";
        }

        int count = ring.Count;
        string[] vertices = new string[count];
        for (int i = 0; i < count; i++)
        {
            vertices[i] = Vector(ring[i], epsilon);
        }

        int start = 0;
        for (int candidate = 1; candidate < count; candidate++)
        {
            if (RotationIsSmaller(vertices, candidate, start))
            {
                start = candidate;
            }
        }

        StringBuilder text = new("[");
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                text.Append(';');
            }

            text.Append(vertices[(start + i) % count]);
        }

        return text.Append(']').ToString();
    }

    private static bool RotationIsSmaller(string[] vertices, int candidate, int incumbent)
    {
        int count = vertices.Length;
        for (int i = 0; i < count; i++)
        {
            int order = string.CompareOrdinal(
                vertices[(candidate + i) % count],
                vertices[(incumbent + i) % count]);
            if (order != 0)
            {
                return order < 0;
            }
        }

        return false;
    }
}
