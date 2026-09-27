//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp.Csg;

/// <summary>
/// A brush being carved: <c>bspbrush_t</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Next"/> is not an implementation detail.</b> Stock threads
/// every brush list through <c>bspbrush_t::next</c> and the CSG stage then
/// SPLICES those lists — <c>AddBrushListToTail</c> appends one to another
/// <c>CullList</c> rebuilds one REVERSED
/// And <c>ChopBrushes</c>'s <c>goto newlist</c> restarts
/// the whole O(n²) scan over the result. The order those
/// operations leave behind decides which brush bites which, so it decides the
/// output. A <c>List&lt;BspBrush&gt;</c> here would be a different algorithm
/// wearing the same name.
/// </para>
/// <para>
/// The sides are a growable array rather than stock's variable-length
/// <c>sides[6]</c> tail allocation, which exists so that
/// <c>AllocBrush(n)</c> can <c>malloc</c> exactly
/// <c>offsetof(bspbrush_t, sides[n])</c> bytes. The behaviour that depends on
/// it is that <c>SplitBrush</c> reserves <c>numsides + 1</c> and then appends
/// — appending is what
/// this reproduces, and the capacity is kept so that over-appending is caught
/// rather than silently grown.
/// </para>
/// <para>
/// <b>The array is the build's, not the brush's.</b> CSG and the tree build
/// make and discard brushes by the hundred thousand -- every split makes two
/// and most are freed within a few calls -- so
/// <see cref="BspBuildContext.AllocBrush"/> rents the side array from the
/// build's <see cref="BrushSidePool"/> and
/// <see cref="BspBuildContext.FreeBrush"/> gives it back. That is why a freed
/// brush has no sides at all (<see cref="IsFreed"/>).
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
    /// <c>AllocBrush</c>. The id and the active-brush
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

    /// <summary>Creates a brush over side storage the caller rented.</summary>
    /// <param name="storage">
    /// The side array, whose length is the capacity. It must be all
    /// <c>default</c>: a brush starts with no sides, and <see cref="AddSide"/>
    /// relies on the slots past <see cref="SideCount"/> never being read, but
    /// <see cref="BrushSidePool"/> clears on return so that a stale side from
    /// an earlier brush cannot be seen even by a reader that breaks that rule.
    /// </param>
    /// <remarks>
    /// How <see cref="BspBuildContext.AllocBrush"/> hands a brush an array out
    /// of the build's pool rather than a fresh one.
    /// </remarks>
    internal BspBrush(BspBrushSide[] storage)
    {
        _sides = storage;
    }

    /// <summary>The brush's serial number: <c>bspbrush_t::id</c>.</summary>
    /// <remarks>
    /// Stock's <c>s_BrushId</c> counts every brush ever allocated in the
    /// process and the value is read only by the
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
    /// and read by <c>SplitBrushList</c>. Two fields
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
    /// of nothing. Stock has the same two cases and
    /// the same null; <c>LeafNode</c> only ever walks the first kind.
    /// </remarks>
    public MapBrush? Original { get; set; }

    /// <summary>How many sides the brush has: <c>numsides</c>.</summary>
    /// <exception cref="InvalidOperationException">The brush has been freed.</exception>
    /// <remarks>
    /// Reading it on a freed brush throws for the same reason
    /// <see cref="Sides"/> does: see <see cref="IsFreed"/>.
    /// </remarks>
    public int SideCount
    {
        get
        {
            ThrowIfFreed();
            return _sideCount;
        }

        private set => _sideCount = value;
    }

    private int _sideCount;

    /// <summary>
    /// Whether <see cref="BspBuildContext.FreeBrush"/> has released this brush.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A freed brush has given its side array back to the build's
    /// <see cref="BrushSidePool"/>, and the next brush of the same capacity
    /// will be carved in it. So its sides are gone rather than merely
    /// windingless: <see cref="Sides"/> and <see cref="SideCount"/> throw
    /// instead of showing another brush's planes. Stock's
    /// <c>FreeBrush</c> hands the memory back to the heap and a read after it
    /// is undefined; here it is loud.
    /// </para>
    /// <para>
    /// The header fields (<see cref="Next"/>, <see cref="Original"/>, the
    /// bounds) stay readable, because <c>FreeBrushList</c> and
    /// <c>CullList</c> read <c>next</c> around the free and nothing about the
    /// pool touches them.
    /// </para>
    /// </remarks>
    public bool IsFreed { get; private set; }

    /// <summary>How many sides were reserved: <c>AllocBrush</c>'s argument.</summary>
    public int SideCapacity => _sides.Length;

    /// <summary>The side array itself, capacity and all, for the pool's facts.</summary>
    internal BspBrushSide[] SideStorage => _sides;

    /// <summary>The brush's live sides.</summary>
    /// <remarks>
    /// A span, so <c>brush.Sides[i].Tested = true</c> writes through to the
    /// array the way stock writes through <c>brush-&gt;sides[i].tested</c>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The brush has been freed.</exception>
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
    /// <exception cref="InvalidOperationException">The brush has been freed.</exception>
    public void SetSideCount(int count)
    {
        ThrowIfFreed();
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _sides.Length);
        SideCount = count;
    }

    /// <summary>
    /// Marks the brush freed and hands back its side array, which the brush no
    /// longer owns.
    /// </summary>
    /// <returns>The side array, with whatever it held; the caller clears it.</returns>
    /// <exception cref="InvalidOperationException">The brush was already freed.</exception>
    /// <remarks>
    /// Detaching is what makes a double return impossible: the second
    /// <c>FreeBrush</c> of a brush finds it freed and throws, instead of
    /// putting one array into the pool twice and handing it to two brushes.
    /// </remarks>
    internal BspBrushSide[] DetachSides()
    {
        ThrowIfFreed();

        BspBrushSide[] sides = _sides;
        _sides = [];
        _sideCount = 0;
        IsFreed = true;
        return sides;
    }

    private void ThrowIfFreed()
    {
        if (IsFreed)
        {
            throw new InvalidOperationException(
                $"BspBrush {Id}: its sides were read after FreeBrush returned them to the pool");
        }
    }

    /// <summary>
    /// Fills the brush from a map brush's sides, reserving exactly its side
    /// count.
    /// </summary>
    /// <param name="map">The map holding the sides.</param>
    /// <param name="brush">The map brush.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="InvalidOperationException">The brush has been freed.</exception>
    /// <remarks>
    /// <c>CreateClippedBrush</c>'s memcpy. The windings are
    /// the map sides' own handles at this point; the caller duplicates them, as
    /// stock does on the following line.
    /// </remarks>
    public void CopySidesFrom(MapFile map, MapBrush brush)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(brush);
        ThrowIfFreed();

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
