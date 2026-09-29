//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Vis;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// What one room's own visibility says about its doorways: which of its
/// clusters see each doorway, which doorways see each other through the
/// room, and where each cluster is. The room compile works it out once, and
/// the link composes the level's visibility from it
/// (<see cref="LevelDoorVisibility"/>) without flooding or running vvis.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these three.</b> A sight line from a cluster of one room to a
/// cluster of another leaves the first room through a doorway, crosses
/// every room between through two of their doorways, and enters the last
/// one through a doorway: rooms are sealed but for their sockets, so there
/// is no other way out. Each step is a sight line inside one room, and each
/// is what this records, from the room's own vvis:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="Sees"/>: per socket, the clusters with a sight line to the
/// doorway. A line from a cluster to the doorway ends in an open leaf that
/// touches the plug box (the socket's facing clusters, the plug census's
/// <see cref="SocketCensus.Facing"/>), so the cluster sees one of them, and
/// vvis, which never drops a sight line, has the bit. The relation is read
/// both ways (a bit in either row), so it holds even where vvis's rows are
/// not symmetric.
/// </description></item>
/// <item><description>
/// <see cref="Through"/>: per pair of sockets, whether a sight line can
/// cross the room from one doorway to the other: some cluster facing the
/// first sees some cluster facing the second. Two plugs whose boxes touch
/// are always through, since a line can then pass from one doorway to the
/// other without entering a leaf of the room.
/// </description></item>
/// <item><description>
/// <see cref="ClusterBoxes"/>: per cluster, the bounds of its open leaves,
/// room-local. The link turns and moves them with the room and tests them
/// against the cone of sight lines a chain of doorways lets through.
/// </description></item>
/// </list>
/// <para>
/// Each relation is a superset of what a sight line needs, so the level
/// composed from them never loses a cluster the flattened level's vvis
/// would find (the linked facts compare against that vvis). What the room
/// alone cannot say is where the lines through a chain of doorways go; the
/// link works that out from the doorways' rectangles, which is geometry of
/// the level, not of the room.
/// </para>
/// <para>
/// <b>Rotation.</b> Nothing here depends on the turn: the relations name
/// the room's own sockets and clusters, which turn with it, and the boxes
/// are turned exactly at link (a quarter turn permutes and negates, which
/// rounds nothing). The pack section stores it once (<see cref="SectionTag"/>).
/// </para>
/// </remarks>
internal sealed class RoomDoorVisibility
{
    /// <summary>The tag of the pack section that holds it.</summary>
    public const string SectionTag = "DVIS";

    /// <summary>The revision of the section's payload this build writes and reads.</summary>
    public const int Revision = 1;

    /// <summary>Creates the door visibility of a room.</summary>
    /// <param name="clusterCount">The room's clusters.</param>
    /// <param name="sees">Per socket, a bit per cluster (<see cref="Words"/> words each).</param>
    /// <param name="through">Per socket pair <c>s * sockets + t</c>, whether a sight line crosses the room between them.</param>
    /// <param name="clusterBoxes">Per cluster, its open leaves' bounds, room-local; empty for a cluster no open leaf has.</param>
    /// <param name="hasBox">Per cluster, whether it has a box.</param>
    /// <param name="turnedBoxes">
    /// Per quarter turn, the cluster boxes already turned, when a section
    /// stored them that way (a rotation count of 4); null to turn
    /// <paramref name="clusterBoxes"/> at link.
    /// </param>
    public RoomDoorVisibility(int clusterCount, ulong[][] sees, bool[] through, Box[] clusterBoxes, bool[] hasBox, Box[][]? turnedBoxes = null)
    {
        ClusterCount = clusterCount;
        Sees = sees;
        Through = through;
        ClusterBoxes = clusterBoxes;
        HasBox = hasBox;
        TurnedBoxes = turnedBoxes;
    }

    /// <summary>The room's clusters.</summary>
    public int ClusterCount { get; }

    /// <summary>The room's sockets.</summary>
    public int SocketCount => Sees.Length;

    /// <summary>The 64-bit words of one cluster bit set.</summary>
    public int Words => (ClusterCount + 63) >> 6;

    /// <summary>Per socket, the clusters that see its doorway, a bit per cluster.</summary>
    public ulong[][] Sees { get; }

