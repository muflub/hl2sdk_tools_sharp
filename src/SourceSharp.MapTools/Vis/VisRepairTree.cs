//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// One portal's <c>-tighten</c> flow as a tree of its first few recursion
/// levels, with the reads it made of neighbours that had NOT finished, so that
/// an inexact run is repaired by re-walking only the subtrees that read
/// something wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>The nodes.</b> Node 0 is the portal's head frame.
/// Each candidate a tracked frame recurses into -- or tests and then skips --
/// is a child node, down to <see cref="Levels"/> levels below the head. A read
/// made in a tracked frame is charged to the child it computes the mask for (it
/// is that child's EDGE); a read made deeper is charged to the deepest tracked
/// node above it. A node is COMPLETE when every read charged to it and to its
/// descendants was exact: its subtree was then walked with the exact masks, so
/// every bit reachable through it is already set, and a later run may skip it
/// outright. A node with an inexact read of its own is DIRTY: its mask (if the
/// edge) or its untracked frames were wrong, so it is re-walked from scratch
/// and nothing recorded below it counts. Everything else on the way to a dirty
/// node is re-walked frame by frame, skipping the complete children.
/// </para>
/// <para>
/// <b>Threads.</b> A portal's walk may be split between workers
/// (<see cref="VisFrameLedger"/>), so every node operation takes the tree's
/// lock. They happen only in tracked frames -- the first few levels -- which
/// are a vanishing share of a flow's frames. The reads themselves are recorded
/// per worker (<see cref="VisSpeculativeReads"/>) and handed over with
/// <see cref="Absorb"/>.
/// </para>
/// </remarks>
internal sealed class VisRepairTree
{
    /// <summary>How many recursion levels below the head frame are tracked, by default.</summary>
    internal const int DefaultLevels = 4;

    private const int NoNode = -1;

    private readonly Action<ulong[]> _return;
    private readonly List<(int Node, int Portal, ulong[] Missed)> _records = [];
    private readonly Dictionary<long, int> _recordIndex = [];
    private readonly List<(int Node, int Portal, ulong[] Missed)> _incoming = [];
    private readonly object _lock = new();

    private int[] _parent = new int[64];
    private int[] _block = new int[64];
    private int[] _blockSize = new int[64];
    private int[] _slots = new int[256];
    private int _slotCount;
    private bool[] _dirty = new bool[64];
    private int[] _dirtyBelow = new int[64];
    private bool[] _walked = new bool[64];
    private int _count;

    /// <summary>Creates an empty tree.</summary>
    /// <param name="giveBack">Takes a record vector back; it need not be cleared.</param>
    /// <param name="levels">How many recursion levels below the head frame are tracked.</param>
    internal VisRepairTree(Action<ulong[]> giveBack, int levels)
    {
        Levels = levels;
        _return = giveBack;
        Clear();
    }

    /// <summary>How many recursion levels below the head frame are tracked.</summary>
    internal int Levels { get; }

    /// <summary>The head frame's node.</summary>
    internal static int Root => 0;

    /// <summary>How many (node, candidate) records the runs since the last judgement made.</summary>
    internal int RecordCount => _records.Count;

    /// <summary>Whether those runs read any neighbour that had not finished.</summary>
    internal bool Speculated => _records.Count > 0;

    /// <summary>Forgets everything: one head node, no records.</summary>
    internal void Clear()
    {
        foreach ((_, _, ulong[] missed) in _records)
        {
            _return(missed);
        }

        _records.Clear();
        _recordIndex.Clear();
        _slotCount = 0;
        _count = 0;
        NewNode(NoNode);
    }

    /// <summary>
    /// The child of <paramref name="node"/> for the candidate at
    /// <paramref name="index"/> of its frame's cluster list, created if the
    /// tree has not seen it.
    /// </summary>
    /// <param name="node">A tracked frame's node.</param>
    /// <param name="index">The candidate's position in the frame's cluster list.</param>
    /// <param name="count">How many candidates that list has.</param>
    /// <param name="shared">Whether another worker may be walking this portal too.</param>
    /// <returns>The child node.</returns>
    /// <remarks>
    /// A node's children are a block of slots, one per candidate of its
    /// cluster, indexed by position: no search and no hash. (A sibling list
    /// searched per candidate, then a hash, were measured at 4-6 % of all
    /// running samples on 2fort at 32 threads; p5-vis-findings.md.) Taken
    /// under the lock only when the walk has been split; before that the
    /// tree has one user.
    /// </remarks>
    internal int Child(int node, int index, int count, bool shared = false)
    {
        if (!shared)
        {
            return ChildUnlocked(node, index, count);
        }

        lock (_lock)
        {
            return ChildUnlocked(node, index, count);
        }
    }

