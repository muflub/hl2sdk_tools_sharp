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
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Props;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// A sky room's direct sun through its sky, the skybox left out, per stored
/// turn (the rooms design, the skybox parallax, D36): what the link scales
/// by the change in the skybox sun map's visibility
/// (<see cref="RoomSunMap"/>) to move the room's sun from its bakes' cell to
/// the cell it is placed at.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is baked.</b> One more vrad run a turn, of the room as its base
/// bake lights it (the same frame and switches) but with the sun alone: every
/// light entity and texture light taken out, the sun's ambient set to zero,
/// no bounce and no skybox. What it gives each face's style-0 luxels (bump
/// pages included) is the sun that reaches them through the room's sky,
/// the room's own shadows in, as if the skybox stopped nothing; and to each
/// lit static prop's vertices the same. Faces and props it leaves black are
/// not stored.
/// </para>
/// <para>
/// <b>At link</b> (<see cref="LevelLinker"/>'s parallax): a placement at
/// its bakes' cell takes its bake as it is; one elsewhere adds, to each
/// stored luxel, its sun here times the map's visibility at the luxel's
/// recast start from its cell less that from its bakes' cell (a prop takes
/// its origin's). The sun layer does not correct the sky ambient's own
/// parallax, nor the bounce of the sun's change: the design's residual.
/// </para>
/// <para>
/// <b>The section</b> (<see cref="SectionTag"/>, <c>SUNL</c>) sits with the
/// room's entry beside its lighting; the link sections' framing,
/// Brotli-coded: revision, the direction towards the sun its bakes were lit
/// by (the link applies a layer only under a map of the same sun, bit for
/// bit), the turn count and per turn a range flag (1 LDR, 2 HDR) and per
/// range the faces (face, first luxel in the base range's lump, luxel count,
/// three halves a luxel) and the props (prop, its origin room-local, vertex
/// count, three halves a vertex). An older build skips the tag.
/// </para>
/// </remarks>
internal sealed class RoomSunLayer
{
    /// <summary>The tag of a sky room's sun layer section.</summary>
    public const string SectionTag = "SUNL";

    /// <summary>The revision of the parallax the rooms' lighting description names (<see cref="RoomLightingSettings.Describe"/>).</summary>
    public const int Revision = 1;

    /// <summary>Makes a layer.</summary>
    internal RoomSunLayer(Vec3 toward, RoomSunTurn[] turns)
    {
        Toward = toward;
        Turns = turns;
    }

    /// <summary>The unit direction towards the sun the room's bakes were lit by, in the world's frame.</summary>
    public Vec3 Toward { get; }

    /// <summary>Per stored turn, its ranges.</summary>
    public RoomSunTurn[] Turns { get; }

    /// <summary>The turn a placement at <paramref name="rotation"/> takes, as its lighting's payload.</summary>
    public RoomSunTurn For(int rotation) => Turns[rotation % Turns.Length];

    /// <summary>
    /// The layer of a sky room whose base bake is <paramref name="payloads"/>:
    /// one sun-only vrad run a stored turn; null when its bake has no sun.
    /// </summary>
    public static async Task<RoomSunLayer?> BakeAsync(
        RoomObject room,
        RoomLightingPayload[] payloads,
        DWorldLight[]? sky,
        RoomLightingSettings settings,
        IContentFileSystem content,
        CompileParallelism parallelism,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Sun is not { } sun || RoomSunMap.TowardSun(sky) is not { } toward)
        {
            return null;
        }

        List<BspEntity> entities = RoomDoorLight.Unlit(room.Bsp);
        entities.Insert(Math.Min(1, entities.Count), RoomLibraryEntities.ToLinked(Darkened(sun)));
        StaticPropLump? propLump = RoomStaticProps.ReadLump(room.Bsp);
        RoomSunTurn[] turns = new RoomSunTurn[payloads.Length];
        for (int turn = 0; turn < payloads.Length; turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BspData lit = RoomLighting.Copy(room.Bsp);
            lit[BspLump.Entities] = EntityLump.Write(entities);
            List<(bool Hdr, StaticPropLightingResult Result)> props = [];
            VradContext context = new()
            {
                Options = settings.Options with { Bounces = 0 },
#pragma warning disable CA1308 // the room compile names a room's files in lower case
                MapName = room.Definition.Name.ToLowerInvariant(),
#pragma warning restore CA1308
                Content = content,
                Parallelism = parallelism,
                FrameTurns = turn,
                NoTextureLights = true,
                StaticPropLightingObserver = (hdr, result) =>
                {
                    lock (props)
                    {
                        props.Add((hdr, result));
                    }
                },
            };

            _ = await Vrad.LightAsync(lit, context, cancellationToken).ConfigureAwait(false);
            turns[turn] = new RoomSunTurn(
                payloads[turn].Ldr is { } ldr ? Extract(room.Bsp, lit, hdr: false, ldr, props, propLump) : null,
                payloads[turn].Hdr is { } hdr ? Extract(room.Bsp, lit, hdr: true, hdr, props, propLump) : null);
        }

