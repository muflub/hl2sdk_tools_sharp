using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp.Csg;

/// <summary>
/// A brush being carved: <c>bspbrush_t</c>, <c>utils/vbsp/vbsp.h:177</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Next"/> is not an implementation detail.</b> Stock threads
/// every brush list through <c>bspbrush_t::next</c> and the CSG stage then
/// SPLICES those lists — <c>AddBrushListToTail</c> appends one to another
/// (<c>csg.cpp:444</c>), <c>CullList</c> rebuilds one REVERSED
/// (<c>csg.cpp:466</c>), and <c>ChopBrushes</c>'s <c>goto newlist</c> restarts
/// the whole O(n²) scan over the result (<c>csg.cpp:679</c>). The order those
/// operations leave behind decides which brush bites which, so it decides the
/// output. A <c>List&lt;BspBrush&gt;</c> here would be a different algorithm
/// wearing the same name.
/// </para>
/// <para>
/// The sides are a growable array rather than stock's variable-length
/// <c>sides[6]</c> tail allocation (<c>vbsp.h:184</c>), which exists so that
/// <c>AllocBrush(n)</c> can <c>malloc</c> exactly
/// <c>offsetof(bspbrush_t, sides[n])</c> bytes. The behaviour that depends on
/// it is that <c>SplitBrush</c> reserves <c>numsides + 1</c> and then appends
/// (<c>brushbsp.cpp:1132</c>, <c>:1168</c>, <c>:1222</c>) — appending is what
/// this reproduces, and the capacity is kept so that over-appending is caught
/// rather than silently grown.
/// </para>
/// <para>
/// Not a struct, because a brush's identity is what a list is made of and
/// because <see cref="Tree.BrushBspTree.LeafNode"/> hands the same brush to a
/// node's <c>brushlist</c> after the list has been walked.
/// </para>
/// </remarks>
public sealed class BspBrush
{
    private BspBrushSide[] _sides;

    /// <summary>Creates a brush with room for a given number of sides.</summary>
    /// <param name="capacity">How many sides to reserve: <c>AllocBrush</c>'s argument.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    /// <remarks>
    /// <c>AllocBrush</c>, <c>brushbsp.cpp:333</c>. The id and the active-brush
    /// count it also maintains belong to the compile, so they are assigned by
    /// <see cref="BspBuildContext.AllocBrush"/> rather than here — a brush
    /// constructed directly is a brush outside the accounting, which is what a
    /// test wants and a stage never does.
    /// </remarks>
    public BspBrush(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _sides = capacity == 0 ? [] : new BspBrushSide[capacity];
    }

    /// <summary>The brush's serial number: <c>bspbrush_t::id</c>.</summary>
    /// <remarks>
    /// Stock's <c>s_BrushId</c> counts every brush ever allocated in the
    /// process (<c>brushbsp.cpp:335</c>) and the value is read only by the
    /// glview debug dumps. It is kept because a brush id in a diagnostic that
    /// does not match stock's is worse than no id at all.
    /// </remarks>
    public int Id { get; set; }

    /// <summary>The next brush in the list, or null: <c>next</c>.</summary>
    public BspBrush? Next { get; set; }

    /// <summary>The brush's bounding box minimum.</summary>
    public Vec3 Mins { get; set; }

    /// <summary>The brush's bounding box maximum.</summary>
    public Vec3 Maxs { get; set; }

    /// <summary>
    /// Which side of the node being built this brush is on: <c>side</c>.
    /// </summary>
    /// <remarks>
    /// A <see cref="Tree.PlaneSideFlags"/> value saved by
    /// <c>SelectSplitSide</c> from <see cref="TestSide"/> when a plane wins,
    /// and read by <c>SplitBrushList</c> (<c>brushbsp.cpp:1277</c>). Two fields
    /// and not one because the winner is only known after every candidate has
    /// been scored.
    /// </remarks>
    public int Side { get; set; }

    /// <summary>
    /// Which side of the plane currently being scored: <c>testside</c>.
    /// </summary>
    public int TestSide { get; set; }

    /// <summary>The map brush this was carved from: <c>original</c>.</summary>
    /// <remarks>
    /// Never null on a brush that came from <c>MakeBspBrushList</c>, and null
    /// on a node's <c>volume</c> brush, which <c>BrushFromBounds</c> builds out
    /// of nothing (<c>brushbsp.cpp:201</c>). Stock has the same two cases and
    /// the same null; <c>LeafNode</c> only ever walks the first kind.
    /// </remarks>
    public MapBrush? Original { get; set; }

    /// <summary>How many sides the brush has: <c>numsides</c>.</summary>
    public int SideCount { get; private set; }

    /// <summary>How many sides were reserved: <c>AllocBrush</c>'s argument.</summary>
    public int SideCapacity => _sides.Length;

    /// <summary>The brush's live sides.</summary>
    /// <remarks>
    /// A span, so <c>brush.Sides[i].Tested = true</c> writes through to the
    /// array the way stock writes through <c>brush-&gt;sides[i].tested</c>.
    /// </remarks>
    public Span<BspBrushSide> Sides => _sides.AsSpan(0, SideCount);

    /// <summary>Appends a side, as stock writes into <c>sides[numsides++]</c>.</summary>
    /// <param name="side">The side to append.</param>
    /// <exception cref="InvalidOperationException">
    /// The brush has no room left. Stock would corrupt the heap here; this says
    /// so instead.
    /// </exception>
    public void AddSide(BspBrushSide side)
    {
        if (SideCount == _sides.Length)
        {
            throw new InvalidOperationException(
                $"BspBrush: appending side {SideCount} to a brush allocated for {_sides.Length}");
        }

        _sides[SideCount++] = side;
    }

    /// <summary>
    /// Drops the sides past a count, as stock's <c>numsides</c> assignments do.
    /// </summary>
    /// <param name="count">The new side count.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> is negative or above the capacity.
    /// </exception>
    public void SetSideCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _sides.Length);
        SideCount = count;
    }

    /// <summary>
    /// Fills the brush from a map brush's sides, reserving exactly its side
    /// count.
    /// </summary>
    /// <param name="map">The map holding the sides.</param>
    /// <param name="brush">The map brush.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// <c>CreateClippedBrush</c>'s memcpy, <c>csg.cpp:235</c>. The windings are
    /// the map sides' own handles at this point; the caller duplicates them, as
    /// stock does on the following line.
    /// </remarks>
    public void CopySidesFrom(MapFile map, MapBrush brush)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(brush);

        if (_sides.Length < brush.SideCount)
        {
            _sides = new BspBrushSide[brush.SideCount];
        }

        for (int i = 0; i < brush.SideCount; i++)
        {
            _sides[i] = BspBrushSide.From(map.BrushSides[brush.FirstSide + i]);
        }

        SideCount = brush.SideCount;
    }
}