    /// <summary>
    /// <see cref="Child"/>, <see cref="IsComplete"/> and, unless the child is
    /// complete, <see cref="Walk"/>, in one step: what the flow does for every
    /// candidate of a tracked frame.
    /// </summary>
    /// <param name="node">A tracked frame's node.</param>
    /// <param name="index">The candidate's position in the frame's cluster list.</param>
    /// <param name="count">How many candidates that list has.</param>
    /// <param name="shared">Whether another worker may be walking this portal too.</param>
    /// <param name="child">The candidate's node.</param>
    /// <returns>
    /// False when an earlier run proved the child's subtree complete, so the
    /// candidate is skipped and nothing was marked; true when the child is
    /// now marked as being walked.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Why one step.</b> A split walk takes the tree's lock for each of
    /// the three calls, and on 2fort at sixteen threads those were 560,000
    /// acquisitions and the tree lock's 8,000 waits -- more than the
    /// schedule's gate saw. Taking it once does the same three things.
    /// </para>
    /// <para>
    /// <b>Why it cannot change the answer.</b> The three operations run in
    /// the same order on the same node; the only difference is that no other
    /// worker can act on the tree between them. Every execution of the
    /// combined step is therefore one the three separate steps could already
    /// have produced (the one where the other workers happened to wait), and
    /// <see cref="VisTightening"/> accepts a run's result whatever the
    /// interleaving of its pieces was.
    /// </para>
    /// </remarks>
    internal bool Enter(int node, int index, int count, bool shared, out int child)
    {
        if (!shared)
        {
            return EnterUnlocked(node, index, count, out child);
        }

        lock (_lock)
        {
            return EnterUnlocked(node, index, count, out child);
        }
    }

    private bool EnterUnlocked(int node, int index, int count, out int child)
    {
        child = ChildUnlocked(node, index, count);
        if (_walked[child] && !_dirty[child] && _dirtyBelow[child] == 0)
        {
            return false;
        }

        WalkUnlocked(child);
        return true;
    }

    private int ChildUnlocked(int node, int index, int count)
    {
        int block = _block[node];
        if (block < 0)
        {
            block = _slotCount;
            if (block + count > _slots.Length)
            {
                Array.Resize(ref _slots, Math.Max(_slots.Length * 2, block + count));
            }

            _slots.AsSpan(block, count).Fill(NoNode);
            _slotCount += count;
            _block[node] = block;
            _blockSize[node] = count;
        }

        int child = _slots[block + index];
        if (child == NoNode)
        {
            child = NewNode(node);
            _slots[block + index] = child;
        }

        return child;
    }

    /// <summary>
    /// Whether a node's whole subtree was walked with exact masks, so a run
    /// may skip it.
    /// </summary>
    /// <param name="node">A node.</param>
    /// <returns>True when complete.</returns>
    /// <remarks>
    /// A node created by the current walk has clear flags but has not been
    /// walked; <see cref="Walk"/> marks walking.
    /// </remarks>
    /// <param name="shared">Whether another worker may be walking this portal too.</param>
    internal bool IsComplete(int node, bool shared = false)
    {
        if (!shared)
        {
            return _walked[node] && !_dirty[node] && _dirtyBelow[node] == 0;
        }

        lock (_lock)
        {
            return _walked[node] && !_dirty[node] && _dirtyBelow[node] == 0;
        }
    }

    /// <summary>
    /// Prepares a node to be walked: a dirty node loses its subtree, any other
    /// only its "something below is dirty" count, which the next judgement
    /// recomputes from this walk's records.
    /// </summary>
    /// <param name="node">A node about to be walked.</param>
    /// <param name="shared">Whether another worker may be walking this portal too.</param>
    internal void Walk(int node, bool shared = false)
    {
        if (!shared)
        {
            WalkUnlocked(node);
            return;
        }

        lock (_lock)
        {
            WalkUnlocked(node);
        }
    }

    private void WalkUnlocked(int node)
    {
        if (_dirty[node])
        {
            Cut(node);
            _dirty[node] = false;
        }

        _dirtyBelow[node] = 0;
        _walked[node] = true;
    }

    /// <summary>
    /// Drops a node's descendants: the run has proved it needs none of them
    /// (the candidate was skipped by the early-out).
    /// </summary>
    /// <param name="node">A node.</param>
    /// <param name="shared">Whether another worker may be walking this portal too.</param>
    internal void Prune(int node, bool shared = false)
    {
        if (_block[node] < 0)
        {
            return;
        }

        if (!shared)
        {
            Cut(node);
            return;
        }

        lock (_lock)
        {
            Cut(node);
        }
    }