        return new RoomSunLayer(toward, turns);
    }

    /// <summary>The sun with its ambient taken out: only its direct light is baked here.</summary>
    internal static VmfChunk Darkened(VmfChunk sun)
    {
        VmfChunk copy = new(sun.Name);
        foreach (VmfKey key in sun.Keys)
        {
            bool ambient = key.Name.Equals("_ambient", StringComparison.OrdinalIgnoreCase) || key.Name.Equals("_ambientHDR", StringComparison.OrdinalIgnoreCase);
            copy.AddKey(key.Name, ambient ? "0 0 0 0" : key.Value);
        }

        if (!sun.Keys.Any(k => k.Name.Equals("_ambient", StringComparison.OrdinalIgnoreCase)))
        {
            copy.AddKey("_ambient", "0 0 0 0");
        }

        return copy;
    }

    /// <summary>One range's sun faces and props from the sun-only run, placed against the base bake's range.</summary>
    private static RoomSunRange Extract(
        BspData compiled, BspData lit, bool hdr, RoomLightRange baked, List<(bool Hdr, StaticPropLightingResult Result)> props, StaticPropLump? propLump)
    {
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(lit[hdr ? BspLump.FacesHdr : BspLump.Faces]);
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(compiled[BspLump.TexInfo]);
        ReadOnlySpan<ColorRgbExp32> light = MemoryMarshal.Cast<byte, ColorRgbExp32>(lit[hdr ? BspLump.LightingHdr : BspLump.Lighting].Data.Span);
        List<RoomSunFace> sunFaces = [];
        for (int f = 0; f < faces.Length && f < baked.LightOffsets.Length; f++)
        {
            int own = StyleSlot(faces[f].Styles[0], faces[f].Styles[1], faces[f].Styles[2], faces[f].Styles[3]);
            int base0 = StyleSlot(baked.Styles[f * 4], baked.Styles[(f * 4) + 1], baked.Styles[(f * 4) + 2], baked.Styles[(f * 4) + 3]);
            if (own < 0 || base0 < 0 || faces[f].LightOfs < 0 || baked.LightOffsets[f] < 0)
            {
                continue;
            }

            int count = RoomDoorLight.Pages(texInfos, faces[f]) * RoomDoorLight.PageLuxels(faces[f]);
            int from = (faces[f].LightOfs / RoomLighting.LuxelBytes) + (own * count);
            int start = (baked.LightOffsets[f] / RoomLighting.LuxelBytes) + (base0 * count);
            if (from + count > light.Length || (start + count) * 3 > baked.Luxels.Length)
            {
                continue;
            }

            Half[] halves = RoomLighting.DecodeColors(light.Slice(from, count));
            if (halves.All(h => h == Half.Zero))
            {
                continue;
            }

            sunFaces.Add(new RoomSunFace(f, start, halves));
        }

        List<RoomSunProp> sunProps = [];
        foreach ((bool passHdr, StaticPropLightingResult result) in props)
        {
            if (passHdr != hdr)
            {
                continue;
            }

            foreach (StaticPropVhvFile file in result.Files)
            {
                if (file.Colors is not { } colors || propLump is null || file.PropIndex >= propLump.Props.Count)
                {
                    continue;
                }

                List<Half> halves = [];
                foreach ((_, Vec3[] c) in colors.Meshes)
                {
                    foreach (Vec3 v in c)
                    {
                        Vec3 e = StockLightColor.Encode(v).ToLinear();
                        halves.Add((Half)e.X);
                        halves.Add((Half)e.Y);
                        halves.Add((Half)e.Z);
                    }
                }

                if (halves.All(h => h == Half.Zero))
                {
                    continue;
                }

                sunProps.Add(new RoomSunProp(file.PropIndex, propLump.Props[file.PropIndex].Origin, [.. halves]));
            }
        }

        sunProps.Sort((a, b) => a.Prop.CompareTo(b.Prop));
        return new RoomSunRange([.. sunFaces], [.. sunProps]);
    }

    /// <summary>Which of a face's four style slots holds style 0, or -1.</summary>
    internal static int StyleSlot(byte a, byte b, byte c, byte d) => a == 0 ? 0 : b == 0 ? 1 : c == 0 ? 2 : d == 0 ? 3 : -1;

    /// <summary>The layer as its pack section.</summary>
    public RoomPackSectionData ToSection()
    {
        RoomLinkSections.Writer w = new();
        w.Int(RoomLinkSections.Revision);
        w.Structs<Vec3>([Toward], counted: false);
        w.Int(Turns.Length);
        foreach (RoomSunTurn turn in Turns)
        {
            w.Byte((byte)((turn.Ldr is null ? 0 : 1) | (turn.Hdr is null ? 0 : 2)));
            foreach (RoomSunRange? range in (ReadOnlySpan<RoomSunRange?>)[turn.Ldr, turn.Hdr])
            {
                if (range is null)
                {
                    continue;
                }

                w.Int(range.Faces.Length);
                foreach (RoomSunFace face in range.Faces)
                {
                    w.Int(face.Face);
                    w.Int(face.Start);
                    w.Int(face.Luxels.Length / 3);
                    w.Structs<Half>(face.Luxels, counted: false);
                }

                w.Int(range.Props.Length);
                foreach (RoomSunProp prop in range.Props)
                {
                    w.Int(prop.Prop);
                    w.Structs<Vec3>([prop.Origin], counted: false);
                    w.Int(prop.Colors.Length / 3);
                    w.Structs<Half>(prop.Colors, counted: false);
                }
            }
        }

        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.Brotli));
    }

    /// <summary>
    /// A layer read back from its section, held to the room's lighting, or
    /// null for none (absent, a revision this build does not read, or a room
    /// without lighting).
    /// </summary>
    /// <exception cref="LinkException">The section is damaged or does not fit the room's lighting.</exception>
    public static RoomSunLayer? Read(ArraySegment<byte>? section, string room, RoomLighting? lighting)
    {
        if (RoomLinkSections.Open(section, room, SectionTag) is not { } r)
        {
            return null;
        }

        if (lighting is null)
        {
            throw r.Mismatch("a sun layer for a room without lighting");
        }

        Vec3 toward = r.Structs<Vec3>("direction", 1, counted: false)[0];
        int count = r.Int();
        if (count != lighting.RotationCount)
        {
            throw r.Mismatch($"{count} turns; the room's lighting stores {lighting.RotationCount}");
        }

        RoomSunTurn[] turns = new RoomSunTurn[count];
        for (int t = 0; t < count; t++)
        {
            int flags = r.Small(3, "a range flag of");
            RoomLightingPayload payload = lighting.Payloads[t];
            RoomSunRange? ldr = (flags & 1) != 0 ? ReadRange(r, payload.Ldr) : null;
            RoomSunRange? hdr = (flags & 2) != 0 ? ReadRange(r, payload.Hdr) : null;
            turns[t] = new RoomSunTurn(ldr, hdr);
        }

        r.End();
        return new RoomSunLayer(toward, turns);
    }

    private static RoomSunRange ReadRange(RoomLinkSections.Reader r, RoomLightRange? baked)
    {
        if (baked is null)
        {
            throw r.Mismatch("a sun range the room's lighting does not have");
        }

        RoomSunFace[] faces = new RoomSunFace[r.Count("sun faces")];
        for (int i = 0; i < faces.Length; i++)
        {
            int face = r.Int(), start = r.Int(), luxels = r.Count("sun luxels");
            if ((uint)face >= (uint)baked.LightOffsets.Length || start < 0 || ((long)start + luxels) * 3 > baked.Luxels.Length)
            {
                throw r.Mismatch($"sun face {face} at luxel {start} of {luxels}, outside the room's lighting");
            }

            faces[i] = new RoomSunFace(face, start, r.Structs<Half>("sun luxels", luxels * 3, counted: false));
        }

        RoomSunProp[] props = new RoomSunProp[r.Count("sun props")];
        for (int i = 0; i < props.Length; i++)
        {
            int prop = r.Int();
            Vec3 origin = r.Structs<Vec3>("prop origin", 1, counted: false)[0];
            int vertices = r.Count("sun prop vertices");
            props[i] = new RoomSunProp(prop, origin, r.Structs<Half>("sun prop colours", vertices * 3, counted: false));
        }

        return new RoomSunRange(faces, props);
    }
}

