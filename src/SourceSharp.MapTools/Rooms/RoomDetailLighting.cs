//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Rad.Props;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// One vrad pass's lighting of a room's detail props: each prop's colour
/// and style count, in the room's lump order, and the style runs the pass
/// appended, in the same order.
/// </summary>
/// <param name="Colors">Each prop's colour as vrad encoded it (the style 0 light).</param>
/// <param name="Counts">Each prop's style count: how many of <paramref name="Styles"/> are its run.</param>
/// <param name="Styles">Every prop's run, one after another in prop order (a prop without styles has none).</param>
/// <remarks>
/// <para>
/// <b>Why per pass.</b> vrad lights a map's detail props once per range. Each
/// pass writes every prop's colour and count, and, for a prop with styles,
/// the start of the run it appends to that range's style lump (<c>dplt</c>
/// for LDR, <c>dplh</c> for HDR); a prop without styles keeps the start it
/// had. So after <c>-both</c> a map holds the HDR pass's colours and counts
/// and a mix of the two passes' starts, and the LDR pass's runs cannot be
/// told apart in its lump (runs of two props run together). The link
/// replays the passes over the level's props in their linked order, as vrad
/// of the linked map would, so it needs each pass as the pass gave it.
/// </para>
/// <para>
/// <b>Encoded, not linear.</b> Unlike the luxels (PR 9's linear halves, which
/// the door light sums onto), the colours are stored as vrad's
/// <c>ColorRGBExp32</c> bytes: no door term reaches a detail prop, and the
/// bytes are what the linked map holds. A later sum decodes them exactly:
/// an 8-bit mantissa times a power of two is exact in a float, where a half
/// would lose a very dim style's value.
/// </para>
/// </remarks>
internal sealed record RoomDetailLight(ColorRgbExp32[] Colors, byte[] Counts, DetailPropLightstylesLump[] Styles)
{
    /// <summary>The prop count the pass lit.</summary>
    public int Count => Colors.Length;

    /// <summary>
    /// A pass's result as the bake keeps it. The runs are the pass's style
    /// lump as it is: vrad appends each prop's run in prop order, so the
    /// lump is the runs one after another.
    /// </summary>
    /// <param name="result">The pass's detail prop lighting.</param>
    /// <returns>The pass's lighting.</returns>
    public static RoomDetailLight From(DetailPropLightingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new RoomDetailLight(
            [.. result.Props.Select(p => p.Lighting)],
            [.. result.Props.Select(p => p.LightStyleCount)],
            result.LightStyles);
    }

    /// <summary>Where each prop's run starts in <see cref="Styles"/>, and the total after the last.</summary>
    public int[] Starts()
    {
        int[] starts = new int[Counts.Length + 1];
        for (int i = 0; i < Counts.Length; i++)
        {
            starts[i + 1] = starts[i] + Counts[i];
        }

        return starts;
    }
}

/// <summary>
/// A lit room's detail prop lighting as its pack section: every stored
/// turn's passes (<see cref="RoomDetailLight"/>) and, when the room records
/// its door light, its detail props' responses (<see cref="DoorResponseDetail"/>),
/// beside the room's <c>LITE</c> and <c>DLIT</c> sections, whose bytes it
/// leaves as they were.
/// </summary>
/// <remarks>
/// <para>
/// <b>Once or four times.</b> The base part follows the room's base bake
/// (the rooms design, 1.1 and 9.4): one payload for a room no sun or sky
/// reaches, four for one it reaches, one per stored turn of the
/// <c>LITE</c> section, which it must match. A detail prop's lighting is a
/// colour and a style run with no direction in it, so a turn changes none
/// of its bytes; the four payloads of a sunlit room differ because the sun
/// is fixed in the world. The responses are stored once, as the door
/// light's are (O14).
/// </para>
/// <para>
/// <b>The section</b> (<see cref="SectionTag"/>, <c>DPLT</c>) has the link
/// sections' framing (<see cref="RoomLinkSections"/>: codec byte, decoded
/// length, revision; codec none). After the revision, big-endian
/// <c>int32</c>s: the prop count (the room's lump's) and the payload count
/// (the <c>LITE</c> section's); then per payload a flag byte (1 LDR, 2 HDR,
/// the ranges the <c>LITE</c> payload lit) and per range every prop's
/// colour (four bytes), every prop's style count (a byte), and the runs
/// (their count, then five bytes each: the colour and the style). Then a
/// byte, 1 when the door part follows (the room records its door light):
/// a byte, 1 when the receivers follow (every prop's lighting centre and up
/// vector, three floats each, then the socket count and per socket which
/// opening cells each prop sees, coded as the door light's receivers are),
/// then per range of the door light, the socket count and per socket the
/// emitter count (none or <see cref="DoorLightMath.EmitterCount"/>)
/// and per emitter the props it reached (their count, then each prop's
/// index and three halves). A room whose bake lit no detail prop has no
/// section; a section that does not fit the room is refused as damaged.
/// The tag is one an older build skips, and such a build refuses a room
/// with detail props by its lump, so the pack's format version is unchanged.
/// </para>
/// </remarks>
internal static class RoomDetailLighting
{
    /// <summary>The tag of a room's detail prop lighting section.</summary>
    public const string SectionTag = "DPLT";

