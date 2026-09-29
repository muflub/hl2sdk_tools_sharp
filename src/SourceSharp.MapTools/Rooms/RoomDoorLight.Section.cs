//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rooms;

internal sealed partial class RoomDoorLight
{
    /// <summary>Bytes of one source record: type, origin, normal, intensity, style, six falloff terms, the cell mask, the stand-in flag.</summary>
    private const int SourceBytes = 4 + (3 * 12) + 4 + (6 * 4) + 16 + 1;

    /// <summary>
    /// The room's door light as its pack section: the receivers of each
    /// socket (per face: the face, its cell count, a code a cell and the
    /// partial masks), a range flag byte (1 LDR, 2 HDR), and per range the
    /// stored turn count, per turn and socket the sources and the ambient
    /// receivers, and per socket the response emitters (per emitter its
    /// faces' half-resolution values, its ambient samples and its props).
    /// Scalars big-endian, halves and floats little-endian, as every link
    /// section writes them.
    /// </summary>
    public RoomPackSectionData ToSection()
    {
        RoomLinkSections.Writer w = new();
        w.Int(RoomLinkSections.RevisionFor(SectionTag));
        w.Int(Receivers.Length);
        foreach (DoorReceiverFace[] socket in Receivers)
        {
            w.Int(socket.Length);
            foreach (DoorReceiverFace face in socket)
            {
                w.Int(face.Face);
                WriteSeen(w, face.Seen);
            }
        }

        w.Byte((byte)((Ldr is null ? 0 : 1) | (Hdr is null ? 0 : 2)));
        foreach (DoorLightRange? range in (ReadOnlySpan<DoorLightRange?>)[Ldr, Hdr])
        {
            if (range is null)
            {
                continue;
            }

            w.Int(range.Captures.Length);
            for (int turn = 0; turn < range.Captures.Length; turn++)
            {
                for (int s = 0; s < Receivers.Length; s++)
                {
                    DoorSource[] sources = range.Captures[turn][s];
                    w.Int(sources.Length);
                    foreach (DoorSource source in sources)
                    {
                        WriteSource(w, source);
                    }

                    WriteSeen(w, range.Ambient[turn][s]);
                }
            }

            foreach (DoorResponseEmitter[] emitters in range.Responses)
            {
                w.Int(emitters.Length);
                foreach (DoorResponseEmitter emitter in emitters)
                {
                    w.Int(emitter.Faces.Length);
                    foreach (DoorResponseFace face in emitter.Faces)
                    {
                        w.Int(face.Face);
                        w.Structs<Half>(face.Values);
                    }

                    w.Int(emitter.Ambient.Length);
                    foreach (DoorResponseSample sample in emitter.Ambient)
                    {
                        w.Int(sample.Leaf);
                        w.Structs<Vec3>([sample.Position], counted: false);
                        w.Structs<Half>(sample.Cube, counted: false);
                    }

                    w.Int(emitter.Props.Length);
                    foreach (DoorResponseProp prop in emitter.Props)
                    {
                        w.Int(prop.Prop);
                        w.Structs<Half>(prop.Colours);
                    }
                }
            }
        }

        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None));
    }

    private static void WriteSeen(RoomLinkSections.Writer w, DoorSeen seen)
    {
        w.Int(seen.Codes.Length);
        w.Raw(seen.Codes);
        w.Structs<UInt128>(seen.Partial);
    }

    private static void WriteSource(RoomLinkSections.Writer w, in DoorSource source)
    {
        w.Int((int)source.Type);
        w.Structs<Vec3>([source.Origin, source.Normal, source.Intensity], counted: false);
        w.Int(source.Style);
        w.Structs<float>([source.ConstantAttn, source.LinearAttn, source.QuadraticAttn, source.StopDot, source.StopDot2, source.Exponent], counted: false);
        w.Structs<UInt128>([source.Cells], counted: false);
        w.Byte(source.StandIn ? (byte)1 : (byte)0);
    }

    /// <summary>
    /// A room's door light read back from its pack section and bound to the
    /// room's compile, or null when the room has none (a pack built without
    /// door light, or a section of a revision this build does not read).
    /// </summary>
    /// <param name="section">The section, or null.</param>
    /// <param name="definition">The room.</param>
    /// <param name="bsp">Its compile.</param>
    /// <param name="lighting">Its base lighting, which the section's turns, samples and props must match; a section without it is refused.</param>
    /// <exception cref="LinkException">The section is damaged or does not fit the room's compile and lighting.</exception>
    public static RoomDoorLight? Read(ArraySegment<byte>? section, RoomDefinition definition, BspData bsp, RoomLighting? lighting)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(bsp);
        string room = definition.Name;
        if (RoomLinkSections.Open(section, room, SectionTag) is not { } r)
        {
            return null;
        }

        if (lighting is null)
        {
            throw r.Mismatch("door light for a room with no base lighting");
        }

        DoorFaceCells?[] cells = FaceCellsOf(bsp);
        int sockets = r.Int();
        if (sockets != definition.Sockets.Count)
        {
            throw r.Mismatch($"{sockets} sockets; the room has {definition.Sockets.Count}");
        }

        DoorReceiverFace[][] receivers = new DoorReceiverFace[sockets][];
        for (int s = 0; s < sockets; s++)
        {
            receivers[s] = new DoorReceiverFace[r.Count("receiving faces")];
            for (int i = 0; i < receivers[s].Length; i++)
            {
                int face = r.Int();
                if ((uint)face >= (uint)cells.Length || cells[face] is not { } faceCells)
                {
                    throw r.Mismatch($"receiving face {face}, which the room does not light");
                }

                receivers[s][i] = new DoorReceiverFace(face, ReadSeen(r, faceCells.Count, "cells of a face"));
            }
        }

        int flags = r.Small(3, "a range flag of");
        if ((flags & 1) != 0 != (lighting.Payloads[0].Ldr is not null) || (flags & 2) != 0 != (lighting.Payloads[0].Hdr is not null))
        {
            throw r.Mismatch($"ranges {flags}; the room's lighting has others");
        }

        DoorLightRange? ldr = (flags & 1) != 0 ? ReadRange(r, false, sockets, lighting, bsp) : null;
        DoorLightRange? hdr = (flags & 2) != 0 ? ReadRange(r, true, sockets, lighting, bsp) : null;
        r.End();
        return new RoomDoorLight(receivers, ldr, hdr, bsp);
    }

    private static DoorLightRange ReadRange(RoomLinkSections.Reader r, bool hdr, int sockets, RoomLighting lighting, BspData bsp)
    {
        int turns = r.Int();
        if (turns != lighting.RotationCount)
        {
            throw r.Mismatch($"{turns} stored turns; the room's lighting stores {lighting.RotationCount}");
        }

        DoorSource[][][] captures = new DoorSource[turns][][];
        DoorSeen[][] ambient = new DoorSeen[turns][];
        for (int turn = 0; turn < turns; turn++)
        {
            RoomLightRange range = lighting.Payloads[turn].Range(hdr)!;
            int samples = range.AmbientPositions.Length / 4;
            captures[turn] = new DoorSource[sockets][];
            ambient[turn] = new DoorSeen[sockets];
            for (int s = 0; s < sockets; s++)
            {
                int count = r.Count("sources");
                if ((long)count * SourceBytes > int.MaxValue)
                {
                    throw r.Mismatch($"{count} sources");
                }

                captures[turn][s] = new DoorSource[count];
                for (int i = 0; i < count; i++)
                {
                    captures[turn][s][i] = ReadSource(r);
                }

                ambient[turn][s] = ReadSeen(r, samples, "ambient samples");
            }
        }

        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);
        int leafCount = BspStructView.Count<DLeaf>(bsp[BspLump.Leafs]);
        RoomPropColors[] props = lighting.Payloads[0].Range(hdr)!.Props;
        DoorResponseEmitter[][] responses = new DoorResponseEmitter[sockets][];
        for (int s = 0; s < sockets; s++)
        {
            int emitters = r.Int();
            if (emitters is not (0 or DoorLightMath.EmitterCount))
            {
                throw r.Mismatch($"{emitters} response emitters; a socket has none or {DoorLightMath.EmitterCount}");
            }

            responses[s] = new DoorResponseEmitter[emitters];
            for (int e = 0; e < emitters; e++)
            {
                DoorResponseFace[] responseFaces = new DoorResponseFace[r.Count("response faces")];
                for (int i = 0; i < responseFaces.Length; i++)
                {
                    int face = r.Int();
                    if ((uint)face >= (uint)faces.Length)
                    {
                        throw r.Mismatch($"response face {face}; the room has {faces.Length} faces");
                    }

                    (int hw, int hh) = Half(faces[face].LightmapTextureSizeInLuxels[0] + 1, faces[face].LightmapTextureSizeInLuxels[1] + 1);
                    responseFaces[i] = new DoorResponseFace(face, r.Structs<Half>("response luxel halves", Pages(texInfos, faces[face]) * hw * hh * 3));
                }

                DoorResponseSample[] samples = new DoorResponseSample[r.Count("response samples")];
                for (int i = 0; i < samples.Length; i++)
                {
                    int leaf = r.Int();
                    if ((uint)leaf >= (uint)leafCount)
                    {
                        throw r.Mismatch($"a response sample in leaf {leaf}; the room has {leafCount} leaves");
                    }

                    Vec3 position = r.Structs<Vec3>("sample position", 1, counted: false)[0];
                    samples[i] = new DoorResponseSample(leaf, position, r.Structs<Half>("cube halves", RoomLighting.AmbientHalves, counted: false));
                }

                DoorResponseProp[] responseProps = new DoorResponseProp[r.Count("response props")];
                for (int i = 0; i < responseProps.Length; i++)
                {
                    int prop = r.Int();
                    if (props.FirstOrDefault(p => p.Prop == prop) is not { } lit)
                    {
                        throw r.Mismatch($"a response for prop {prop}, which the room's lighting did not light");
                    }

                    responseProps[i] = new DoorResponseProp(prop, r.Structs<Half>("prop colour halves", lit.Colors.Length));
                }

                responses[s][e] = new DoorResponseEmitter(responseFaces, samples, responseProps);
            }
        }

        return new DoorLightRange(captures, ambient, responses);
    }

    private static DoorSeen ReadSeen(RoomLinkSections.Reader r, int expected, string what)
    {
        byte[] codes = r.Structs<byte>(what, expected);
        int partial = 0;
        foreach (byte code in codes)
        {
            if (code > 2)
            {
                throw r.Mismatch($"a receiver code of {code}");
            }

            if (code == 2)
            {
                partial++;
            }
        }

        UInt128[] masks = r.Structs<UInt128>("partial masks", partial);
        foreach (UInt128 mask in masks)
        {
            if (mask == UInt128.Zero || (mask & ~DoorLightMath.AllCells) != UInt128.Zero)
            {
                throw r.Mismatch("a partial mask naming no cell or a cell the opening does not have");
            }
        }

        return new DoorSeen(codes, masks);
    }

    private static DoorSource ReadSource(RoomLinkSections.Reader r)
    {
        int type = r.Int();
        if (type is not ((int)EmitType.Point or (int)EmitType.Spotlight or (int)EmitType.Surface or (int)EmitType.SkyLight))
        {
            throw r.Mismatch($"a source of type {type}");
        }

        Vec3[] vectors = r.Structs<Vec3>("source vectors", 3, counted: false);
        int style = r.Int();
        float[] terms = r.Structs<float>("source falloff", 6, counted: false);
        UInt128 cells = r.Structs<UInt128>("source cells", 1, counted: false)[0];
        if ((cells & ~DoorLightMath.AllCells) != UInt128.Zero)
        {
            throw r.Mismatch("a source reaching a cell the opening does not have");
        }

        bool standIn = r.Flag();
        return new DoorSource((EmitType)type, vectors[0], vectors[1], vectors[2], style, terms[0], terms[1], terms[2], terms[3], terms[4], terms[5], cells)
        {
            StandIn = standIn,
        };
    }
}
