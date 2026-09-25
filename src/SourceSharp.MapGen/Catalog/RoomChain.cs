namespace SourceSharp.MapGen.Catalog;

/// <summary>
/// N rooms in a row, each joined to the next by a doorway: the shape most of the
/// vvis catalogue is made of.
///
/// <para>
/// One generator rather than four, because "two rooms and a door", "a long
/// corridor", "a see-through-three-doors line" and "an almost-line that must not
/// see" are the SAME map with different N and different doorway offsets. Writing
/// them separately would mean four places for a sealing bug to hide and four
/// brush counts to keep in step.
/// </para>
///
/// <para>
/// One outer shell around the whole chain, with the dividers inside it, rather
/// than N shells butted together. Butted shells share a face, which is an
/// overlapping-CSG case in every vvis entry in the catalogue, and a gap of one
/// unit between two of them is a leak nobody would see in the VMF.
/// </para>
/// </summary>
public sealed class RoomChain
{
    /// <summary>How long each room is, along the chain.</summary>
    public const float RoomLength = 384f;

    /// <summary>How far the rooms reach either side of the chain's axis.</summary>
    public const float RoomHalfWidth = 384f;

    /// <summary>How tall the rooms are.</summary>
    public const float RoomHeight = 256f;

    /// <summary>How thick a divider between two rooms is.</summary>
    public const float DividerThickness = 32f;

    /// <summary>
    /// How wide a doorway is.
    ///
    /// <para>
    /// NARROW ON PURPOSE, and the number is load-bearing. A doorway's width sets
    /// how fast a sight line can move sideways per room, and that is what
    /// decides whether <see cref="TestMapCatalog"/>'s almost-a-line entry can
    /// actually be made not to see. With 128-wide doors in 384-long rooms the
    /// reachable spread after three doorways covers more than the room is wide
    /// and no offset breaks the line; at 64 it does not. The arithmetic is in
    /// <see cref="ReachableSpreadAfter"/>.
    /// </para>
    /// </summary>
    public const float DoorWidth = 64f;

    /// <summary>How tall a doorway is, from the floor.</summary>
    public const float DoorHeight = 128f;

    /// <summary>How many rooms.</summary>
    public required int Rooms { get; init; }

    /// <summary>
    /// Where each doorway's centre sits, across the chain. One entry per
    /// divider, so <see cref="Rooms"/> minus one of them.
    /// </summary>
    public required IReadOnlyList<float> DoorOffsets { get; init; }

    /// <summary>Every doorway on the axis: a straight line through the lot.</summary>
    /// <param name="rooms">How many rooms.</param>
    public static RoomChain Straight(int rooms)
        => new() { Rooms = rooms, DoorOffsets = [.. Enumerable.Repeat(0f, rooms - 1)] };

    /// <summary>
    /// Doorways alternating to either side: what makes a corridor a DEEP portal
    /// chain rather than a long sight line.
    /// </summary>
    /// <param name="rooms">How many rooms.</param>
    /// <param name="offset">How far to either side.</param>
    public static RoomChain Zigzag(int rooms, float offset = 256f)
        => new()
        {
            Rooms = rooms,
            DoorOffsets = [.. Enumerable.Range(0, rooms - 1).Select(i => i % 2 == 0 ? -offset : offset)],
        };

    /// <summary>The spacing from one divider to the next.</summary>
    public static float Pitch => RoomLength + DividerThickness;

    /// <summary>How long the whole chain is, end to end.</summary>
    public float Length => (Rooms * RoomLength) + ((Rooms - 1) * DividerThickness);

    /// <summary>
    /// Where the chain starts.
    ///
    /// <para>
    /// CENTRED ON THE ORIGIN, and that is not tidiness. Source's world is
    /// +-16384 units on each axis and a chain long enough to be an L3 entry runs
    /// out of it: 64 rooms is 26,592 units, which starting at zero ends 10,208
    /// units outside the map. Centred, the same chain reaches +-13,296 and fits.
    /// Every chain length this generator can produce lands on a 16-unit grid
    /// (416n - 32, halved, is 208n - 16), so centring costs no off-grid
    /// coordinates.
    /// </para>
    /// </summary>
    public float Start => -Length * 0.5f;

    /// <summary>Where divider <paramref name="index"/>'s low face sits, along the chain.</summary>
    /// <param name="index">Which divider, from zero.</param>
    public float DividerAt(int index) => Start + RoomLength + (index * Pitch);

