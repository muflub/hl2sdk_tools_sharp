using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp.Portals;

/// <summary>
/// A plain <see cref="IBspNode"/>: the node type the portal stages were
/// developed against, and a tree builder can use it as-is.
/// </summary>
/// <remarks>
/// Nothing in <c>Bsp/Portals</c> mentions this class; every stage takes
/// <see cref="IBspNode"/>. It exists so the portal code can be built and
/// gated against hand-made and reconstructed trees without waiting for
/// <c>BuildTree_r</c>, and so that there is one worked example of what the
/// interface asks for.
/// </remarks>
public sealed class BspNode : IBspNode
{
    private readonly List<MapBrush> _leafBrushes = [];

    /// <summary>Creates a leaf.</summary>
    /// <param name="id">A number unique within the tree.</param>
    public BspNode(int id)
    {
        Id = id;
        PlaneNumber = IBspNode.LeafPlaneNumber;
        Mins = MapFile.ClearedMins;
        Maxs = MapFile.ClearedMaxs;
    }

    /// <inheritdoc/>
    public int Id { get; }

    /// <inheritdoc/>
    public int PlaneNumber { get; set; }

    /// <inheritdoc/>
    public IBspNode? Parent { get; set; }

    /// <inheritdoc/>
    public IBspNode? Front { get; set; }

    /// <inheritdoc/>
    public IBspNode? Back { get; set; }

    /// <inheritdoc/>
    public MapBrushSide? Side { get; set; }

    /// <inheritdoc/>
    public Vec3 Mins { get; set; }

    /// <inheritdoc/>
    public Vec3 Maxs { get; set; }

    /// <inheritdoc/>
    public int Contents { get; set; }

    /// <inheritdoc/>
    public int Occupied { get; set; }

    /// <inheritdoc/>
    public MapEntity? Occupant { get; set; }

    /// <inheritdoc/>
    public int Cluster { get; set; }

    /// <inheritdoc/>
    public int Area { get; set; }

    /// <inheritdoc/>
    public Portal? Portals { get; set; }

    /// <inheritdoc/>
    public IReadOnlyList<MapBrush> LeafBrushes => _leafBrushes;

    /// <summary>Appends a brush fragment's original to this leaf's list.</summary>
    /// <param name="brush">The original map brush.</param>
    public void AddLeafBrush(MapBrush brush) => _leafBrushes.Add(brush);

    /// <summary>Makes this node split on a plane, with the two given children.</summary>
    /// <param name="planeNumber">The index into the map's plane table.</param>
    /// <param name="front">The child in front of the plane.</param>
    /// <param name="back">The child behind it.</param>
    public void SplitOn(int planeNumber, BspNode front, BspNode back)
    {
        ArgumentNullException.ThrowIfNull(front);
        ArgumentNullException.ThrowIfNull(back);

        PlaneNumber = planeNumber;
        Front = front;
        Back = back;
        front.Parent = this;
        back.Parent = this;
    }
}

/// <summary>A plain <see cref="IBspTree"/> over <see cref="BspNode"/>.</summary>
public sealed class BspTree : IBspTree
{
    /// <summary>Creates a tree.</summary>
    /// <param name="headNode">The root.</param>
    /// <param name="outsideNode">The leaf outside the box portals.</param>
    /// <param name="mins">The tree's lower bound, before <c>SIDESPACE</c> padding.</param>
    /// <param name="maxs">The tree's upper bound, before <c>SIDESPACE</c> padding.</param>
    public BspTree(BspNode headNode, BspNode outsideNode, Vec3 mins, Vec3 maxs)
    {
        HeadNode = headNode;
        OutsideNode = outsideNode;
        Mins = mins;
        Maxs = maxs;
    }

    /// <inheritdoc/>
    public IBspNode HeadNode { get; }

    /// <inheritdoc/>
    public IBspNode OutsideNode { get; }

    /// <inheritdoc/>
    public Vec3 Mins { get; }

    /// <inheritdoc/>
    public Vec3 Maxs { get; }

    /// <inheritdoc/>
    public bool Leaked { get; set; }
}