    /// <summary>The revision this build writes and reads: the link sections' own.</summary>
    public const int Revision = RoomLinkSections.Revision;

    /// <summary>Whether any pass of any stored turn of a room's lighting lit its detail props.</summary>
    public static bool Has(RoomLighting lighting)
    {
        ArgumentNullException.ThrowIfNull(lighting);
        return lighting.Payloads.Any(p => p.Ldr?.Detail is not null || p.Hdr?.Detail is not null);
    }

    /// <summary>The section holding a room's detail prop lighting, or null when its bake lit none.</summary>
    /// <param name="lighting">The room's lighting.</param>
    /// <param name="door">The room's door light, whose detail responses the section stores; null for a room without door light.</param>
    /// <returns>The section, tagged <see cref="SectionTag"/>, or null.</returns>
    /// <exception cref="ArgumentException">A payload lit its detail props in one range and not in another.</exception>
    public static RoomPackSectionData? ToSection(RoomLighting lighting, RoomDoorLight? door = null)
    {
        if (!Has(lighting))
        {
            return null;
        }

        RoomLinkSections.Writer w = new();
        w.Int(Revision);
        w.Int(lighting.Payloads.Select(p => (p.Ldr ?? p.Hdr)?.Detail?.Count).First(c => c is not null)!.Value);
        w.Int(lighting.Payloads.Length);
        foreach (RoomLightingPayload payload in lighting.Payloads)
        {
            w.Byte((byte)((payload.Ldr is null ? 0 : 1) | (payload.Hdr is null ? 0 : 2)));
            foreach (RoomLightRange? range in (ReadOnlySpan<RoomLightRange?>)[payload.Ldr, payload.Hdr])
            {
                if (range is null)
                {
                    continue;
                }

                RoomDetailLight detail = range.Detail
                    ?? throw new ArgumentException("a lit range without its detail prop lighting beside one with it", nameof(lighting));
                w.Structs<ColorRgbExp32>(detail.Colors, counted: false);
                w.Raw(detail.Counts);
                w.Structs<DetailPropLightstylesLump>(detail.Styles);
            }
        }

        w.Byte(door is null ? (byte)0 : (byte)1);
        if (door is not null)
        {
            w.Byte(door.Details is null ? (byte)0 : (byte)1);
            if (door.Details is { } receivers)
            {
                w.Structs<Vec3>(receivers.Centres, counted: false);
                w.Structs<Vec3>(receivers.Normals, counted: false);
                w.Int(receivers.Seen.Length);
                foreach (DoorSeen seen in receivers.Seen)
                {
                    RoomDoorLight.WriteSeen(w, seen);
                }
            }

            foreach (DoorLightRange? range in (ReadOnlySpan<DoorLightRange?>)[door.Ldr, door.Hdr])
            {
                if (range is null)
                {
                    continue;
                }

                w.Int(range.Responses.Length);
                foreach (DoorResponseEmitter[] emitters in range.Responses)
                {
                    w.Int(emitters.Length);
                    foreach (DoorResponseEmitter emitter in emitters)
                    {
                        w.Int(emitter.Details.Length);
                        foreach (DoorResponseDetail detail in emitter.Details)
                        {
                            w.Int(detail.Prop);
                            w.Structs<Half>(detail.Colour, counted: false);
                        }
                    }
                }
            }
        }

        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None));
    }

    /// <summary>
    /// A room's detail prop lighting section read and checked against the
    /// room's lighting: the lighting with the detail passes attached, and the
    /// responses per range, socket and emitter (null when the section holds
    /// none); both as given when the section is absent or of a revision this
    /// build does not read, or when the room is unlit.
    /// </summary>
    /// <param name="lighting">The room's lighting from its <c>LITE</c> section, or null.</param>
    /// <param name="section">The section's bytes, or null when the room has none.</param>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compiled BSP, whose detail prop lump the section must fit.</param>
    /// <returns>The lighting, and the responses per range (LDR, HDR), each per socket and emitter.</returns>
    /// <exception cref="LinkException">
    /// A section that does not fit the room: another prop count, another
    /// payload count or ranges than the room's lighting, style counts that
    /// do not add up to the runs, a response naming a prop the room does not
    /// have or an emitter count other than none or sixteen, a codec this
    /// build does not read, bytes after its end.
    /// </exception>
    public static (RoomLighting? Lighting, DetailDoor? Door) Read(
        RoomLighting? lighting, ArraySegment<byte>? section, string room, BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(bsp);
        if (lighting is null || RoomLinkSections.Open(section, room, SectionTag) is not { } r)
        {
            return (lighting, null);
        }

        int props = RoomDetailProps.CountOf(bsp);
        int count = r.Int();
        if (count != props || count == 0)
        {
            throw r.Mismatch($"the lighting of {count} detail props; the room has {props}");
        }

        int turns = r.Int();
        if (turns != lighting.Payloads.Length)
        {
            throw r.Mismatch($"{turns} turns of detail prop lighting; the room's lighting holds {lighting.Payloads.Length}");
        }

        RoomLightingPayload[] payloads = new RoomLightingPayload[turns];
        for (int t = 0; t < turns; t++)
        {
            RoomLightingPayload payload = lighting.Payloads[t];
            int expected = (payload.Ldr is null ? 0 : 1) | (payload.Hdr is null ? 0 : 2);
            int flags = r.Small(3, "a range flag of");
            if (flags != expected)
            {
                throw r.Mismatch($"a range flag of {flags}; the room's lighting lit {expected}");
            }

            payloads[t] = new RoomLightingPayload(ReadRange(r, payload.Ldr, count), ReadRange(r, payload.Hdr, count));
        }

        DetailDoor? door = null;
        if (r.Flag())
        {
            DoorDetailReceivers? receivers = null;
            if (r.Flag())
            {
                Vec3[] centres = r.Structs<Vec3>("detail receiver centres", count, counted: false);
                Vec3[] normals = r.Structs<Vec3>("detail receiver normals", count, counted: false);
                DoorSeen[] seen = new DoorSeen[r.Count("sockets of detail receivers")];
                for (int s = 0; s < seen.Length; s++)
                {
                    seen[s] = RoomDoorLight.ReadSeen(r, count, "detail receivers");
                }

                receivers = new DoorDetailReceivers(centres, normals, seen);
            }

            DoorResponseDetail[][][]?[] responses = new DoorResponseDetail[][][]?[2];
            door = new DetailDoor(receivers, responses);
            for (int range = 0; range < 2; range++)
            {
                if ((range == 0 ? payloads[0].Ldr : payloads[0].Hdr) is null)
                {
                    continue;
                }

                int sockets = r.Count("sockets of detail responses");
                responses[range] = new DoorResponseDetail[sockets][][];
                for (int s = 0; s < sockets; s++)
                {
                    int emitters = r.Int();
                    if (emitters is not (0 or DoorLightMath.EmitterCount))
                    {
                        throw r.Mismatch($"{emitters} detail response emitters; a socket has none or {DoorLightMath.EmitterCount}");
                    }

                    responses[range]![s] = new DoorResponseDetail[emitters][];
                    for (int e = 0; e < emitters; e++)
                    {
                        DoorResponseDetail[] details = new DoorResponseDetail[r.Count("detail responses")];
                        for (int i = 0; i < details.Length; i++)
                        {
                            int prop = r.Int();
                            if ((uint)prop >= (uint)count)
                            {
                                throw r.Mismatch($"a response for detail prop {prop}; the room has {count}");
                            }

                            details[i] = new DoorResponseDetail(prop, r.Structs<Half>("detail response halves", 3, counted: false));
                        }

                        responses[range]![s][e] = details;
                    }
                }
            }
        }

        r.End();
        return (lighting.WithPayloads(payloads), door);
    }

    /// <summary>
    /// A room's door light with its detail props' receivers and responses
    /// attached; the door light as it is when the room has none or the
    /// section held no door part.
    /// </summary>
    /// <param name="door">The room's door light, or null.</param>
    /// <param name="detail">The door part <see cref="Read"/> gave.</param>
    /// <param name="room">The room's name, for messages.</param>
    /// <returns>The door light.</returns>
    /// <exception cref="LinkException">The door part does not fit the door light: other ranges, sockets or emitter counts.</exception>
    public static RoomDoorLight? AttachDoor(RoomDoorLight? door, DetailDoor? detail, string room)
    {
        if (door is null || detail is null)
        {
            return door;
        }

        if (detail.Receivers is { } receivers && receivers.Seen.Length != door.Receivers.Length)
        {
            throw Mismatch($"detail receivers for {receivers.Seen.Length} sockets; the room's door light has {door.Receivers.Length}");
        }

        DoorLightRange? Range(DoorLightRange? range, DoorResponseDetail[][][]? details, string name)
        {
            if ((range is null) != (details is null))
            {
                throw Mismatch($"detail responses for the {name} range, which the room's door light {(range is null ? "does not" : "does")} light otherwise");
            }

            if (range is null)
            {
                return null;
            }

            if (details!.Length != range.Responses.Length)
            {
                throw Mismatch($"detail responses for {details.Length} sockets; the room's door light has {range.Responses.Length}");
            }

            DoorResponseEmitter[][] emitters = new DoorResponseEmitter[range.Responses.Length][];
            for (int s = 0; s < emitters.Length; s++)
            {
                if (details[s].Length != range.Responses[s].Length)
                {
                    throw Mismatch($"{details[s].Length} detail response emitters at socket {s}; the room's door light has {range.Responses[s].Length}");
                }

                emitters[s] = [.. range.Responses[s].Select((e, i) => e with { Details = details[s][i] })];
            }

            return range with { Responses = emitters };
        }

        LinkException Mismatch(string what) => new($"room pack entry \"{room}\": its \"{SectionTag}\" section holds {what}.");

        return door.With(Range(door.Ldr, detail.Responses[0], "LDR"), Range(door.Hdr, detail.Responses[1], "HDR"), detail.Receivers);
    }

    private static RoomLightRange? ReadRange(RoomLinkSections.Reader r, RoomLightRange? range, int count)
    {
        if (range is null)
        {
            return null;
        }

        ColorRgbExp32[] colors = r.Structs<ColorRgbExp32>("detail prop colours", count, counted: false);
        byte[] counts = r.Structs<byte>("detail prop style counts", count, counted: false);
        long runs = 0;
        foreach (byte c in counts)
        {
            runs += c;
        }

        int styleCount = r.Count("detail prop styles");
        if (styleCount != runs)
        {
            throw r.Mismatch($"{styleCount} detail prop styles for runs of {runs}");
        }

        DetailPropLightstylesLump[] styles = MemoryMarshal.Cast<byte, DetailPropLightstylesLump>(
            r.Structs<byte>("detail prop styles", styleCount * System.Runtime.CompilerServices.Unsafe.SizeOf<DetailPropLightstylesLump>(), counted: false)).ToArray();
        return range with { Detail = new RoomDetailLight(colors, counts, styles) };
    }
}

/// <summary>
/// The door part of a room's detail prop lighting section, read ahead of the
/// room's door light it is attached to (<see cref="RoomDetailLighting.AttachDoor"/>).
/// </summary>
/// <param name="Receivers">The detail props as receivers, or null.</param>
/// <param name="Responses">Per range (LDR, HDR), per socket and emitter, the detail props each emitter reached; null for a range the door light does not light.</param>
internal sealed record DetailDoor(DoorDetailReceivers? Receivers, DoorResponseDetail[][][]?[] Responses);
