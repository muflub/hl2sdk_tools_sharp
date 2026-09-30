//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;

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
/// turn's passes (<see cref="RoomDetailLight"/>), beside the room's
/// <c>LITE</c> section, whose bytes it leaves as they were.
/// </summary>
/// <remarks>
/// <para>
/// <b>Once or four times.</b> The detail lighting follows the room's base
/// bake (the rooms design, 1.1 and 9.4): one payload for a room no sun or
/// sky reaches, four for one it reaches, one per stored turn of the
/// <c>LITE</c> section, which it must match. A detail prop's lighting is a
/// colour and a style run with no direction in it, so a turn changes none
/// of its bytes; the four payloads of a sunlit room differ because the sun
/// is fixed in the world.
/// </para>
/// <para>
/// <b>The section</b> (<see cref="SectionTag"/>, <c>DPLT</c>) has the link
/// sections' framing (<see cref="RoomLinkSections"/>: codec byte, decoded
/// length, revision; codec none). After the revision, big-endian
/// <c>int32</c>s: the prop count (the room's lump's) and the payload count
/// (the <c>LITE</c> section's); then per payload a flag byte (1 LDR, 2 HDR,
/// the ranges the <c>LITE</c> payload lit) and per range every prop's
/// colour (four bytes), every prop's style count (a byte), and the runs
/// (their count, then five bytes each: the colour and the style). A room
/// whose bake lit no detail prop has no section; a section that does not
/// fit the room is refused as damaged. The tag is one an older build skips,
/// and such a build refuses a room with detail props by its lump, so the
/// pack's format version is unchanged.
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
    /// <returns>The section, tagged <see cref="SectionTag"/>, or null.</returns>
    /// <exception cref="ArgumentException">A payload lit its detail props in one range and not in another.</exception>
    public static RoomPackSectionData? ToSection(RoomLighting lighting)
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

        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None));
    }

    /// <summary>
    /// A room's lighting with its detail prop lighting attached from its
    /// section; the lighting as it is when the section is absent or of a
    /// revision this build does not read, or when the room is unlit.
    /// </summary>
    /// <param name="lighting">The room's lighting from its <c>LITE</c> section, or null.</param>
    /// <param name="section">The section's bytes, or null when the room has none.</param>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compiled BSP, whose detail prop lump the section must fit.</param>
    /// <returns>The lighting.</returns>
    /// <exception cref="LinkException">
    /// A section that does not fit the room: another prop count, another
    /// payload count or ranges than the room's lighting, style counts that
    /// do not add up to the runs, a codec this build does not read, bytes
    /// after its end.
    /// </exception>
    public static RoomLighting? Attach(RoomLighting? lighting, ArraySegment<byte>? section, string room, BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(bsp);
        if (lighting is null || RoomLinkSections.Open(section, room, SectionTag) is not { } r)
        {
            return lighting;
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

        r.End();
        return lighting.WithPayloads(payloads);
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
