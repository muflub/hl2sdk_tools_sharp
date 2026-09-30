//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;
using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// A quarter turn about +z followed by a translation: the only way room
/// geometry is ever moved in VMF form.
/// </summary>
/// <param name="Rotation">Quarter turns counter-clockwise seen from above, 0 to 3.</param>
/// <param name="Offset">The translation added after the turn.</param>
/// <remarks>
/// A quarter turn permutes and negates components, so a point on the
/// integer grid stays on it exactly, and adding an offset that is itself on
/// the grid adds nothing inexact. <see cref="Of"/> takes a placement's
/// <see cref="RoomTransform"/>, which is the same map written with the
/// translation folded in: <c>Apply(p) == RoomTransform.Apply(p)</c> for
/// every point, which a fact holds the two to.
/// </remarks>
internal readonly record struct QuarterTurn(int Rotation, Vec3 Offset)
{
    /// <summary>A pure translation.</summary>
    /// <param name="offset">What to add.</param>
    /// <returns>The transform.</returns>
    public static QuarterTurn Translation(Vec3 offset) => new(0, offset);

    /// <summary>The transform a placement applies to its room.</summary>
    /// <param name="transform">The placement's transform.</param>
    /// <returns>The same map as a turn and an offset.</returns>
    public static QuarterTurn Of(RoomTransform transform) =>
        new(transform.Placement.NormalizedRotation, transform.Apply(Vec3.Zero));

    /// <summary>A direction turned, not moved.</summary>
    /// <param name="v">The direction.</param>
    /// <returns>The turned direction.</returns>
    public Vec3 Rotate(Vec3 v) => (((Rotation % 4) + 4) % 4) switch
    {
        0 => v,
        1 => new Vec3(-v.Y, v.X, v.Z),
        2 => new Vec3(-v.X, -v.Y, v.Z),
        _ => new Vec3(v.Y, -v.X, v.Z),
    };

    /// <summary>A point turned and moved.</summary>
    /// <param name="p">The point.</param>
    /// <returns>The moved point.</returns>
    public Vec3 Apply(Vec3 p) => Rotate(p) + Offset;

    /// <summary>A yaw in degrees turned by the rotation, kept in [0, 360).</summary>
    /// <param name="yaw">The yaw.</param>
    /// <returns>The turned yaw.</returns>
    /// <remarks>The linker turns an entity's yaw the same way (<c>LevelLinker.MoveEntity</c>).</remarks>
    public float TurnYaw(float yaw)
    {
        float turned = (yaw + (90f * (((Rotation % 4) + 4) % 4))) % 360f;
        return turned < 0 ? turned + 360f : turned;
    }
}

/// <summary>
/// Moving VMF brushes and entities by a <see cref="QuarterTurn"/>, and
/// measuring a brush from its text.
/// </summary>
/// <remarks>
/// <para>
/// This is what splitting a room library into rooms and flattening a level
/// into one map are made of. Both work on the VMF text rather than on a
/// loaded <see cref="MapFile"/>, because their output is a VMF that vbsp
/// then compiles like any other: the room library is cut into room-local
/// documents, and the level is copied room by room into world space.
/// </para>
/// <para>
/// A side's three plane points are moved; its texture axes are turned with
/// it and their shifts corrected for the move, so a texture stays where it
/// was on the brush: a texel's coordinate is <c>dot(p, axis) / scale +
/// shift</c>, and for <c>p' = R p + t</c> and <c>axis' = R axis</c> that is
/// unchanged when <c>shift' = shift − dot(t, axis') / scale</c>, which is
/// the correction vbsp applies to a <c>func_instance</c>. An entity's
/// <c>origin</c> is moved and its yaw (<c>angles</c>' second value, or
/// <c>angle</c> unless it is the -1 "up" or -2 "down" code) is turned, as
/// the linker does to a room's compiled entities.
/// </para>
/// </remarks>
internal static class VmfPlacement
{
    /// <summary>A deep copy of a chunk.</summary>
    /// <param name="chunk">The chunk.</param>
    /// <returns>The copy.</returns>
    public static VmfChunk Clone(VmfChunk chunk)
    {
        VmfChunk copy = new(chunk.Name);
        foreach (VmfNode node in chunk.Children)
        {
            copy.Children.Add(node switch
            {
                VmfKey key => new VmfKey(key.Name, key.Value),
                VmfChunk child => Clone(child),
                _ => throw new InvalidOperationException($"a VMF node of type {node.GetType().Name}"),
            });
        }

        return copy;
    }

