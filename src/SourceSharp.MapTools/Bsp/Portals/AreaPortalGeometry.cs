//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Numerics;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Bsp.Portals;

/// <summary>
/// The convex outline of an areaportal, as the engine clips against it
/// </summary>
/// <remarks>
/// The engine needs one convex polygon per areaportal to clip the view against,
/// but the BSP has cut that doorway into as many portals as the tree happened
/// to need. So every portal between the same two areas lying in the same plane
/// is found, all their points are thrown into one bag, and a 2D convex hull is
/// wrapped around the bag.
/// </remarks>
public static class AreaPortalGeometry
{
    /// <summary>An areaportal's hull came out with a suspicious number of vertices.</summary>
    public const string AreaPortalHasManyVerts = "VBSP0309";

    /// <summary>
    /// <c>MAX_MAP_PORTALVERTS</c>: the whole map's
    /// budget for areaportal hull vertices.
    /// </summary>
    public const int MaxMapPortalVerts = 128000;

    /// <summary>The hull vertex count above which stock suspects its own output.</summary>
    public const int SuspiciousVertexCount = 32;

    /// <summary>The fixed size of stock's index and touched arrays.</summary>
    public const int MaxHullPoints = 512;

    /// <summary>
    /// The angle from one direction round to another, always in [0, 2π)
    /// (<c>AngleOffset</c>).
    /// </summary>
    /// <param name="baseAngle">The direction to measure from.</param>
    /// <param name="testAngle">The direction to measure to.</param>
    /// <returns>The clockwise offset in radians.</returns>
    /// <remarks>
    /// The <c>while</c> loop runs at least once even when the test angle is
    /// already below the base, so the answer is taken modulo 2π afterwards.
    /// A test angle far below the base therefore still lands in range, but a
    /// non-finite one spins forever — stock's loop has the same property.
    /// </remarks>
    public static float AngleOffset(float baseAngle, float testAngle)
    {
        while (testAngle > baseAngle)
        {
            testAngle -= 2f * MathF.PI;
        }

        // C's fmod, which is C#'s % on float: truncated division, sign of the
        // dividend. Not Math.IEEERemainder, which rounds to nearest and can
        // come back negative.
        return (baseAngle - testAngle) % (2f * MathF.PI);
    }

    /// <summary>
    /// The indices of the points that are not within
    /// <paramref name="tolerance"/> of an earlier one
    /// (<c>FindUniquePoints</c>).
    /// </summary>
    /// <param name="points">The points to thin.</param>
    /// <param name="indexMap">Filled with the surviving indices.</param>
    /// <param name="tolerance">The collapse distance.</param>
    /// <returns>How many entries of <paramref name="indexMap"/> were filled.</returns>
    /// <exception cref="MapCompileException"><paramref name="indexMap"/> overflowed.</exception>
    public static int FindUniquePoints(
        ReadOnlySpan<(float X, float Y)> points,
        Span<int> indexMap,
        float tolerance)
    {
        float toleranceSqr = tolerance * tolerance;
        int unique = 0;

        for (int i = 0; i < points.Length; i++)
        {
            int j;

            for (j = 0; j < unique; j++)
            {
                if (DistToSqr(points[i], points[indexMap[j]]) < toleranceSqr)
                {
                    break;
                }
            }

            if (j != unique)
            {
                continue;
            }

            if (unique >= indexMap.Length)
            {
                throw new MapCompileException(
                    $"FindUniquePoints: overflowed unique point list (size {indexMap.Length}).");
            }

            indexMap[unique++] = i;
        }

        return unique;
    }

