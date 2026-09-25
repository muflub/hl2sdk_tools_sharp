using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Bsp.Csg;

/// <summary>
/// The state, and keep in
/// file-scope statics, in one object with an owner.
/// </summary>
/// <remarks>
/// <para>
/// Eleven pieces of hidden state are replaced here: <c>c_nodes</c>,
/// <c>c_nonvis</c>, <c>c_active_brushes</c>,
/// <c>c_pruned</c>, <c>AllocNode</c>'s
/// <c>static int s_NodeCount</c>,
/// <c>AllocBrush</c>'s <c>static int s_BrushId</c>,
/// <c>minplanenums</c> and <c>maxplanenums</c>,
/// <c>block_nodes</c> and the pair
/// <c>brush_start</c>/<c>brush_end</c>. All of them are
/// per-compile, and two of them — the bounding plane numbers and the block node
/// grid — are per-compile state that stock's one threaded call site
/// Writes from what it believes are several threads.
/// </para>
/// <para>
/// <b>Stock's <c>numthreads == 1</c> guards are always taken.</b>
/// <c>AllocBrush</c>, <c>FreeBrush</c>, <c>BuildTree_r</c>,
/// <c>SelectSplitSide</c> and <c>FreeTree_r</c> all bump their counters only
/// when <c>numthreads == 1</c> — and vbsp sets <c>numthreads = 1</c>
/// unconditionally, after parsing <c>-threads</c>, with
/// the comment "multiple threads aren't helping...". So the guard is dead and
/// the counters are always live. They are always live here too, and the guard
/// is not reproduced: a counter that silently stops counting when a later phase
/// makes this parallel is a worse bug than a contended increment.
/// </para>
/// <para>
/// One context is one compile of one map on one thread. Phase 3p's fork/join
/// over <c>BuildTree_r</c> gets per-worker state; it does not share this.
/// </para>
/// </remarks>
public sealed class BspBuildContext
{
    private readonly int[] _minPlaneNumbers = [-1, -1, -1];
    private readonly int[] _maxPlaneNumbers = [-1, -1, -1];

    /// <summary>Creates a build context over a loaded map.</summary>
    /// <param name="compile">The compile this belongs to.</param>
    /// <param name="map">The map being carved: stock's <c>g_MainMap</c>.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public BspBuildContext(VbspContext compile, MapFile map)
    {
        ArgumentNullException.ThrowIfNull(compile);
        ArgumentNullException.ThrowIfNull(map);

        Compile = compile;
        Map = map;
    }

    /// <summary>The compile's shared state.</summary>
    public VbspContext Compile { get; }

    /// <summary>The map being carved: <c>g_MainMap</c>.</summary>
    /// <remarks>
    /// Every plane lookup in these three files goes through
    /// <c>g_MainMap-&gt;mapplanes</c> and <c>g_MainMap-&gt;FindFloatPlane</c>,
    /// never through the map currently being loaded. An instance's own plane
    /// table is finished with by the time CSG runs.
    /// </remarks>
    public MapFile Map { get; }

    /// <summary>The compile's switches.</summary>
    public VbspOptions Options => Compile.Options;

    /// <summary>The arena every winding in the compile lives in.</summary>
    public WindingArena Windings => Compile.Windings;

    /// <summary>The map's plane table: <c>g_MainMap-&gt;mapplanes</c>.</summary>
    public PlaneTable Planes => Map.Planes;

    /// <summary>Everything the compile has to say.</summary>
    public IList<CompileDiagnostic> Diagnostics => Compile.Diagnostics;

    /// <summary>How many brushes have ever been allocated: <c>s_BrushId</c>.</summary>
    public int AllocatedBrushes { get; private set; }

    /// <summary>How many brushes are allocated now: <c>c_active_brushes</c>.</summary>
    public int ActiveBrushes { get; private set; }

    /// <summary>How many nodes have ever been allocated: <c>s_NodeCount</c>.</summary>
    public int AllocatedNodes { get; private set; }

    /// <summary>How many nodes the current tree has: <c>c_nodes</c>.</summary>
    /// <remarks>
    /// Reset to zero at the top of <c>BrushBSP</c>
    /// and decremented by <c>FreeTree_r</c>, so it is a
    /// live count and not a total. <c>BrushBSP</c> reports
    /// <c>c_nodes/2 - c_nonvis</c> visible nodes and <c>(c_nodes+1)/2</c>
    /// leaves off it.
    /// </remarks>
    public int Nodes { get; set; }

    /// <summary>
    /// How many nodes were split by a non-visible face: <c>c_nonvis</c>.
    /// </summary>
    public int NonVisibleNodes { get; set; }

    /// <summary>How many nodes <c>PruneNodes</c> collapsed: <c>c_pruned</c>.</summary>
    public int PrunedNodes { get; set; }