    /// <summary>
    /// A brush's extent, from the points its sides' planes are written
    /// through.
    /// </summary>
    /// <param name="solid">The <c>solid</c> chunk.</param>
    /// <returns>The box around every plane point.</returns>
    /// <exception cref="RoomLibraryException">The brush has no sides, or a plane is not three points.</exception>
    /// <remarks>
    /// Hammer writes each side's plane through three of that face's corners,
    /// and every corner of a brush is a corner of some face, so for an
    /// editor's brush this is the brush's own box. Nothing is compiled to
    /// get it, which is what lets a room library be split, and a level
    /// generated from it, without a game to load materials from.
    /// </remarks>
    public static Box Bounds(VmfChunk solid)
    {
        Vec3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vec3 hi = new(float.MinValue, float.MinValue, float.MinValue);
        bool any = false;
        foreach (VmfChunk side in solid.GetChunks(MapFileLoader.SideChunk))
        {
            foreach (Vec3 p in PlanePoints(side.GetValue("plane"), solid))
            {
                lo = new Vec3(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z));
                hi = new Vec3(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z));
                any = true;
            }
        }

        if (!any)
        {
            throw new RoomLibraryException($"brush {IdOf(solid)} has no sides.");
        }

        return new Box(lo, hi);
    }

    /// <summary>A brush moved: a copy whose planes and texture axes are in the new place.</summary>
    /// <param name="solid">The <c>solid</c> chunk.</param>
    /// <param name="turn">The move.</param>
    /// <returns>The moved copy.</returns>
    /// <exception cref="RoomLibraryException">The brush is malformed, or a displacement's keys are.</exception>
    /// <remarks>
    /// A displacement side's <c>dispinfo</c> moves with it
    /// (<see cref="MoveDispInfo"/>): its start position as a point, its
    /// normals and offsets as directions.
    /// </remarks>
    public static VmfChunk MoveSolid(VmfChunk solid, QuarterTurn turn)
    {
        VmfChunk moved = new(solid.Name);
        foreach (VmfNode node in solid.Children)
        {
            if (node is VmfKey key)
            {
                moved.Children.Add(new VmfKey(key.Name, key.Value));
                continue;
            }

            VmfChunk child = (VmfChunk)node;
            if (!string.Equals(child.Name, MapFileLoader.SideChunk, StringComparison.OrdinalIgnoreCase))
            {
                moved.Children.Add(Clone(child));
                continue;
            }

            moved.Children.Add(MoveSide(child, solid, turn));
        }

        return moved;
    }

    /// <summary>The class of the sun: the one light whose direction is the library's, not the room's.</summary>
    internal const string SunClass = "light_environment";

    /// <summary>
    /// Whether an entity's angles are a world direction that a placement's
    /// turn must leave alone: the sun's.
    /// </summary>
    /// <param name="classname">The entity's class, or null when it has none.</param>
    /// <returns>True for <see cref="SunClass"/>, matched exactly as vbsp matches classes.</returns>
    /// <remarks>
    /// All rooms of a library share one sun (the owner's decision D3 in the
    /// rooms design): the sun is fixed in the world, and a room turned by a
    /// quarter turn is lit by the same sun from the same side of the sky as
    /// every other room. Turning a room's <c>light_environment</c> with the
    /// room would give each turned placement its own sun, which a whole map
    /// cannot have. So its <c>angles</c> and <c>angle</c> are carried as
    /// written (its <c>pitch</c> never turns, being no yaw), by the linker
    /// (<c>LevelLinker.MoveEntity</c>), the split and the flatten alike; its
    /// origin still moves, since it says only where the entity stands.
    /// Whether a room may carry its own sun at all, rather than the library
    /// holding the one, is a pack-time rule of its own.
    /// </remarks>
    internal static bool KeepsWorldAngles(string? classname) => string.Equals(classname, SunClass, StringComparison.Ordinal);

    /// <summary>The key an <c>info_overlay</c> is placed by: the point its basis stands on.</summary>
    private const string OverlayOriginKey = "BasisOrigin";

    /// <summary>An entity moved: its origin, its yaw, its overlay basis and its brushes.</summary>
    /// <param name="entity">The <c>entity</c> chunk.</param>
    /// <param name="turn">The move.</param>
    /// <returns>The moved copy.</returns>
    /// <exception cref="RoomLibraryException">A placement key or a brush is malformed.</exception>
    /// <remarks>
    /// <para>
    /// <b>Overlays are placed by their basis, not their origin.</b> vbsp
    /// builds an <c>info_overlay</c> at <c>BasisOrigin</c>, oriented by
    /// <c>BasisU</c>, <c>BasisV</c> and <c>BasisNormal</c>; its <c>origin</c>
    /// is only where the editor draws it. So <c>BasisOrigin</c> is moved as a
    /// point and the three axes are turned as directions (a translation does
    /// not change a direction). The <c>uv0</c> to <c>uv3</c> corners are in
    /// the overlay's own basis and stay as written, and so does its handedness:
    /// a quarter turn about +z is a proper rotation.
    /// </para>
    /// <para>
    /// <b>Brush entities</b> follow the rule the link follows
    /// (<see cref="BrushEntityDirections"/>): their brushes are turned, so
    /// their <c>angles</c> and <c>angle</c> turn only for a class that reads
    /// them as a direction and are carried as written otherwise, and their
    /// direction keys (<c>movedir</c>, <c>pushdir</c>, <c>gibdir</c>) turn as
    /// a yaw. A class vbsp consumes keeps the point entity's rule: its angles
    /// mean nothing to the map it compiles to.
    /// </para>
    /// </remarks>
    public static VmfChunk MoveEntity(VmfChunk entity, QuarterTurn turn)
    {
        string? className = entity.GetValue("classname");
        int turns = KeepsWorldAngles(className) ? 0 : ((turn.Rotation % 4) + 4) % 4;
        bool brush = BrushEntityDirections.IsBrushEntity(entity) && !BrushEntityDirections.IsConsumed(className);
        int angleTurns = brush && !BrushEntityDirections.ReadsAnglesAsDirection(className) ? 0 : turns;
        VmfChunk moved = new(entity.Name);
        foreach (VmfNode node in entity.Children)
        {
            if (node is VmfChunk child)
            {
                moved.Children.Add(string.Equals(child.Name, MapFileLoader.SolidChunk, StringComparison.OrdinalIgnoreCase)
                    ? MoveSolid(child, turn)
                    : Clone(child));
                continue;
            }

            VmfKey key = (VmfKey)node;
            string value = key.Value;
            if (IsKey(key.Name, "origin") || IsKey(key.Name, OverlayOriginKey))
            {
                value = Format(turn.Apply(Vector(value, key.Name, entity)));
            }
            else if (turns != 0 && (IsKey(key.Name, "BasisU") || IsKey(key.Name, "BasisV") || IsKey(key.Name, "BasisNormal")))
            {
                value = Format(turn.Rotate(Vector(value, key.Name, entity)));
            }
            else if (angleTurns != 0 && IsKey(key.Name, "angles"))
            {
                Vec3 angles = Vector(value, "angles", entity);
                value = Format(new Vec3(angles.X, turn.TurnYaw(angles.Y), angles.Z));
            }
            else if (brush && turns != 0 && BrushEntityDirections.IsDirectionKey(key.Name))
            {
                Vec3 direction = Vector(value, key.Name, entity);
                value = Format(new Vec3(direction.X, turn.TurnYaw(direction.Y), direction.Z));
            }
            else if (angleTurns != 0 && IsKey(key.Name, "angle"))
            {
                float yaw = Number(value, "angle", entity);
                value = yaw is -1f or -2f ? value : Format(turn.TurnYaw(yaw));
            }

            moved.Children.Add(new VmfKey(key.Name, value));
        }

        return moved;
    }

    /// <summary>A point entity's origin, or null when it has none.</summary>
    /// <param name="entity">The <c>entity</c> chunk.</param>
    /// <returns>The origin.</returns>
    /// <exception cref="RoomLibraryException">The origin is not three numbers.</exception>
    public static Vec3? Origin(VmfChunk entity) =>
        entity.GetValue("origin") is { } text ? Vector(text, "origin", entity) : null;

    /// <summary>A number as a VMF writes it: shortest round-trip, invariant, no negative zero.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The text.</returns>
    public static string Format(float value) =>
        (value == 0f ? 0f : value).ToString(CultureInfo.InvariantCulture);

    /// <summary>Three numbers, space-separated.</summary>
    /// <param name="v">The vector.</param>
    /// <returns>The text.</returns>
    public static string Format(Vec3 v) => $"{Format(v.X)} {Format(v.Y)} {Format(v.Z)}";

    private static VmfChunk MoveSide(VmfChunk side, VmfChunk solid, QuarterTurn turn)
    {
        VmfChunk moved = new(side.Name);
        foreach (VmfNode node in side.Children)
        {
            if (node is VmfChunk child)
            {
                moved.Children.Add(string.Equals(child.Name, DispInfoChunk, StringComparison.OrdinalIgnoreCase)
                    ? MoveDispInfo(child, solid, turn)
                    : Clone(child));
                continue;
            }

            VmfKey key = (VmfKey)node;
            string value = key.Value;
            if (IsKey(key.Name, "plane"))
            {
                StringBuilder text = new();
                foreach (Vec3 p in PlanePoints(value, solid))
                {
                    text.Append(text.Length == 0 ? "(" : " (").Append(Format(turn.Apply(p))).Append(')');
                }

                value = text.ToString();
            }
            else if (IsKey(key.Name, "uaxis") || IsKey(key.Name, "vaxis"))
            {
                value = MoveAxis(value, turn, solid);
            }

            moved.Children.Add(new VmfKey(key.Name, value));
        }

        return moved;
    }

    /// <summary>A side's displacement chunk.</summary>
    internal const string DispInfoChunk = "dispinfo";

    /// <summary>The rows of a <c>dispinfo</c> that hold one direction per vertex.</summary>
    private static readonly ImmutableArray<string> DispDirectionRows = ["normals", "offsets", "offset_normals"];

    /// <summary>
    /// A displacement moved with its side: the <c>startposition</c> point
    /// through the whole move, every vector of its direction rows turned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// vbsp builds a displacement over its side's four corners, starting at
    /// the corner nearest <c>startposition</c>, and places each vertex at its
    /// point of that quad plus <c>normals</c> times <c>distances</c> plus
    /// <c>offsets</c>. The side's corners are moved with its plane
    /// (<see cref="MoveSide"/>), so the start position moves as a point and
    /// the per-vertex vectors turn as directions; that keeps the start corner
    /// the same corner of the quad, and so every vertex, row and triangle
    /// where it was on the surface. <c>offset_normals</c>, which vbsp does
    /// not read (Hammer keeps them for its own editing), turn too, so the
    /// moved file edits as the room did.
    /// </para>
    /// <para>
    /// Distances, alphas, triangle tags, allowed vertices, power, flags and
    /// the rest are the same on the moved surface and are copied. A quarter
    /// turn permutes and negates components, so every turned number is
    /// exact; each is written in its shortest round-trip spelling
    /// (<see cref="Format(float)"/>), which vbsp reads back to the same
    /// float.
    /// </para>
    /// </remarks>
    private static VmfChunk MoveDispInfo(VmfChunk dispinfo, VmfChunk solid, QuarterTurn turn)
    {
        VmfChunk moved = new(dispinfo.Name);
        foreach (VmfNode node in dispinfo.Children)
        {
            if (node is VmfChunk rows)
            {
                moved.Children.Add(DispDirectionRows.Any(r => string.Equals(rows.Name, r, StringComparison.OrdinalIgnoreCase))
                    ? TurnRows(rows, turn, solid)
                    : Clone(rows));
                continue;
            }

            VmfKey key = (VmfKey)node;
            string value = key.Value;
            if (IsKey(key.Name, "startposition"))
            {
                value = $"[{Format(turn.Apply(Bracketed(value, key.Name, solid)))}]";
            }

            moved.Children.Add(new VmfKey(key.Name, value));
        }

        return moved;
    }

    /// <summary>A <c>dispinfo</c> row chunk of vectors (<c>rowN</c> keys, three numbers a vertex), every vector turned.</summary>
    private static VmfChunk TurnRows(VmfChunk rows, QuarterTurn turn, VmfChunk solid)
    {
        VmfChunk moved = new(rows.Name);
        foreach (VmfNode node in rows.Children)
        {
            if (node is not VmfKey key)
            {
                moved.Children.Add(Clone((VmfChunk)node));
                continue;
            }

            string[] numbers = key.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (numbers.Length % 3 != 0)
            {
                throw new RoomLibraryException(
                    $"brush {IdOf(solid)} has a displacement {rows.Name} {key.Name} of {numbers.Length} numbers, not three per vertex.");
            }

            StringBuilder text = new();
            for (int i = 0; i < numbers.Length; i += 3)
            {
                Vec3 v = turn.Rotate(new Vec3(
                    Number(numbers[i], rows.Name, solid),
                    Number(numbers[i + 1], rows.Name, solid),
                    Number(numbers[i + 2], rows.Name, solid)));
                text.Append(i == 0 ? string.Empty : " ").Append(Format(v));
            }

            moved.Children.Add(new VmfKey(key.Name, text.ToString()));
        }

        return moved;
    }

    /// <summary>Three numbers in brackets, <c>[x y z]</c>, as a displacement writes its start position.</summary>
    private static Vec3 Bracketed(string text, string key, VmfChunk solid)
    {
        string trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[^1] != ']')
        {
            throw new RoomLibraryException($"brush {IdOf(solid)} has a displacement {key} \"{text}\", not \"[x y z]\".");
        }

        return Vector(trimmed[1..^1], key, solid);
    }

    /// <summary>A texture axis <c>[x y z shift] scale</c>, turned and re-shifted.</summary>
    private static string MoveAxis(string text, QuarterTurn turn, VmfChunk solid)
    {
        int open = text.IndexOf('[', StringComparison.Ordinal);
        int close = text.IndexOf(']', StringComparison.Ordinal);
        if (open < 0 || close < open)
        {
            throw new RoomLibraryException($"brush {IdOf(solid)} has a texture axis \"{text}\", not \"[x y z shift] scale\".");
        }

        string[] inside = text[(open + 1)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] after = text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (inside.Length != 4 || after.Length != 1)
        {
            throw new RoomLibraryException($"brush {IdOf(solid)} has a texture axis \"{text}\", not \"[x y z shift] scale\".");
        }

        Vec3 axis = turn.Rotate(new Vec3(
            Number(inside[0], "texture axis", solid),
            Number(inside[1], "texture axis", solid),
            Number(inside[2], "texture axis", solid)));
        float shift = Number(inside[3], "texture axis", solid);
        float scale = Number(after[0], "texture axis", solid);
        if (scale != 0f)
        {
            shift -= Vec3.Dot(turn.Offset, axis) / scale;
        }

        return $"[{Format(axis)} {Format(shift)}] {after[0]}";
    }

    private static IEnumerable<Vec3> PlanePoints(string? plane, VmfChunk solid)
    {
        if (plane is null)
        {
            throw new RoomLibraryException($"brush {IdOf(solid)} has a side with no plane.");
        }

        string[] parts = plane.Split(')', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            throw new RoomLibraryException($"brush {IdOf(solid)} has a plane \"{plane}\", not three points.");
        }

        Vec3[] points = new Vec3[3];
        for (int i = 0; i < 3; i++)
        {
            points[i] = Vector(parts[i].TrimStart('('), "plane", solid);
        }

        return points;
    }

    private static Vec3 Vector(string text, string key, VmfChunk owner)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            throw new RoomLibraryException($"{What(owner)}: {key} \"{text}\" is not three numbers.");
        }

        return new Vec3(Number(parts[0], key, owner), Number(parts[1], key, owner), Number(parts[2], key, owner));
    }

    private static float Number(string text, string key, VmfChunk owner) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value)
            ? value
            : throw new RoomLibraryException($"{What(owner)}: {key} \"{text}\" is not a number.");

    private static string What(VmfChunk owner) =>
        string.Equals(owner.Name, MapFileLoader.SolidChunk, StringComparison.OrdinalIgnoreCase)
            ? $"brush {IdOf(owner)}"
            : $"entity {IdOf(owner)} ({owner.GetValue("classname") ?? "no classname"})";

    /// <summary>A chunk's <c>id</c> key, for messages.</summary>
    internal static string IdOf(VmfChunk chunk) => chunk.GetValue("id") ?? "without an id";

    private static bool IsKey(string key, string name) => string.Equals(key, name, StringComparison.OrdinalIgnoreCase);
}