    /// <summary>Per socket pair <c>s * <see cref="SocketCount"/> + t</c>, whether a sight line crosses the room from one doorway to the other.</summary>
    public bool[] Through { get; }

    /// <summary>Per cluster, the bounds of its open leaves, room-local.</summary>
    public Box[] ClusterBoxes { get; }

    /// <summary>Per cluster, whether any open leaf has it (a cluster number vvis skipped has none).</summary>
    public bool[] HasBox { get; }

    /// <summary>Per quarter turn, the cluster boxes turned, when the section stored them so; else null.</summary>
    public Box[][]? TurnedBoxes { get; }

    /// <summary>
    /// A cluster's box turned by <paramref name="rotation"/> quarter turns and
    /// not yet moved: the stored turned box when there is one, else the
    /// room-local box turned now. The two are the same bytes, since a
    /// quarter turn only permutes and negates (<see cref="LevelLinker.RotateBox"/>).
    /// </summary>
    public Box TurnedBox(int cluster, int rotation)
    {
        if (TurnedBoxes is { } turned)
        {
            return turned[rotation][cluster];
        }

        Box local = ClusterBoxes[cluster];
        return LevelLinker.RotateBox(local.Mins, local.Maxs, rotation);
    }

    /// <summary>Whether a sight line can cross the room from socket <paramref name="from"/>'s doorway to socket <paramref name="to"/>'s.</summary>
    public bool IsThrough(int from, int to) => Through[(from * SocketCount) + to];

    /// <summary>Whether cluster <paramref name="cluster"/> sees socket <paramref name="socket"/>'s doorway.</summary>
    public bool SeesDoor(int socket, int cluster) => (Sees[socket][cluster >> 6] & (1UL << (cluster & 63))) != 0;

    /// <summary>Works out a room's door visibility from its own vvis and plug census.</summary>
    /// <param name="room">The compiled room.</param>
    /// <param name="shared">Its plug census (<see cref="RoomLinkShared"/>), which names each socket's facing clusters.</param>
    /// <returns>The door visibility, a pure function of the room.</returns>
    public static RoomDoorVisibility Compute(RoomObject room, RoomLinkShared shared)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(shared);
        VisResult vis = room.Vis;
        RoomDefinition definition = room.Definition;
        int clusters = vis.ClusterCount;
        int sockets = definition.Sockets.Count;
        int words = (clusters + 63) >> 6;

        ulong[][] sees = new ulong[sockets][];
        for (int s = 0; s < sockets; s++)
        {
            ulong[] row = new ulong[words];
            foreach (int f in shared.Sockets[s].Facing)
            {
                // The facing cluster itself, what it sees, and what sees it.
                row[f >> 6] |= 1UL << (f & 63);
                ReadOnlySpan<byte> own = vis.Pvs(f);
                for (int z = 0; z < clusters; z++)
                {
                    if ((own[z >> 3] & (1 << (z & 7))) != 0 || vis.CanSee(z, f))
                    {
                        row[z >> 6] |= 1UL << (z & 63);
                    }
                }
            }

            sees[s] = row;
        }

        Box[] plugs = new Box[sockets];
        for (int s = 0; s < sockets; s++)
        {
            plugs[s] = RoomLinter.SealBox(definition, definition.Sockets[s], definition.CellSize);
        }

        bool[] through = new bool[sockets * sockets];
        for (int s = 0; s < sockets; s++)
        {
            for (int t = 0; t < sockets; t++)
            {
                if (s == t)
                {
                    continue;
                }

                bool seen = plugs[s].Overlaps(plugs[t], LevelLinker.DoorOverlapEpsilon);
                foreach (int f in shared.Sockets[s].Facing)
                {
                    seen |= (sees[t][f >> 6] & (1UL << (f & 63))) != 0;
                }

                through[(s * sockets) + t] = seen;
            }
        }

        Box[] boxes = new Box[clusters];
        bool[] hasBox = new bool[clusters];
        foreach (DLeaf leaf in BspStructView.As<DLeaf>(room.Bsp[BspLump.Leafs]))
        {
            if ((leaf.Contents & (int)BrushContents.Solid) != 0 || leaf.Cluster < 0 || leaf.Cluster >= clusters)
            {
                continue;
            }

            Box box = new(
                new Vec3(leaf.Mins[0], leaf.Mins[1], leaf.Mins[2]),
                new Vec3(leaf.Maxs[0], leaf.Maxs[1], leaf.Maxs[2]));
            boxes[leaf.Cluster] = hasBox[leaf.Cluster] ? Union(boxes[leaf.Cluster], box) : box;
            hasBox[leaf.Cluster] = true;
        }

