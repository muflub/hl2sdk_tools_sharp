namespace SourceSharp.MapTools.Vis;

/// <summary>
/// Where each open frame of a shared <c>-tighten</c> walk got its arguments
/// from, and how far through its candidates it is, so that the worker can
/// split off the SHALLOWEST frame with work left -- the biggest piece -- from
/// wherever it happens to be in the recursion.
/// </summary>
/// <remarks>
/// A frame's windings and mask live in its PARENT's slab (or in the portal
/// set, the flood, or the split-off task that started the walk), and a parent
/// does not touch that slab again until the frame returns. So while a frame is
/// open, its arguments can be found again from a few integers, and nothing has
/// to be copied until a split actually happens.
/// </remarks>
internal sealed class VisFrameLedger
{
    /// <summary>A source winding: a portal's own.</summary>
    internal const int SourcePortal = 0;

    /// <summary>A source winding: a slab's source buffer.</summary>
    internal const int SourceSlab = 1;

    /// <summary>A source winding: the split-off task's.</summary>
    internal const int SourceTask = 2;

    /// <summary>A pass winding: none (the head frame).</summary>
    internal const int PassEmpty = 0;

    /// <summary>A pass winding: a portal's own.</summary>
    internal const int PassPortal = 1;

    /// <summary>A pass winding: a slab's pass buffer.</summary>
    internal const int PassSlab = 2;

    /// <summary>A pass winding: a slab's clip buffer.</summary>
    internal const int PassClip = 3;

    /// <summary>A pass winding: the split-off task's.</summary>
    internal const int PassTask = 4;

    /// <summary>A mask: a portal's flood.</summary>
    internal const int MightFlood = 0;

    /// <summary>A mask: a slab's.</summary>
    internal const int MightSlab = 1;

    /// <summary>A mask: the split-off task's.</summary>
    internal const int MightTask = 2;

    private readonly int[] _sourceKind;
    private readonly int[] _sourceA;
    private readonly int[] _sourceB;
    private readonly int[] _passKind;
    private readonly int[] _passA;
    private readonly int[] _passB;
    private readonly int[] _mightKind;
    private readonly int[] _mightA;
    private readonly int[] _cluster;
    private readonly int[] _node;
    private readonly int[] _index;
    private readonly int[] _end;

    /// <summary>Creates a ledger for frames up to a depth.</summary>
    /// <param name="maxDepth">The deepest frame.</param>
    internal VisFrameLedger(int maxDepth)
    {
        int n = maxDepth + 2;
        _sourceKind = new int[n];
        _sourceA = new int[n];
        _sourceB = new int[n];
        _passKind = new int[n];
        _passA = new int[n];
        _passB = new int[n];
        _mightKind = new int[n];
        _mightA = new int[n];
        _cluster = new int[n];
        _node = new int[n];
        _index = new int[n];
        _end = new int[n];
    }

    /// <summary>The depth of the walk's first frame.</summary>
    internal int Root { get; set; }

    /// <summary>Records where a frame's arguments come from, before it is entered.</summary>
    internal void Arguments(
        int depth,
        int sourceKind,
        int sourceA,
        int sourceB,
        int passKind,
        int passA,
        int passB,
        int mightKind,
        int mightA)
    {
        _sourceKind[depth] = sourceKind;
        _sourceA[depth] = sourceA;
        _sourceB[depth] = sourceB;
        _passKind[depth] = passKind;
        _passA[depth] = passA;
        _passB[depth] = passB;
        _mightKind[depth] = mightKind;
        _mightA[depth] = mightA;
    }

    /// <summary>Records a frame being entered.</summary>
    internal void Enter(int depth, int cluster, int node, int end)
    {
        _cluster[depth] = cluster;
        _node[depth] = node;
        _end[depth] = end;
        _index[depth] = -1;
    }

    /// <summary>One past the last candidate a frame still owns.</summary>
    internal int End(int depth) => _end[depth];

    /// <summary>Records the candidate a frame is on.</summary>
    internal void At(int depth, int index) => _index[depth] = index;

    /// <summary>
    /// The shallowest open frame, from the root down to
    /// <paramref name="depth"/>, with at least one candidate after the one it
    /// is on; -1 if none.
    /// </summary>
    internal int Shallowest(int depth)
    {
        for (int d = Root; d <= depth; d++)
        {
            if (_end[d] - (_index[d] + 1) >= 1)
            {
                return d;
            }
        }

        return -1;
    }

    /// <summary>Gives a frame's second half away: it now ends at the split point.</summary>
    /// <returns>(from, to) of the half given away.</returns>
    internal (int From, int To) Halve(int depth)
    {
        int first = _index[depth] + 1;
        int end = _end[depth];
        int mid = first + ((end - first) >> 1);
        _end[depth] = mid;
        return (mid, end);
    }

    internal int Cluster(int depth) => _cluster[depth];

    internal int Node(int depth) => _node[depth];

    internal (int Kind, int A, int B) Source(int depth) => (_sourceKind[depth], _sourceA[depth], _sourceB[depth]);

    internal (int Kind, int A, int B) Pass(int depth) => (_passKind[depth], _passA[depth], _passB[depth]);

    internal (int Kind, int A) Might(int depth) => (_mightKind[depth], _mightA[depth]);
}