    /// <summary>The whole chain's interior volume, shell excluded.</summary>
    public Bounds Interior => new(
        new Point(Start, -RoomHalfWidth, 0f),
        new Point(Start + Length, RoomHalfWidth, RoomHeight));

    /// <summary>The middle of one room's floor plan.</summary>
    /// <param name="index">Which room, from zero.</param>
    public Point RoomCentre(int index)
        => new(Start + (index * Pitch) + (RoomLength * 0.5f), 0f, 0f);

    /// <summary>What a room's probe is called.</summary>
    /// <param name="index">Which room, from zero.</param>
    public static string ProbeName(int index)
        => $"room{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>A probe standing in the middle of one room, at eye height.</summary>
    /// <param name="index">Which room, from zero.</param>
    public VisProbe Probe(int index)
        => new(ProbeName(index), RoomCentre(index) + new Point(0f, 0f, 64f));

    /// <summary>One probe per room.</summary>
    public IReadOnlyList<VisProbe> Probes
        => [.. Enumerable.Range(0, Rooms).Select(Probe)];

    /// <summary>
    /// How far across the chain a straight sight line can have moved by the
    /// n-th doorway, given it passed through every doorway before it.
    ///
    /// <para>
    /// The one piece of arithmetic behind the "almost a line that must NOT see"
    /// entry, written down rather than eyeballed. Doorways are evenly spaced, so
    /// a line through doorway i-1 at y(i-1) and doorway i at y(i) arrives at
    /// doorway i+1 at exactly 2·y(i) − y(i−1). Starting from a half-width of
    /// w = DoorWidth/2 at the first two doorways, the reachable interval at
    /// doorway n is ±(2n−1)·w.
    /// </para>
    ///
    /// <para>
    /// So a doorway whose own interval is disjoint from that one cannot be on
    /// any line through its predecessors, and the first room cannot see the
    /// last. With three doorways the bound is ±3w = ±96, which is why the
    /// almost-a-line entry offsets its third doorway by 256 and not by 128.
    /// </para>
    /// </summary>
    /// <param name="doorwayIndex">Which doorway, from zero.</param>
    public static float ReachableSpreadAfter(int doorwayIndex)
        => ((2 * doorwayIndex) - 1) * (DoorWidth * 0.5f);

    /// <summary>How many world solids <see cref="Build"/> produces.</summary>
    public int BrushCount => RoomKit.ShellBrushes + (3 * (Rooms - 1));

    /// <summary>
    /// Builds the chain: the shell, the dividers, a player start in the first
    /// room and a light in every room.
    /// </summary>
    /// <param name="map">The map to add to.</param>
    public void Build(VmfMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        if (Rooms < 1)
            throw new InvalidOperationException("a chain has at least one room");

        if (DoorOffsets.Count != Rooms - 1)
        {
            throw new InvalidOperationException(
                $"a {Rooms}-room chain has {Rooms - 1} dividers, so it needs that many door "
                + $"offsets and was given {DoorOffsets.Count}");
        }

        Bounds interior = Interior;

        RoomKit.Worldspawn(map);
        RoomKit.Shell(map, interior);

        for (int i = 0; i < DoorOffsets.Count; i++)
        {
            RoomKit.DividerWithDoorway(
                map, interior, DividerAt(i), DividerThickness,
                DoorOffsets[i], DoorWidth, DoorHeight);
        }

        RoomKit.PlayerStart(map, RoomCentre(0) + new Point(0f, 0f, 16f));

        for (int i = 0; i < Rooms; i++)
            RoomKit.Light(map, RoomCentre(i) + new Point(0f, 0f, RoomHeight - 32f));
    }

    /// <summary>
    /// The doorway's hole, as a volume: what an areaportal has to fill exactly.
    /// </summary>
    /// <param name="index">Which divider, from zero.</param>
    public Bounds Doorway(int index)
    {
        float x = DividerAt(index);
        float centre = DoorOffsets[index];

        return new Bounds(
            new Point(x, centre - (DoorWidth * 0.5f), Interior.Mins.Z),
            new Point(x + DividerThickness, centre + (DoorWidth * 0.5f), Interior.Mins.Z + DoorHeight));
    }

    /// <summary>One room's own interior volume, between its dividers.</summary>
    /// <param name="index">Which room, from zero.</param>
    public Bounds Room(int index)
    {
        float x0 = Start + (index * Pitch);

        return new Bounds(
            new Point(x0, -RoomHalfWidth, 0f),
            new Point(x0 + RoomLength, RoomHalfWidth, RoomHeight));
    }
}
