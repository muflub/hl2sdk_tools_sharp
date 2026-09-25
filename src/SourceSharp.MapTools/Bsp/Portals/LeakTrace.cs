using System.Collections.Immutable;
using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Bsp.Portals;

/// <summary>
/// The leak line: the shortest chain of portals from outside the map to the
/// entity that got out (<c>src/utils/vbsp/leakfile.cpp</c>).
/// </summary>
/// <remarks>
/// <para>
/// The chain falls out of the hop numbers the entity flood already wrote. Start
/// at the outside leaf, which carries the largest number on the path, and at
/// each step take the portal whose far leaf carries the smallest number below
/// the current one. That walks strictly downhill to the leaf numbered 1, which
/// is the one the entity was placed in.
/// </para>
/// <para>
/// This produces a <see cref="LeakReport"/>; writing the <c>.lin</c> file is
/// <see cref="Write"/>, a separate step, because a host that wants to draw the
/// leak should not have to parse a file the compiler just wrote.
/// </para>
/// </remarks>
public static class LeakTrace
{
    /// <summary>An entity reached the void.</summary>
    public const string MapLeaked = "VBSP0303";

    /// <summary>An areaportal brush does not separate two areas.</summary>
    public const string AreaportalLeaked = "VBSP0304";

    /// <summary>
    /// Traces the leak from the outside leaf back to the entity
    /// (<c>LeakFile</c>, <c>leakfile.cpp:31</c>).
    /// </summary>
    /// <param name="tree">The flooded tree.</param>
    /// <param name="windings">The arena holding the portal windings.</param>
    /// <param name="entities">The map's entities, to name the occupant.</param>
    /// <returns>
    /// The leak path, or <see langword="null"/> when the flood never got out.
    /// </returns>
    /// <remarks>
    /// <see cref="LeakReport.EntityId"/> carries the entity's index in
    /// <paramref name="entities"/> — stock's <c>entitynum</c>, which is what
    /// every other vbsp message about an entity reports — or -1 if the occupant
    /// is not in that list.
    /// </remarks>
    public static LeakReport? Trace(IBspTree tree, WindingArena windings, IReadOnlyList<MapEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(windings);
        ArgumentNullException.ThrowIfNull(entities);

        if (tree.OutsideNode.Occupied == 0)
        {
            return null;
        }

        tree.Leaked = true;

        ImmutableArray<(float X, float Y, float Z)>.Builder path =
            ImmutableArray.CreateBuilder<(float X, float Y, float Z)>();

        IBspNode node = tree.OutsideNode;

        while (node.Occupied > 1)
        {
            (Portal? nextPortal, IBspNode? nextNode) = BestExit(node);

            // Stock dereferences both without checking; a null here would be a
            // broken flood, not a broken map.
            node = nextNode!;
            Vec3 mid = windings.Center(nextPortal!.Winding);
            path.Add((mid.X, mid.Y, mid.Z));
        }

        // Add the occupant's origin to the leakfile.
        //
        // StockQuirk.LeakFileUnnudgedOrigin. leakfile.cpp:82 re-reads the
        // `origin` key, so stock's last point is a place the flood never
        // stood: FloodEntities raised it by one unit in z before placing the
        // occupant (portals.cpp:765) and may have moved it on the 16-unit grid
        // as well. The line is therefore drawn from the wrong end.
        MapEntity? occupant = node.Occupant;
        Vec3 keyOrigin = occupant?.GetVectorForKey("origin") ?? Vec3.Zero;
        Vec3 origin = windings.Compliance.Emulates(StockQuirk.LeakFileUnnudgedOrigin)
            ? keyOrigin
            : occupant?.FloodOrigin ?? keyOrigin;

        path.Add((origin.X, origin.Y, origin.Z));

        string className = occupant?.ValueForKey("classname") ?? string.Empty;
        int entityId = occupant is null ? -1 : IndexOf(entities, occupant);

        return new LeakReport(entityId, className, path.ToImmutable());
    }

    /// <summary>
    /// The diagnostic stock spews in red beside the <c>.lin</c>
    /// (<c>leakfile.cpp:92</c>).
    /// </summary>
    /// <param name="report">The traced leak.</param>
    /// <returns>A warning naming the entity and where it was.</returns>
    public static CompileDiagnostic Diagnose(LeakReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        (float X, float Y, float Z) origin = report.Path[^1];

        return new CompileDiagnostic(
            MapLeaked,
            DiagnosticSeverity.Error,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Entity {report.ClassName} ({origin.X:F2} {origin.Y:F2} {origin.Z:F2}) leaked!"),
            new MapLocation(EntityId: report.EntityId, Position: origin));
    }

    /// <summary>
    /// The leak line for an areaportal that failed to separate two areas
    /// (<c>AreaportalLeakFile</c>, <c>leakfile.cpp:95</c>).
    /// </summary>
    /// <param name="tree">The tree; skipped entirely if it has already leaked.</param>
    /// <param name="windings">The arena holding the portal windings.</param>
    /// <param name="startPortal">The portal out of the areaportal into open space.</param>
    /// <param name="endPortal">The portal into the longest path around it.</param>
    /// <param name="start">The leaf on the far side of <paramref name="startPortal"/>.</param>
    /// <returns>The path, or <see langword="null"/> when a real leak was written first.</returns>
    /// <remarks>
    /// The loop in the middle of this almost never runs. The flood that filled
    /// in the hop numbers starts at 2, and the loop only steps to a leaf
    /// numbered strictly below the current one, so from a start leaf numbered 2
    /// there is nowhere to go and the path is the four fixed points: the far
    /// portal's centre, the start leaf's centre twice over, and the near
    /// portal's centre.
    /// </remarks>
    public static LeakReport? TraceAreaportal(
        IBspTree tree,
        WindingArena windings,
        Portal startPortal,
        Portal endPortal,
        IBspNode start)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(windings);
        ArgumentNullException.ThrowIfNull(startPortal);
        ArgumentNullException.ThrowIfNull(endPortal);
        ArgumentNullException.ThrowIfNull(start);