    /// <summary>Forgets a node's descendants, hash entries included. Under the lock.</summary>
    private void Cut(int node)
    {
        int block = _block[node];
        if (block < 0)
        {
            return;
        }

        for (int j = 0; j < _blockSize[node]; j++)
        {
            int c = _slots[block + j];
            if (c != NoNode)
            {
                Cut(c);
                _slots[block + j] = NoNode;
            }
        }
    }

    /// <summary>
    /// Takes one worker's reads, merging records for the same (node,
    /// candidate): what either could have missed, the pair could have.
    /// </summary>
    /// <param name="reads">The worker's record; left empty.</param>
    internal void Absorb(VisSpeculativeReads reads)
    {
        lock (_lock)
        {
            _incoming.Clear();
            reads.MoveTo(_incoming);
            foreach ((int node, int portal, ulong[] missed) in _incoming)
            {
                long key = ((long)node << 32) | (uint)portal;
                if (_recordIndex.TryGetValue(key, out int slot))
                {
                    ulong[] held = _records[slot].Missed;
                    for (int j = 0; j < held.Length; j++)
                    {
                        held[j] |= missed[j];
                    }

                    _return(missed);
                }
                else
                {
                    _recordIndex[key] = _records.Count;
                    _records.Add((node, portal, missed));
                }
            }

            _incoming.Clear();
        }
    }

    /// <summary>Adds every candidate the runs read unfinished to a list, once each.</summary>
    /// <param name="into">Receives each portal once.</param>
    internal void CollectPending(List<int> into)
    {
        int start = into.Count;
        foreach ((_, int portal, _) in _records)
        {
            bool seen = false;
            for (int i = start; i < into.Count; i++)
            {
                if (into[i] == portal)
                {
                    seen = true;
                    break;
                }
            }

            if (!seen)
            {
                into.Add(portal);
            }
        }
    }

    /// <summary>
    /// Judges the runs once every candidate they read unfinished has
    /// finished: marks each node with an inexact read dirty, and every node
    /// above it as having something dirty below. Forgets the records.
    /// </summary>
    /// <param name="missedAnything">
    /// Whether a record for a finished portal meets that portal's final <c>portalvis</c>.
    /// </param>
    /// <returns>True when every read was exact.</returns>
    internal bool Validate(Func<int, ulong[], bool> missedAnything)
    {
        bool exact = true;
        foreach ((int node, int portal, ulong[] missed) in _records)
        {
            if (missedAnything(portal, missed))
            {
                exact = false;
                if (!_dirty[node])
                {
                    _dirty[node] = true;
                    for (int a = _parent[node]; a != NoNode; a = _parent[a])
                    {
                        _dirtyBelow[a]++;
                    }
                }
            }

            _return(missed);
        }

        _records.Clear();
        _recordIndex.Clear();
        return exact;
    }

    /// <summary>
    /// Whether a run's reads of a candidate missed anything, now that the
    /// candidate's <c>portalvis</c> is final.
    /// </summary>
    /// <param name="missed">What the reads could have missed.</param>
    /// <param name="final">The candidate's finished <c>portalvis</c>.</param>
    /// <returns>True when some read produced a smaller intersection than the exact one.</returns>
    internal static bool MissedAnything(ReadOnlySpan<ulong> missed, ReadOnlySpan<ulong> final)
    {
        // Four words at a time where the CPU has 256-bit vectors: this runs
        // under VisTightening's gate, once per record of every judged run,
        // and a yes-or-no over the whole vector does not depend on the order
        // or grouping of the words it looks at.
        int j = 0;
        if (Vector256.IsHardwareAccelerated && final.Length >= missed.Length)
        {
            ref ulong m = ref MemoryMarshal.GetReference(missed);
            ref ulong f = ref MemoryMarshal.GetReference(final);
            for (; j + 4 <= missed.Length; j += 4)
            {
                if ((Vector256.LoadUnsafe(ref m, (nuint)j) & Vector256.LoadUnsafe(ref f, (nuint)j)) != Vector256<ulong>.Zero)
                {
                    return true;
                }
            }
        }

        for (; j < missed.Length; j++)
        {
            if ((missed[j] & final[j]) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private int NewNode(int parent)
    {
        if (_count == _parent.Length)
        {
            int size = _count * 2;
            Array.Resize(ref _parent, size);
            Array.Resize(ref _block, size);
            Array.Resize(ref _blockSize, size);
            Array.Resize(ref _dirty, size);
            Array.Resize(ref _dirtyBelow, size);
            Array.Resize(ref _walked, size);
        }

        int node = _count++;
        _parent[node] = parent;
        _block[node] = NoNode;
        _blockSize[node] = 0;
        _dirty[node] = false;
        _dirtyBelow[node] = 0;
        _walked[node] = false;
        return node;
    }
}