    /// <summary>
    /// The 2D convex hull of a set of points, by gift wrapping
    /// (<c>Convex2D</c>).
    /// </summary>
    /// <param name="points">The points to wrap.</param>
    /// <param name="indices">Filled with indices into <paramref name="points"/>.</param>
    /// <returns>How many entries of <paramref name="indices"/> were filled.</returns>
    /// <remarks>
    /// <para>
    /// The points are collapsed to a unique set at 0.1 first, and stock says
    /// why in a comment: "if we don't collapse the points into a unique set, we
    /// can loop around forever". Two coincident points give the walk two
    /// candidates at the same angle and zero distance and it never closes.
    /// </para>
    /// <para>
    /// Ties in angle go to the point FARTHEST away, unless the near one is the
    /// starting point, which is what stops the walk cutting the loop short
    /// across a straight edge.
    /// </para>
    /// </remarks>
    public static int Convex2D(ReadOnlySpan<(float X, float Y)> points, Span<int> indices)
    {
        if (points.Length == 0)
        {
            return 0;
        }

        Span<int> indexMap = stackalloc int[MaxHullPoints];
        int uniqueCount = FindUniquePoints(points, indexMap, 0.1f);

        // Find the (lower) left side.
        int best = 0;

        for (int i = 1; i < uniqueCount; i++)
        {
            (float X, float Y) candidate = points[indexMap[i]];
            (float X, float Y) incumbent = points[indexMap[best]];

            if (candidate.X < incumbent.X || (candidate.X == incumbent.X && candidate.Y < incumbent.Y))
            {
                best = i;
            }
        }

        indices[0] = indexMap[best];
        int count = 1;

        (float X, float Y) currentEdge = (0f, 1f);

        // Wind around clockwise.
        while (true)
        {
            (float X, float Y) start = points[indices[count - 1]];
            float edgeAngle = DetMathF.Atan2(currentEdge.Y, currentEdge.X);

            int minAngleIndex = -1;
            float minAngle = 5000f;

            for (int i = 0; i < uniqueCount; i++)
            {
                (float X, float Y) to = (points[indexMap[i]].X - start.X, points[indexMap[i]].Y - start.Y);
                float distToSqr = (to.X * to.X) + (to.Y * to.Y);

                if (distToSqr <= 0.1f)
                {
                    continue;
                }

                // Get the angle from the edge to this point.
                float angle = AngleOffset(edgeAngle, DetMathF.Atan2(to.Y, to.X));

                if (MathF.Abs(angle - minAngle) < 0.00001f)
                {
                    // minAngle starts at 5000 and every angle is in [0, 2pi),
                    // so this branch is unreachable on the first candidate and
                    // minAngleIndex is always valid by the time it is taken.
                    // Stock reads pPoints[-1] if it ever were; this does not.
                    float distToTestSqr = minAngleIndex >= 0
                        ? DistToSqr(start, points[minAngleIndex])
                        : 0f;

                    // If the angle is the same, pick the point farthest away.
                    // unless the current one is closing the face loop
                    if (minAngleIndex != indices[0] && distToSqr > distToTestSqr)
                    {
                        minAngle = angle;
                        minAngleIndex = indexMap[i];
                    }
                }
                else if (angle < minAngle)
                {
                    minAngle = angle;
                    minAngleIndex = indexMap[i];
                }
            }

            if (minAngleIndex == -1)
            {
                // Couldn't find a point?
                break;
            }

            if (minAngleIndex == indices[0])
            {
                // Finished.
                break;
            }

            // Add this point.
            if (count >= indices.Length)
            {
                break;
            }

            indices[count] = minAngleIndex;
            count++;

            currentEdge = (
                points[indices[count - 1]].X - points[indices[count - 2]].X,
                points[indices[count - 1]].Y - points[indices[count - 2]].Y);
        }

        return count;
    }

    /// <summary>
    /// Every portal in the tree that separates the two given areas and lies in
    /// the given plane
    /// (<c>FindPortalsLeadingToArea_R</c>).
    /// </summary>
    /// <param name="headNode">The tree root.</param>
    /// <param name="planes">The map's plane table.</param>
    /// <param name="sourceArea">One of the two areas.</param>
    /// <param name="destArea">The other.</param>
    /// <param name="plane">The plane the portals must lie in.</param>
    /// <param name="found">The portals, appended in tree order.</param>
    /// <remarks>
    /// The plane test is a pair of loose comparisons — normals parallel within
    /// 0.01 of a dot product of 1, and the two planes' closest points to the
    /// origin within 0.1 of each other — and it takes the ABSOLUTE dot, so a
    /// portal facing the other way still counts.
    /// </remarks>
    public static void FindPortalsLeadingToArea(
        IBspNode headNode,
        PlaneTable planes,
        int sourceArea,
        int destArea,
        Plane plane,
        List<Portal> found)
    {
        ArgumentNullException.ThrowIfNull(headNode);
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(found);

        if (!headNode.IsLeaf())
        {
            FindPortalsLeadingToArea(headNode.Front!, planes, sourceArea, destArea, plane, found);
            FindPortalsLeadingToArea(headNode.Back!, planes, sourceArea, destArea, plane, found);
            return;
        }

        // Ok.. this is a leaf, check its portals.
        for (Portal? p = headNode.Portals; p is not null;)
        {
            // Stock's `s` here is (nodes[0] == leaf), and the step is next[!s].
            int mine = ReferenceEquals(p.FrontNode, headNode) ? 0 : 1;
            Portal? next = p.NextAt(mine);

            if (p.FrontNode!.Occupied == 0 || p.BackNode!.Occupied == 0)
            {
                p = next;
                continue;
            }

            bool joinsTheTwoAreas =
                (p.BackNode.Area == destArea && p.FrontNode.Area == sourceArea) ||
                (p.FrontNode.Area == destArea && p.BackNode.Area == sourceArea);

            if (!joinsTheTwoAreas)
            {
                p = next;
                continue;
            }

            if (LiesInPlane(planes, p, plane))
            {
                found.Add(p);
            }

            p = next;
        }
    }

