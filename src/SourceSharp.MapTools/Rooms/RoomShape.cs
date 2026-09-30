//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// A shaped room's <c>SHAP</c> pack section: what the room container does
/// not say about a room that is not a cube of its cell (the rooms design,
/// 17.6 and 17.11).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a section, and why the room container stays as it is.</b> The
/// container (<see cref="RoomObjectStore"/>, <c>SSROOM01</c>) records a
/// room's cell size, kit and sockets, and every cube room's container is
/// what it always was; so a room's height travels beside it, in a section
/// only a shaped room has. A library of cube rooms packs to the bytes it
/// did, and the room cache keeps every cube room's entries (its rows hold a
/// room's finished sections, this one among them for a shaped room).
/// </para>
/// <para>
/// <b>The bytes</b> have the link sections' framing
/// (<see cref="RoomLinkSections"/>: a codec byte, always none, the payload's
/// length as a big-endian <c>int64</c>, then an <c>int32</c> revision,
/// <see cref="Revision"/>). The payload, in big-endian <c>int32</c>s and a
/// little-endian float, as the other room sections write them: the rotation
/// count, always 1 (a room's shape is the same at every turn: the height
/// does not turn, and a one-cell footprint neither); then per rotation its
/// payload's byte length and the payload: the height (a float, a whole
/// number of units), the footprint's width and depth in cells (1 and 1),
/// the socket count and each socket's cell offset in the footprint, in
/// socket order (0 and 0). A larger room (PR 20) will add its per-cell
/// subtree roots and the node runs its link omits after the offsets.
/// </para>
/// <para>
/// <b>The pack version.</b> A pack holding any shaped room is written at
/// version 5 (<see cref="RoomPack.Version"/>), one of cube rooms at version
/// 4 (<see cref="RoomPack.CubeVersion"/>). An older build would skip an
/// unknown tag and link a tall room as a cube, its top tree's bounds one
/// cell tall, so this is the kind of change a version is kept for: an older
/// build refuses the version and never reads the section.
/// </para>
/// <para>
/// <b>Refused</b>, naming the room and the section: a revision this build
/// does not read (read around, the room would be a cube), a rotation count
/// other than 1, a footprint other than one cell (a larger room is PR 20's),
/// a socket count other than the room's, an offset outside the footprint, a
/// height that breaks <see cref="RoomDefinition.HeightProblem"/> for the
/// room's kit, a height equal to the cell size (a cube carries no section),
/// and a section in a pack of a version that cannot hold one.
/// </para>
/// </remarks>
internal static class RoomShape
{
    /// <summary>The tag of a shaped room's section.</summary>
    public const string SectionTag = "SHAP";

    /// <summary>The revision of the section's payload this build writes and reads.</summary>
    public const int Revision = RoomLinkSections.Revision;

    /// <summary>The section for a room, or null for a cube room, which has none.</summary>
    /// <param name="definition">The room.</param>
    /// <returns>The section, or null.</returns>
    public static RoomPackSectionData? ToSection(RoomDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.IsShaped)
        {
            return null;
        }

        RoomLinkSections.Writer turn = new();
        turn.Structs<float>([definition.Height], counted: false);
        turn.Int(1);
        turn.Int(1);
        turn.Int(definition.Sockets.Count);
        for (int s = 0; s < definition.Sockets.Count; s++)
        {
            turn.Int(0);
            turn.Int(0);
        }

        byte[] payload = turn.ToArray();
        RoomLinkSections.Writer w = new();
        w.Int(Revision);
        w.Int(1);
        w.Int(payload.Length);
        w.Raw(payload);
        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None));
    }

    /// <summary>What a room's section records, read from its bytes; null when the room has none.</summary>
    /// <param name="section">The section's bytes, or null.</param>
    /// <param name="room">The room's name, for messages.</param>
    /// <returns>The shape, or null.</returns>
    /// <exception cref="LinkException">
    /// The section is cut short, of a revision this build does not read, or
    /// out of shape (a rotation count other than 1, a footprint other than
    /// one cell, an offset outside it, bytes after its end); each message
    /// names the room and the section.
    /// </exception>
    /// <remarks>
    /// Read before the room's container, so the container's check of its
    /// compile (<see cref="RoomObjectStore"/>) holds the leaves to the room's
    /// own box; what the section says about the room's kit and sockets is
    /// held to the container by <see cref="Apply"/>.
    /// </remarks>
    public static RoomShapeData? Read(ArraySegment<byte>? section, string room)
    {
        if (section is null)
        {
            return null;
        }

        RoomLinkSections.Reader r = RoomLinkSections.Open(section, room, SectionTag)
            ?? throw new LinkException(
                $"room pack entry \"{room}\": its \"{SectionTag}\" section is of a revision this build does not read;"
                + " its shape would be lost, so recompile the library with ssmap room.");
        int rotations = r.Int();
        if (rotations != 1)
        {
            throw r.Mismatch($"{rotations} rotations; a room's shape is stored once");
        }

        int length = r.Count("shape bytes");
        int start = r.Position;
        float height = r.Structs<float>("height", 1, counted: false)[0];
        int width = r.Int();
        int depth = r.Int();
        if (width != 1 || depth != 1)
        {
            throw r.Mismatch(string.Create(CultureInfo.InvariantCulture,
                $"a footprint of {width} x {depth} cells; this build links rooms of one cell"));
        }

        int sockets = r.Int();
        if (sockets < 0 || sockets > 4)
        {
            throw r.Mismatch($"{sockets} sockets; a room of one cell has 0 to 4");
        }

        for (int s = 0; s < sockets; s++)
        {
            int dx = r.Int();
            int dy = r.Int();
            if (dx != 0 || dy != 0)
            {
                throw r.Mismatch(string.Create(CultureInfo.InvariantCulture,
                    $"socket {s} at cell ({dx}, {dy}) of a one-cell footprint"));
            }
        }

        if (r.Position - start != length)
        {
            throw r.Mismatch($"a shape of {r.Position - start} bytes where it records {length}");
        }

        r.End();
        return new RoomShapeData(height, sockets);
    }

    /// <summary>
    /// The room's definition with the height its section records, or the
    /// definition as it is when the room has no section.
    /// </summary>
    /// <param name="shape">The room's shape (<see cref="Read"/>), or null.</param>
    /// <param name="definition">The room's definition from its container.</param>
    /// <returns>The definition, shaped when the section says so.</returns>
    /// <exception cref="LinkException">
    /// The section disagrees with the container: another socket count, the
    /// cell size as a height, or a height the room's kit refuses. The message
    /// names the section, and the pack reader puts the room in front.
    /// </exception>
    public static RoomDefinition Apply(RoomShapeData? shape, RoomDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (shape is not { } data)
        {
            return definition;
        }

        if (data.Sockets != definition.Sockets.Count)
        {
            throw Mismatch($"{data.Sockets} sockets; the room has {definition.Sockets.Count}");
        }

        if (data.Height == definition.CellSize)
        {
            throw Mismatch("the cell size as its height; a cube room has no shape section");
        }

        return RoomDefinition.HeightProblem(data.Height, definition.Kit) is { } problem
            ? throw Mismatch($"room_height {problem.TrimEnd('.')}")
            : definition with { Height = data.Height };

        static LinkException Mismatch(string what) => new($"its \"{SectionTag}\" section holds {what}.");
    }
}

/// <summary>What a room's <c>SHAP</c> section records (<see cref="RoomShape"/>).</summary>
/// <param name="Height">The room's height.</param>
/// <param name="Sockets">How many sockets it lists, each in the one cell.</param>
internal readonly record struct RoomShapeData(float Height, int Sockets);
