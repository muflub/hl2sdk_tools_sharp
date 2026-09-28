//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

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
/// One context is one compile of one map on one thread at a time. The
/// parallel tree build does not share it between threads: a subtree built on
/// another thread gets a <see cref="Fork"/> of it, with its own winding arena,
/// diagnostics, counters and id sequences, and <see cref="Join"/> folds the
/// fork back in the order the serial build would have produced it.
/// </para>
/// </remarks>
public sealed class BspBuildContext
{
    private readonly int[] _minPlaneNumbers = [-1, -1, -1];
    private readonly int[] _maxPlaneNumbers = [-1, -1, -1];

    // Set only on a fork. The root context reads the compile's own arena and
    // diagnostics through Compile, as it always has. The fork's arena comes
    // from the root's pool and goes back to it (ReleaseForkWindings), after
    // which this is null and the fork has no arena at all. A block's fork
    // (ForkForBlock) rents it only when it first allocates.
    private WindingArena? _forkWindings;
    private bool _forkWindingsReleased;
    private readonly List<CompileDiagnostic>? _forkDiagnostics;

    // The root's, shared by every fork of it, however deep: see ForkArenas.
    private readonly WindingArenaPool _forkArenas;

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
        _forkArenas = new WindingArenaPool(compile.Windings.Compliance);