/// <summary>One stored turn of a sun layer: its LDR and HDR ranges, each null when the bake did not light it.</summary>
/// <param name="Ldr">The LDR range, or null.</param>
/// <param name="Hdr">The HDR range, or null.</param>
internal sealed record RoomSunTurn(RoomSunRange? Ldr, RoomSunRange? Hdr)
{
    /// <summary>The range of one pass.</summary>
    public RoomSunRange? Range(bool hdr) => hdr ? Hdr : Ldr;
}

/// <summary>One range of a sun layer: the faces and props the sun reaches through the room's sky.</summary>
/// <param name="Faces">The faces, in face order.</param>
/// <param name="Props">The props, in prop order.</param>
internal sealed record RoomSunRange(RoomSunFace[] Faces, RoomSunProp[] Props);

/// <summary>One face's style-0 sun luxels.</summary>
/// <param name="Face">The room's face.</param>
/// <param name="Start">Its style-0 block's first luxel in the base range's luxels.</param>
/// <param name="Luxels">Three halves a luxel, pages after one another as the lump lays them.</param>
internal sealed record RoomSunFace(int Face, int Start, Half[] Luxels);

/// <summary>One static prop's sun per vertex.</summary>
/// <param name="Prop">The prop's index in the room's lump.</param>
/// <param name="Origin">Its origin, room-local: where its whole sun is taken to be recast from.</param>
/// <param name="Colors">Three halves a vertex, in the base range's vertex order.</param>
internal sealed record RoomSunProp(int Prop, Vec3 Origin, Half[] Colors);