    /// <summary>
    /// The <c>+X</c> and <c>+Y</c> bounding planes of the current clip box:
    /// <c>maxplanenums</c>.
    /// </summary>
    /// <remarks>
    /// Three slots because stock declares three, but
    /// <c>ComputeBoundingPlanes</c> only ever fills the first two
    /// (<c>for (i=0; i&lt;2; i++)</c>) and
    /// <c>ClipBrushToBox</c> only ever reads those two. The Z slot is never
    /// written and never read; it stays -1 here where stock leaves it zero,
    /// so that reading it is an obviously invalid plane index rather than a
    /// plausible one.
    /// </remarks>
    public ReadOnlySpan<int> MaxPlaneNumbers => _maxPlaneNumbers;

    /// <summary>
    /// The <c>-X</c> and <c>-Y</c> bounding planes of the current clip box:
    /// <c>minplanenums</c>.
    /// </summary>
    /// <remarks>
    /// <b>These are +X and +Y normals at the box's MINIMUM distance, not
    /// negated planes.</b> <c>ComputeBoundingPlanes</c> sets
    /// <c>normal[i] = 1</c> once and calls <c>FindFloatPlane</c> twice, at
    /// <c>clipmaxs[i]</c> and then <c>clipmins[i]</c>,
    /// which is why <c>ClipBrushToBox</c> keeps the FRONT half when it clips on
    /// a min plane and the BACK half on a max plane.
    /// </remarks>
    public ReadOnlySpan<int> MinPlaneNumbers => _minPlaneNumbers;

    /// <summary>
    /// The first brush of the entity being compiled: <c>brush_start</c>.
    /// </summary>
    public int BrushStart { get; set; }

    /// <summary>
    /// One past the last brush of the entity being compiled: <c>brush_end</c>.
    /// </summary>
    public int BrushEnd { get; set; }

    /// <summary>
    /// Allocates a brush and gives it the next id: <c>AllocBrush</c>.
    /// </summary>
    /// <param name="sideCapacity">How many sides to reserve.</param>
    /// <returns>The brush.</returns>
    public BspBrush AllocBrush(int sideCapacity)
    {
        BspBrush brush = new(sideCapacity) { Id = AllocatedBrushes };
        AllocatedBrushes++;
        ActiveBrushes++;
        return brush;
    }

    /// <summary>
    /// Frees a brush's windings and drops it from the live count:
    /// <c>FreeBrush</c>.
    /// </summary>
    /// <param name="brush">The brush to free.</param>
    /// <exception cref="ArgumentNullException"><paramref name="brush"/> is null.</exception>
    /// <remarks>
    /// The brush object itself is the GC's; what has to be returned by hand is
    /// the arena storage its side windings hold, which is exactly what stock's
    /// <c>FreeWinding</c> loop does. Freeing a brush twice throws out of the
    /// arena's double-free check rather than corrupting a free list.
    /// </remarks>
    public void FreeBrush(BspBrush brush)
    {
        ArgumentNullException.ThrowIfNull(brush);

        for (int i = 0; i < brush.SideCount; i++)
        {
            Windings.Free(brush.Sides[i].Winding);
            brush.Sides[i].Winding = Winding.Null;
        }

        ActiveBrushes--;
    }

    /// <summary>
    /// Frees a whole list: <c>FreeBrushList</c>.
    /// </summary>
    /// <param name="brushes">The head of the list, or null.</param>
    public void FreeBrushList(BspBrush? brushes)
    {
        for (BspBrush? b = brushes; b is not null;)
        {
            BspBrush? next = b.Next;
            FreeBrush(b);
            b = next;
        }
    }

    /// <summary>
    /// Allocates a node and gives it the next id: <c>AllocNode</c>.
    /// </summary>
    /// <returns>The node, with <c>diskId</c> -1 as stock sets it.</returns>
    public Tree.BspNode AllocNode()
    {
        Tree.BspNode node = new() { Id = AllocatedNodes };
        AllocatedNodes++;
        return node;
    }

    /// <summary>
    /// Records the bounding planes of a clip box:
    /// <c>ComputeBoundingPlanes</c>.
    /// </summary>
    /// <param name="clipMins">The box's minimum.</param>
    /// <param name="clipMaxs">The box's maximum.</param>
    /// <remarks>
    /// Called at the top of both <c>MakeBspBrushList</c> overloads, so the pair
    /// always describes the box the most recent list was built for. It appends
    /// to the plane table — four planes for a box that has none of them yet —
    /// which is why a block that produces no brushes still leaves planes
    /// behind.
    /// </remarks>
    public void ComputeBoundingPlanes(Vec3 clipMins, Vec3 clipMaxs)
    {
        for (int i = 0; i < 2; i++)
        {
            Vec3 normal = i switch
            {
                0 => new Vec3(1f, 0f, 0f),
                _ => new Vec3(0f, 1f, 0f),
            };

            _maxPlaneNumbers[i] = Planes.Find(normal, clipMaxs[i]);
            _minPlaneNumbers[i] = Planes.Find(normal, clipMins[i]);
        }
    }
}