    /// <summary>
    /// <c>FindPortalsLeadingToArea_R</c>'s plane test:
    /// the portal's node plane parallel to <paramref name="plane"/> within
    /// 0.01 of an absolute dot of 1, and the two planes' closest points to the
    /// origin within 0.1 of each other.
    /// </summary>
    /// <param name="planes">The map's plane table.</param>
    /// <param name="p">The portal.</param>
    /// <param name="plane">The plane it must lie in.</param>
    /// <returns>Whether it does.</returns>
    internal static bool LiesInPlane(PlaneTable planes, Portal p, Plane plane)
    {
        // Make sure the plane normals point the same way.
        Plane mapPlane = planes[p.OnNode!.PlaneNumber];
        float dot = MathF.Abs(Vec3.Dot(mapPlane.Normal, plane.Normal));

        if (MathF.Abs(1f - dot) < 0.01f)
        {
            Vec3 a = plane.Normal * plane.Dist;
            Vec3 b = mapPlane.Normal * mapPlane.Dist;

            return (a - b).LengthSquared() < 0.01f;
        }

        return false;
    }

    /// <summary>
    /// The convex hull of every portal between two areas in one plane
    /// (<c>EmitClipPortalGeometry</c>).
    /// </summary>
    /// <param name="headNode">The tree root.</param>
    /// <param name="planes">The map's plane table.</param>
    /// <param name="windings">The arena holding the portal windings.</param>
    /// <param name="portal">The portal that divides the two areas.</param>
    /// <param name="sourceArea">The area this areaportal record belongs to.</param>
    /// <param name="otherArea">The area on the other side.</param>
    /// <param name="diagnostics">Where a suspicious hull is reported.</param>
    /// <param name="index">
    /// The tree's portals already grouped by the areas they join
    /// (<see cref="AreaPortalIndex"/>), for a caller that asks for many
    /// areaportals of one unchanged tree; null walks the tree as stock does.
    /// Both give the same portals in the same order.
    /// </param>
    /// <returns>The hull's vertices, in winding order.</returns>
    public static IReadOnlyList<Vec3> ClipPortalGeometry(
        IBspNode headNode,
        PlaneTable planes,
        WindingArena windings,
        Portal portal,
        int sourceArea,
        int otherArea,
        IList<CompileDiagnostic>? diagnostics = null,
        AreaPortalIndex? index = null)
    {
        ArgumentNullException.ThrowIfNull(portal);
        ArgumentNullException.ThrowIfNull(windings);

        // Build a list of all the points in portals from the same original face.
        List<Portal> portals = [];
        if (index is null)
        {
            FindPortalsLeadingToArea(headNode, planes, sourceArea, otherArea, portal.Plane, portals);
        }
        else
        {
            index.FindPortalsLeadingToArea(planes, sourceArea, otherArea, portal.Plane, portals);
        }

        List<Vec3> points = [];

        foreach (Portal pointPortal in portals)
        {
            points.AddRange(windings.Points(pointPortal.Winding));
        }

        return Hull(points, portal.Plane.Normal, diagnostics);
    }