        return new RoomDoorVisibility(clusters, sees, through, boxes, hasBox);
    }

    /// <summary>The smallest box holding both.</summary>
    internal static Box Union(Box a, Box b) => new(
        new Vec3(Math.Min(a.Mins.X, b.Mins.X), Math.Min(a.Mins.Y, b.Mins.Y), Math.Min(a.Mins.Z, b.Mins.Z)),
        new Vec3(Math.Max(a.Maxs.X, b.Maxs.X), Math.Max(a.Maxs.Y, b.Maxs.Y), Math.Max(a.Maxs.Z, b.Maxs.Z)));

    /// <summary>Whether two door visibilities hold the same relations and, at every turn, the same boxes.</summary>
    internal bool SameAs(RoomDoorVisibility other) =>
        ClusterCount == other.ClusterCount
        && SocketCount == other.SocketCount
        && Sees.Zip(other.Sees).All(p => p.First.AsSpan().SequenceEqual(p.Second))
        && Through.AsSpan().SequenceEqual(other.Through)
        && HasBox.AsSpan().SequenceEqual(other.HasBox)
        && Enumerable.Range(0, 4).All(turn => Enumerable.Range(0, ClusterCount)
            .All(c => !HasBox[c] || TurnedBox(c, turn) == other.TurnedBox(c, turn)));

    /// <summary>The pack section holding it.</summary>
    /// <param name="codec">How to store the payload: none by default (<see cref="RoomLinkSections"/> says why).</param>
    /// <returns>The section's bytes, codec byte first.</returns>
    /// <remarks>
    /// <para>
    /// The payload, after the revision: the rotation count (1: nothing here
    /// changes with a turn, see the type's remarks), then per rotation the
    /// cluster count, the socket count, per socket the <see cref="Sees"/>
    /// words (big-endian <c>int32</c> halves, high first), the
    /// <see cref="Through"/> flags as bytes, and per cluster a flag byte and,
    /// when it is 1, its box (six little-endian floats, as a BSP lump holds
    /// them).
    /// </para>
    /// <para>
    /// The count follows the pack's rule for any per-room section that could
    /// hold rotation variants: it starts with how many it holds, 1 or 4, and
    /// the link takes payload <c>rotation mod count</c>. With 4, payload
    /// <i>r</i> holds the same relations (the reader refuses four that
    /// differ) and the boxes turned <i>r</i> quarters. Stored once by
    /// default: turning a room's few cluster boxes at link is a handful of
    /// float negations per placement, and on the 256-room stress library the
    /// two link to the same bytes in the same time within run-to-run noise
    /// (the pack's <c>DVIS</c> sections grow fourfold for nothing).
    /// </para>
    /// </remarks>
    /// <param name="rotations">
    /// 1 (the default) to store the boxes room-local, turned at link; 4 to
    /// store one payload per quarter turn with its boxes turned, for the
    /// facts that hold the two to the same link and for a measurement that
    /// ever finds four copies faster.
    /// </param>
    public byte[] ToSection(RoomLinkCodec codec = RoomLinkCodec.None, int rotations = 1)
    {
        if (rotations is not (1 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(rotations), rotations, "a section holds 1 or 4 rotations");
        }

        RoomLinkSections.Writer w = new();
        w.Int(Revision);
        w.Int(rotations);
        for (int turn = 0; turn < rotations; turn++)
        {
            WritePayload(w, turn);
        }

        return RoomLinkSections.Encode(w.ToArray(), codec);
    }

    private void WritePayload(RoomLinkSections.Writer w, int turn)
    {
        w.Int(ClusterCount);
        w.Int(SocketCount);
        foreach (ulong[] row in Sees)
        {
            foreach (ulong word in row)
            {
                w.Int((int)(word >> 32));
                w.Int((int)word);
            }
        }

        foreach (bool flag in Through)
        {
            w.Byte(flag ? (byte)1 : (byte)0);
        }

        for (int c = 0; c < ClusterCount; c++)
        {
            w.Byte(HasBox[c] ? (byte)1 : (byte)0);
            if (HasBox[c])
            {
                w.Box(TurnedBox(c, turn));
            }
        }
    }

    /// <summary>
    /// A room's door visibility from its section, or null when the section
    /// is absent or of a revision this build does not read (the link then
    /// works it out from the room, to the same relations).
    /// </summary>
    /// <param name="section">The section's bytes, or null.</param>
    /// <param name="room">The room it sits with: its cluster and socket counts must match.</param>
    /// <returns>The door visibility, or null.</returns>
    /// <exception cref="LinkException">
    /// A codec this build does not read, a payload that decodes to another
    /// length than recorded, or one out of shape: a rotation count other
    /// than 1 or 4, counts that are not the room's, a bit past the room's
    /// clusters, a socket through itself, a flag byte other than 0 or 1, a
    /// box whose mins pass its maxs, bytes after its end.
    /// </exception>
    public static RoomDoorVisibility? Read(ArraySegment<byte>? section, RoomObject room)
    {
        string name = room.Definition.Name;
        if (RoomLinkSections.Open(section, name, SectionTag) is not { } r)
        {
            return null;
        }

        int rotations = r.Int();
        if (rotations is not (1 or 4))
        {
            throw r.Mismatch($"{rotations} rotations; a section holds 1 or 4");
        }

        RoomDoorVisibility first = ReadPayload(r, room);
        if (rotations == 1)
        {
            r.End();
            return first;
        }

        // Four payloads: the same relations, each turn's boxes turned.
        Box[][] turned = new Box[4][];
        turned[0] = first.ClusterBoxes;
        for (int turn = 1; turn < 4; turn++)
        {
            RoomDoorVisibility read = ReadPayload(r, room);
            if (!read.Sees.Zip(first.Sees).All(p => p.First.AsSpan().SequenceEqual(p.Second))
                || !read.Through.AsSpan().SequenceEqual(first.Through)
                || !read.HasBox.AsSpan().SequenceEqual(first.HasBox))
            {
                throw r.Mismatch($"turn {turn}'s relations, which differ from turn 0's");
            }

            turned[turn] = read.ClusterBoxes;
        }

        r.End();
        return new RoomDoorVisibility(first.ClusterCount, first.Sees, first.Through, first.ClusterBoxes, first.HasBox, turned);
    }

    private static RoomDoorVisibility ReadPayload(RoomLinkSections.Reader r, RoomObject room)
    {
        int clusters = r.Int();
        if (clusters != room.Vis.ClusterCount)
        {
            throw r.Mismatch($"{clusters} clusters; the room has {room.Vis.ClusterCount}");
        }

        int sockets = r.Int();
        if (sockets != room.Definition.Sockets.Count)
        {
            throw r.Mismatch($"{sockets} sockets; the room has {room.Definition.Sockets.Count}");
        }

        int words = (clusters + 63) >> 6;
        ulong spare = (clusters & 63) == 0 ? 0 : ~0UL << (clusters & 63);
        ulong[][] sees = new ulong[sockets][];
        for (int s = 0; s < sockets; s++)
        {
            sees[s] = new ulong[words];
            for (int w = 0; w < words; w++)
            {
                ulong high = (uint)r.Int();
                ulong low = (uint)r.Int();
                sees[s][w] = (high << 32) | low;
            }

            if (words > 0 && (sees[s][words - 1] & spare) != 0)
            {
                throw r.Mismatch($"socket {s} seen by a cluster past the room's {clusters}");
            }
        }

        bool[] through = new bool[sockets * sockets];
        for (int i = 0; i < through.Length; i++)
        {
            through[i] = r.Flag();
            if (through[i] && i / sockets == i % sockets)
            {
                throw r.Mismatch($"socket {i % sockets} through itself");
            }
        }

        Box[] boxes = new Box[clusters];
        bool[] hasBox = new bool[clusters];
        for (int c = 0; c < clusters; c++)
        {
            hasBox[c] = r.Flag();
            if (!hasBox[c])
            {
                continue;
            }

            Box box = r.Box();
            if (!(box.Mins.X <= box.Maxs.X && box.Mins.Y <= box.Maxs.Y && box.Mins.Z <= box.Maxs.Z))
            {
                throw r.Mismatch($"cluster {c}'s box with its mins past its maxs");
            }

            boxes[c] = box;
        }

        return new RoomDoorVisibility(clusters, sees, through, boxes, hasBox);
    }
}