        // wrote a leak line file already, don't overwrite it with the
        // areaportal leak file
        if (tree.Leaked)
        {
            return null;
        }

        tree.Leaked = true;

        ImmutableArray<(float X, float Y, float Z)>.Builder path =
            ImmutableArray.CreateBuilder<(float X, float Y, float Z)>();

        Vec3 mid = windings.Center(endPortal.Winding);
        path.Add((mid.X, mid.Y, mid.Z));

        mid = Midpoint(start);
        path.Add((mid.X, mid.Y, mid.Z));

        IBspNode? node = start;

        while (node is not null && node.Occupied >= 1)
        {
            (Portal? nextPortal, IBspNode? nextNode) = BestExit(node);

            if (nextNode is null)
            {
                break;
            }

            node = nextNode;
            mid = windings.Center(nextPortal!.Winding);
            path.Add((mid.X, mid.Y, mid.Z));
        }

        // add the occupant center
        if (node is not null)
        {
            mid = Midpoint(node);
            path.Add((mid.X, mid.Y, mid.Z));
        }

        mid = windings.Center(startPortal.Winding);
        path.Add((mid.X, mid.Y, mid.Z));

        return new LeakReport(-1, "func_areaportal", path.ToImmutable());
    }

    /// <summary>
    /// Renders a leak path as a <c>.lin</c> file
    /// (<c>leakfile.cpp:76</c>: one <c>"%f %f %f"</c> line per point).
    /// </summary>
    /// <param name="report">The leak to render.</param>
    /// <param name="lineEnding">
    /// <see cref="PortalLineEnding.CrLf"/> matches a Windows toolset, which is
    /// what the reference <c>.lin</c> files were made with;
    /// <see cref="PortalLineEnding.Lf"/> matches a Linux one. Stock opens the
    /// file in text mode and lets the platform decide.
    /// </param>
    /// <returns>The file's bytes.</returns>
    public static byte[] Write(LeakReport report, PortalLineEnding lineEnding = PortalLineEnding.CrLf)
    {
        ArgumentNullException.ThrowIfNull(report);

        string newline = lineEnding == PortalLineEnding.CrLf ? "\r\n" : "\n";
        StringBuilder output = new();

        foreach ((float X, float Y, float Z) point in report.Path)
        {
            output.Append(FormatF6(point.X)).Append(' ');
            output.Append(FormatF6(point.Y)).Append(' ');
            output.Append(FormatF6(point.Z)).Append(newline);
        }

        return Encoding.Latin1.GetBytes(output.ToString());
    }

    /// <summary>
    /// C's <c>%f</c> on a float promoted to double: six decimal places.
    /// </summary>
    /// <param name="value">The coordinate.</param>
    /// <returns>Its text.</returns>
    /// <remarks>
    /// The promotion matters. <c>fprintf</c> takes a <c>vec_t</c> through
    /// <c>...</c>, which widens it to double, so the digits printed are the
    /// digits of the float's exact value and not of some shorter decimal that
    /// happens to round to it — 0.1f prints as 0.100000 either way, but a
    /// coordinate near the end of a winding does not.
    /// </remarks>
    private static string FormatF6(float value)
    {
        double d = value;

        if (double.IsNaN(d))
        {
            return "nan";
        }

        if (double.IsPositiveInfinity(d))
        {
            return "inf";
        }

        if (double.IsNegativeInfinity(d))
        {
            return "-inf";
        }

        return d.ToString("F6", CultureInfo.InvariantCulture);
    }

    private static Vec3 Midpoint(IBspNode node) =>
        new(
            0.5f * (node.Mins.X + node.Maxs.X),
            0.5f * (node.Mins.Y + node.Maxs.Y),
            0.5f * (node.Mins.Z + node.Maxs.Z));

    private static int IndexOf(IReadOnlyList<MapEntity> entities, MapEntity entity)
    {
        for (int i = 0; i < entities.Count; i++)
        {
            if (ReferenceEquals(entities[i], entity))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The portal out of this leaf whose far side carries the lowest hop count
    /// below this leaf's own.
    /// </summary>
    private static (Portal? Portal, IBspNode? Node) BestExit(IBspNode node)
    {
        Portal? nextPortal = null;
        IBspNode? nextNode = null;
        int next = node.Occupied;

        for (Portal? p = node.Portals; p is not null;)
        {
            int mine = ReferenceEquals(p.FrontNode, node) ? 0 : 1;
            IBspNode other = p.NodeAt(1 - mine)!;

            if (other.Occupied != 0 && other.Occupied < next)
            {
                nextPortal = p;
                nextNode = other;
                next = other.Occupied;
            }

            p = p.NextAt(mine);
        }

        return (nextPortal, nextNode);
    }
}