    /// <summary>
    /// The outline vbsp writes for an area portal from the points of the
    /// portals it found in the portal's plane: their convex hull in the
    /// plane, in the order the hull walk leaves them (the second half of
    /// <c>EmitClipPortalGeometry</c>).
    /// </summary>
    /// <param name="points">The points, in the order the portals gave them.</param>
    /// <param name="normal">The portal plane's normal, as the tree holds the plane.</param>
    /// <param name="diagnostics">Where a suspicious hull is reported, or null.</param>
    /// <returns>The hull's points, each one of <paramref name="points"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="points"/> is null.</exception>
    /// <remarks>
    /// Public so a caller that knows an outline without a tree (the room
    /// link's door portals, a rectangle on a cell face) writes it in the
    /// order vbsp would.
    /// </remarks>
    public static IReadOnlyList<Vec3> Hull(IReadOnlyList<Vec3> points, Vec3 normal, IList<CompileDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(points);

        // First transform them into a plane.
        (float Pitch, float Yaw, float Roll) angles = VectorAngles(normal);
        (Vec3 Forward, Vec3 Right, Vec3 Up) basis = AngleVectors(angles);

        // mTransform has the basis vectors as its COLUMNS (VMatrix::SetForward
        // writes m[0][0], m[1][0], m[2][0]), so multiplying by a point gives
        // the dot of each ROW, and the row is one component of each basis
        // vector. The 2D point stock keeps is (y, z) of that product.
        (float X, float Y)[] points2D = new (float, float)[points.Count];

        for (int i = 0; i < points.Count; i++)
        {
            Vec3 v = points[i];
            float y = (basis.Forward.Y * v.X) + (basis.Right.Y * v.Y) + (basis.Up.Y * v.Z);
            float z = (basis.Forward.Z * v.X) + (basis.Right.Z * v.Y) + (basis.Up.Z * v.Z);
            points2D[i] = (y, z);
        }

        // Build the hull.
        Span<int> indices = stackalloc int[MaxHullPoints];
        int count = Convex2D(points2D, indices);

        if (count >= SuspiciousVertexCount)
        {
            diagnostics?.Add(new CompileDiagnostic(
                AreaPortalHasManyVerts,
                DiagnosticSeverity.Warning,
                $"area portal has {count} verts. Could be a vbsp bug."));
        }

        Vec3[] hull = new Vec3[count];

        for (int i = 0; i < count; i++)
        {
            hull[i] = points[indices[i]];
        }

        return hull;
    }

    /// <summary>
    /// A direction as pitch/yaw/roll degrees
    /// (<c>VectorAngles</c>).
    /// </summary>
    /// <param name="forward">The direction.</param>
    /// <returns>Pitch, yaw and roll in degrees; roll is always zero.</returns>
    public static (float Pitch, float Yaw, float Roll) VectorAngles(Vec3 forward)
    {
        float yaw;
        float pitch;

        if (forward.Y == 0f && forward.X == 0f)
        {
            yaw = 0f;
            pitch = forward.Z > 0f ? 270f : 90f;
        }
        else
        {
            yaw = DetMathF.Atan2(forward.Y, forward.X) * 180f / MathF.PI;

            if (yaw < 0f)
            {
                yaw += 360f;
            }

            float tmp = MathF.Sqrt((forward.X * forward.X) + (forward.Y * forward.Y));
            pitch = DetMathF.Atan2(-forward.Z, tmp) * 180f / MathF.PI;

            if (pitch < 0f)
            {
                pitch += 360f;
            }
        }

        return (pitch, yaw, 0f);
    }

    /// <summary>
    /// Pitch/yaw/roll degrees as three basis vectors
    /// (<c>AngleVectors</c>).
    /// </summary>
    /// <param name="angles">Pitch, yaw and roll in degrees.</param>
    /// <returns>The forward, right and up vectors.</returns>
    public static (Vec3 Forward, Vec3 Right, Vec3 Up) AngleVectors((float Pitch, float Yaw, float Roll) angles)
    {
        const float degToRad = MathF.PI / 180f;

        (float sy, float cy) = DetMathF.SinCos(angles.Yaw * degToRad);
        (float sp, float cp) = DetMathF.SinCos(angles.Pitch * degToRad);
        (float sr, float cr) = DetMathF.SinCos(angles.Roll * degToRad);

        Vec3 forward = new(cp * cy, cp * sy, -sp);
        Vec3 right = new(
            (-1f * sr * sp * cy) + (-1f * cr * -sy),
            (-1f * sr * sp * sy) + (-1f * cr * cy),
            -1f * sr * cp);
        Vec3 up = new(
            (cr * sp * cy) + (-sr * -sy),
            (cr * sp * sy) + (-sr * cy),
            cr * cp);

        return (forward, right, up);
    }

    private static float DistToSqr((float X, float Y) a, (float X, float Y) b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }
}