        SidePool = compile.BrushSidePooling == BrushSidePooling.Off
            ? null
            : new BrushSidePool(checkReturns: compile.BrushSidePooling == BrushSidePooling.Checked);
    }

    // A fork of a context: see Fork and ForkForBlock.
    private BspBuildContext(BspBuildContext parent, bool rentArenaNow)
    {
        Compile = parent.Compile;
        Map = parent.Map;
        IsFork = true;

        // The same compliance as the arena it stands in for (WindingIsTiny
        // and BaseWindingForPlane read the policy off the arena): the pool
        // was made with the compile's.
        _forkArenas = parent._forkArenas;
        _forkWindings = rentArenaNow ? _forkArenas.Rent() : null;
        _forkDiagnostics = [];

        SidePool = parent.SidePool is null
            ? null
            : new BrushSidePool(checkReturns: Compile.BrushSidePooling == BrushSidePooling.Checked);

        BrushStart = parent.BrushStart;
        BrushEnd = parent.BrushEnd;
        parent._minPlaneNumbers.CopyTo(_minPlaneNumbers, 0);
        parent._maxPlaneNumbers.CopyTo(_maxPlaneNumbers, 0);
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
    /// <remarks>
    /// On a <see cref="Fork"/>, the fork's own arena instead: an arena is not
    /// thread safe, and a fork runs beside the context it came from. A fork
    /// whose arena has gone back to the pool (<see cref="ReleaseForkWindings"/>)
    /// has none, and asking for it throws rather than quietly handing out
    /// the compile's arena, which another thread may be using.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// This is a fork that has released its arena.
    /// </exception>
    public WindingArena Windings => _forkWindings ?? WindingsWithoutAnArena();

    /// <summary>
    /// Where this compile's forks get their arenas from, and give them back
    /// to: one pool per compile, made by the root context and shared by
    /// every fork of it.
    /// </summary>
    /// <remarks>
    /// See <see cref="WindingArenaPool"/> for why. The vbsp driver releases
    /// it with <see cref="ReleaseWindingArenaPool"/> when the compile ends.
    /// </remarks>
    internal WindingArenaPool ForkArenas => _forkArenas;

    /// <summary>The map's plane table: <c>g_MainMap-&gt;mapplanes</c>.</summary>
    public PlaneTable Planes => Map.Planes;

    /// <summary>Everything the compile has to say.</summary>
    /// <remarks>
    /// On a <see cref="Fork"/>, a list of the fork's own, which
    /// <see cref="Join"/> appends to its parent's at the point where the
    /// serial build would have said them.
    /// </remarks>
    public IList<CompileDiagnostic> Diagnostics =>
        _forkDiagnostics ?? (IList<CompileDiagnostic>)Compile.Diagnostics;

    /// <summary>Whether this context is a <see cref="Fork"/> of another.</summary>
    internal bool IsFork { get; }

    /// <summary>
    /// How to build trees in parallel, or null to build them serially: set by
    /// the vbsp driver, read by <see cref="Tree.BrushBspTree.BrushBsp"/>.
    /// </summary>
    /// <remarks>
    /// A fork never carries it: the forking recursion hands its own, one level
    /// deeper, down the tree.
    /// </remarks>
    internal Tree.BspTreeParallelism? TreeParallelism { get; set; }

    /// <summary>
    /// How many subtrees this context, and the forks joined into it, built in
    /// a fork. A measurement for the facts: zero means nothing forked, and a
    /// fact that proves equality over a build that never forked proves nothing.
    /// </summary>
    internal int ForkedSubtrees { get; private set; }

    /// <summary>
    /// How many world blocks were built in a fork and joined here by
    /// <see cref="JoinBlock"/>: zero when every world pass ran serially. A
    /// measurement for the facts, as <see cref="ForkedSubtrees"/> is.
    /// </summary>
    internal int ForkedBlocks { get; private set; }

    /// <summary>
    /// Where freed brushes' side arrays wait for the next brush, or null when
    /// the compile turned pooling off.
    /// </summary>
    /// <remarks>
    /// Owned by this context and by nothing else, so it lives exactly as long
    /// as one compile; <see cref="ReleaseBrushSidePool"/> empties it at the
    /// end. See <see cref="BrushSidePool"/> for which brushes feed it.
    /// </remarks>
    internal BrushSidePool? SidePool { get; }

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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sideCapacity"/> is negative.</exception>
    /// <remarks>
    /// The side array comes from <see cref="SidePool"/> when there is one,
    /// already cleared, so the brush starts exactly as a freshly allocated one
    /// would.
    /// </remarks>
    public BspBrush AllocBrush(int sideCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sideCapacity);

        BspBrush brush = SidePool is null
            ? new BspBrush(sideCapacity)
            : new BspBrush(SidePool.Rent(sideCapacity));
        brush.Id = AllocatedBrushes;
        brush.IdScope = IsFork ? this : null;
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
    /// <exception cref="InvalidOperationException">The brush was already freed.</exception>
    /// <remarks>
    /// <para>
    /// The brush object itself is the GC's; what has to be returned by hand is
    /// the arena storage its side windings hold, which is exactly what stock's
    /// <c>FreeWinding</c> loop does, and its side array, which goes back to
    /// <see cref="SidePool"/> for the next brush of the same capacity.
    /// </para>
    /// <para>
    /// The brush is left <see cref="BspBrush.IsFreed"/>: its sides are gone,
    /// and reading them, or freeing it again, throws. Stock's double free
    /// corrupts the heap; this one says so.
    /// </para>
    /// </remarks>
    public void FreeBrush(BspBrush brush)
    {
        ArgumentNullException.ThrowIfNull(brush);

        Span<BspBrushSide> sides = brush.Sides;
        for (int i = 0; i < sides.Length; i++)
        {
            Windings.Free(sides[i].Winding);
            sides[i].Winding = Winding.Null;
        }

        BspBrushSide[] storage = brush.DetachSides();
        SidePool?.Return(storage);

        ActiveBrushes--;
    }

    /// <summary>Drops every side array the build has pooled.</summary>
    /// <remarks>
    /// The vbsp driver calls it when the compile ends, in a <c>finally</c>, so
    /// a finished, failed or cancelled compile leaves nothing behind in a
    /// context a host might still be holding. The pool stays usable: a later
    /// <see cref="AllocBrush"/> simply allocates.
    /// </remarks>
    public void ReleaseBrushSidePool() => SidePool?.Clear();

    /// <summary>Drops every arena the compile's forks have given back.</summary>
    /// <remarks>
    /// The vbsp driver calls it when the compile ends, in the same
    /// <c>finally</c> as <see cref="ReleaseBrushSidePool"/>, so the forks'
    /// storage goes with the compile however it ended. A fork that returns
    /// its arena afterwards, still unwinding from a failure, has it dropped.
    /// </remarks>
    internal void ReleaseWindingArenaPool() => _forkArenas.Release();

    /// <summary>
    /// Gives a fork's arena back to the compile's pool, once nothing reads
    /// the windings in it: after <see cref="Join"/>, or after the fork's
    /// subtree failed and will never be joined.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tree build calls it in a <c>finally</c> around the fork's whole
    /// life, so a subtree that throws, or a build that is cancelled, returns
    /// its arena as a finished one does. By then every thread that used the
    /// arena has stopped: the parallel loop that ran the fork waits for all
    /// of its helpers before it returns or throws.
    /// </para>
    /// <para>
    /// Afterwards the fork has no arena, so a use of it by mistake throws
    /// rather than reading storage the next fork owns. Idempotent, and a
    /// no-op on a context that is not a fork.
    /// </para>
    /// </remarks>
    internal void ReleaseForkWindings()
    {
        if (!IsFork)
        {
            return;
        }

        _forkWindingsReleased = true;
        if (_forkWindings is { } arena)
        {
            _forkWindings = null;
            _forkArenas.Return(arena);
        }
    }

    // Windings when no fork arena is held: the compile's for the root; for a
    // block's fork that has not allocated yet, a newly rented one; for a
    // fork that has given its arena back, an error.
    private WindingArena WindingsWithoutAnArena()
    {
        if (!IsFork)
        {
            return Compile.Windings;
        }

        if (_forkWindingsReleased)
        {
            throw new InvalidOperationException(
                "this fork has given its winding arena back; a released fork builds nothing more");
        }

        return _forkWindings = _forkArenas.Rent();
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
    /// A context for building one subtree on another thread, beside this one.
    /// </summary>
    /// <returns>The fork.</returns>
    /// <remarks>
    /// <para>
    /// <b>What a subtree build shares, and what the fork gives it instead.</b>
    /// Splitting a node never creates a plane (only
    /// <see cref="BrushGeometry.BrushFromBounds"/> does, once per tree, before
    /// the recursion), so the plane table and the map are only read and are
    /// shared as they are. Everything the recursion writes is private to the
    /// fork: the winding arena, the diagnostics list, the brush side pool, the
    /// node and brush id sequences and the counters. None of it is visible
    /// to the context the fork came from until <see cref="Join"/>.
    /// </para>
    /// <para>
    /// The fork starts with no windings. The brushes it is to build from live
    /// in this context's arena, so <see cref="TakeWindings"/> moves them over
    /// first, on this thread, before either side of the fork starts.
    /// </para>
    /// </remarks>
    internal BspBuildContext Fork() => new(this, rentArenaNow: true);

    /// <summary>
    /// A context for building one whole block of the world on another thread:
    /// a <see cref="Fork"/> whose arena is rented only when it first
    /// allocates a winding, and which <see cref="JoinBlock"/> folds back.
    /// </summary>
    /// <returns>The fork.</returns>
    /// <remarks>
    /// The parallel world pass (<see cref="Tree.BlockGrid.BuildWorldPass"/>)
    /// makes every block's fork up front, on the calling thread, so that none
    /// of them reads this context while another thread is joining into it.
    /// Renting their arenas then too would hold one arena per block for the
    /// whole pass; renting on first use holds one per block being built or
    /// waiting to be joined.
    /// </remarks>
    internal BspBuildContext ForkForBlock() => new(this, rentArenaNow: false);

    /// <summary>
    /// Moves the windings of a brush list and of one more brush out of another
    /// context's arena into this one's, freeing them there.
    /// </summary>
    /// <param name="from">The context the brushes were built in.</param>
    /// <param name="brushes">A brush list, or null.</param>
    /// <param name="brush">One more brush (a node volume), or null.</param>
    /// <remarks>
    /// The points are copied exactly and each winding keeps its capacity, so
    /// the brushes are the same brushes; only which arena slot holds them
    /// changes, and no part of the output depends on a slot.
    /// </remarks>
    internal void TakeWindings(BspBuildContext from, BspBrush? brushes, BspBrush? brush)
    {
        ArgumentNullException.ThrowIfNull(from);

        for (BspBrush? b = brushes; b is not null; b = b.Next)
        {
            MoveWindings(b, from.Windings, freeSource: true);
        }

        if (brush is not null)
        {
            MoveWindings(brush, from.Windings, freeSource: true);
        }
    }

    /// <summary>
    /// Folds a finished fork, and the subtree it built, back into this
    /// context, as though this context had built the subtree itself.
    /// </summary>
    /// <param name="fork">The fork, from this context's <see cref="Fork"/>.</param>
    /// <param name="subtree">
    /// The subtree's root: a node this context allocated, whose descendants
    /// the fork allocated.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="fork"/> is not a fork.</exception>
    /// <remarks>
    /// <para>
    /// <b>Call it where the serial build would have started the subtree.</b>
    /// The serial build numbers nodes and brushes from one sequence each, in
    /// the order it allocates them: everything in the front subtree, then
    /// everything in the back. A fork numbers its own from zero, so its ids
    /// are offsets, and joining it after the front subtree has finished on
    /// this context rebases them onto this context's counts at that moment,
    /// which are exactly the counts the serial build had when it began the
    /// back subtree. Diagnostics are appended at the same point for the same
    /// reason.
    /// </para>
    /// <para>
    /// A brush id is either the fork's own, from its
    /// <see cref="AllocBrush"/>, or inherited through
    /// <see cref="BrushGeometry.CopyBrush"/> from a brush made before the
    /// fork. <see cref="BspBrush.IdScope"/> says which, and only the fork's
    /// own are rebased. The subtree root keeps its id, as its parent
    /// allocated it.
    /// </para>
    /// <para>
    /// The live windings, every node volume's and every leaf brush's, are
    /// copied into this context's arena. <see cref="Tree.BspNode.Side"/>
    /// also carries a winding handle, a copy of the splitting side's taken
    /// just before that side's brush was freed; it names freed storage in the
    /// serial build too, nothing reads it, and it is left as it is.
    /// </para>
    /// </remarks>
    internal void Join(BspBuildContext fork, Tree.BspNode subtree)
    {
        ArgumentNullException.ThrowIfNull(fork);
        ArgumentNullException.ThrowIfNull(subtree);
        if (!fork.IsFork)
        {
            throw new ArgumentException("only a fork can be joined", nameof(fork));
        }

        Rebase(fork, subtree, rebaseRoot: false, bringWindings: true);

        AllocatedNodes += fork.AllocatedNodes;
        AllocatedBrushes += fork.AllocatedBrushes;
        ActiveBrushes += fork.ActiveBrushes;
        Nodes += fork.Nodes;
        NonVisibleNodes += fork.NonVisibleNodes;
        PrunedNodes += fork.PrunedNodes;
        ForkedSubtrees += fork.ForkedSubtrees + 1;

        foreach (CompileDiagnostic diagnostic in fork._forkDiagnostics!)
        {
            Diagnostics.Add(diagnostic);
        }

        fork.ReleaseBrushSidePool();
    }

    /// <summary>
    /// Folds a block's fork, and the block tree it built, back into this
    /// context, as though this context had built the block itself.
    /// </summary>
    /// <param name="fork">The block's fork, from <see cref="ForkForBlock"/>.</param>
    /// <param name="head">
    /// The block's head node, which the fork allocated; null for a block
    /// whose brush list came out empty (the caller then allocates the block's
    /// solid leaf here, as the serial build does).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="fork"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="fork"/> is not a fork.</exception>
    /// <remarks>
    /// <para>
    /// <b>Call it where the serial build would have started the block</b>:
    /// after every earlier block has been joined, in block order. The serial
    /// pass numbers every node and brush of the world from one sequence each,
    /// block after block, so the fork's ids, numbered from zero, are rebased
    /// onto this context's counts at that moment, exactly as
    /// <see cref="Join"/> rebases a subtree's. Unlike a subtree's root, the
    /// block's head was allocated by the fork, so it is rebased too.
    /// </para>
    /// <para>
    /// <b>The live-node counters are replaced, not added to.</b> Each block's
    /// <see cref="Tree.BrushBspTree.BrushBsp"/> starts
    /// <see cref="Nodes"/> and <see cref="NonVisibleNodes"/> again from zero,
    /// so after a serial pass they hold the last built block's counts; a
    /// block whose list was empty never reaches <c>BrushBsp</c> and leaves
    /// them as they were. The bounding planes of the last block listed are
    /// likewise what the serial pass leaves behind, and are copied home.
    /// </para>
    /// <para>
    /// The block's windings must already be home
    /// (<see cref="TakeBlockWindings"/>), which may happen in any order and
    /// as soon as the block is built; only the numbering waits for its turn.
    /// </para>
    /// </remarks>
    internal void JoinBlock(BspBuildContext fork, Tree.BspNode? head)
    {
        ArgumentNullException.ThrowIfNull(fork);
        if (!fork.IsFork)
        {
            throw new ArgumentException("only a fork can be joined", nameof(fork));
        }

        if (head is not null)
        {
            Rebase(fork, head, rebaseRoot: true, bringWindings: false);
            Nodes = fork.Nodes;
            NonVisibleNodes = fork.NonVisibleNodes;
        }

        AllocatedNodes += fork.AllocatedNodes;
        AllocatedBrushes += fork.AllocatedBrushes;
        ActiveBrushes += fork.ActiveBrushes;
        PrunedNodes += fork.PrunedNodes;
        ForkedSubtrees += fork.ForkedSubtrees;
        ForkedBlocks++;
        fork._minPlaneNumbers.CopyTo(_minPlaneNumbers, 0);
        fork._maxPlaneNumbers.CopyTo(_maxPlaneNumbers, 0);

        foreach (CompileDiagnostic diagnostic in fork._forkDiagnostics!)
        {
            Diagnostics.Add(diagnostic);
        }

        fork.ReleaseBrushSidePool();
    }

    /// <summary>
    /// Copies the live windings of a block built in a fork into this
    /// context's arena, and gives the fork's arena back to the pool.
    /// </summary>
    /// <param name="fork">The block's fork.</param>
    /// <param name="head">The block tree's head node.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="fork"/> is not a fork.</exception>
    /// <remarks>
    /// <para>
    /// <b>Why this is separate from <see cref="JoinBlock"/>.</b> A block's
    /// ids can only be numbered once every earlier block is joined, but its
    /// windings can come home the moment it is built: no output depends on
    /// which arena slot a winding sits in. Holding each finished block's
    /// arena until its turn kept, on four threads, some thirty block arenas
    /// alive at once behind one slow early block, and most of the ninety on
    /// thirty-two; each is a chain of segments that is large-object memory.
    /// Bringing the windings home at once keeps only the blocks still being
    /// built holding an arena.
    /// </para>
    /// <para>
    /// The same walk as a join's (every node volume and every leaf brush),
    /// ids untouched. The caller serialises it with the joins, as both write
    /// this context's arena.
    /// </para>
    /// </remarks>
    internal void TakeBlockWindings(BspBuildContext fork, Tree.BspNode head)
    {
        ArgumentNullException.ThrowIfNull(fork);
        ArgumentNullException.ThrowIfNull(head);
        if (!fork.IsFork)
        {
            throw new ArgumentException("only a fork's windings can be taken home", nameof(fork));
        }

        Stack<Tree.BspNode> pending = new();
        pending.Push(head);
        while (pending.Count > 0)
        {
            Tree.BspNode node = pending.Pop();
            if (node.Volume is not null)
            {
                MoveWindings(node.Volume, fork.Windings, freeSource: false);
            }

            if (node.IsLeaf)
            {
                for (BspBrush? b = node.BrushList; b is not null; b = b.Next)
                {
                    MoveWindings(b, fork.Windings, freeSource: false);
                }

                continue;
            }

            pending.Push(node.Children[1]!);
            pending.Push(node.Children[0]!);
        }

        fork.ReleaseForkWindings();
    }

    // Renumbers a fork's nodes and brushes onto this context's current counts
    // and, when asked, copies their live windings home: every node volume and
    // every leaf brush. The subtree root keeps its id unless the fork
    // allocated it too.
    private void Rebase(BspBuildContext fork, Tree.BspNode subtree, bool rebaseRoot, bool bringWindings)
    {
        int nodeBase = AllocatedNodes;
        int brushBase = AllocatedBrushes;
        object? scope = IsFork ? this : null;

        Stack<Tree.BspNode> pending = new();
        pending.Push(subtree);
        while (pending.Count > 0)
        {
            Tree.BspNode node = pending.Pop();
            if (rebaseRoot || !ReferenceEquals(node, subtree))
            {
                node.Id += nodeBase;
            }

            if (node.Volume is not null)
            {
                Rehome(node.Volume);
            }

            if (node.IsLeaf)
            {
                for (BspBrush? b = node.BrushList; b is not null; b = b.Next)
                {
                    Rehome(b);
                }

                continue;
            }

            pending.Push(node.Children[1]!);
            pending.Push(node.Children[0]!);
        }

        void Rehome(BspBrush brush)
        {
            if (ReferenceEquals(brush.IdScope, fork))
            {
                brush.Id += brushBase;
                brush.IdScope = scope;
            }

            if (bringWindings)
            {
                MoveWindings(brush, fork.Windings, freeSource: false);
            }
        }
    }

    // Copies a brush's side windings from another arena into this context's.
    private void MoveWindings(BspBrush brush, WindingArena from, bool freeSource)
    {
        Span<BspBrushSide> sides = brush.Sides;
        for (int i = 0; i < sides.Length; i++)
        {
            Winding w = sides[i].Winding;
            if (w.IsNull)
            {
                continue;
            }

            sides[i].Winding = Windings.Adopt(from, w);
            if (freeSource)
            {
                from.Free(w);
            }
        }
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
