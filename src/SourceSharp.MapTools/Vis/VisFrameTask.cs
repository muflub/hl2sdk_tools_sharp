//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// "A worker is idle and would take a split-off frame": read by every flow
/// on every candidate, written only when it changes.
/// </summary>
/// <remarks>
/// On a cache line of its own. It sat among the schedule's counters, which
/// the gate's holder writes on every run, so every one of those writes
/// invalidated the line every worker reads once per candidate.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal sealed class VisHunger
{
    [FieldOffset(64)]
    private int _value;

    /// <summary>Whether a worker is idle.</summary>
    internal bool Hungry => Volatile.Read(ref _value) != 0;

    /// <summary>Records whether a worker is idle; writes only a change.</summary>
    /// <param name="hungry">The new state.</param>
    internal void Set(bool hungry)
    {
        int value = hungry ? 1 : 0;
        if (Volatile.Read(ref _value) != value)
        {
            Volatile.Write(ref _value, value);
        }
    }
}

/// <summary>
/// Where a shared <c>-tighten</c> run hands off part of a frame.
/// </summary>
internal interface IVisFlowSplitter
{
    /// <summary>Whether a worker is idle and would take a split-off frame.</summary>
    VisHunger Hunger { get; }

    /// <summary>
    /// Hands the candidates <paramref name="from"/> to <paramref name="to"/>
    /// of one frame to another worker. Everything passed is copied.
    /// </summary>
    /// <param name="basePortal">The portal being flowed.</param>
    /// <param name="cluster">The frame's cluster.</param>
    /// <param name="depth">The frame's depth.</param>
    /// <param name="source">The frame's source winding.</param>
    /// <param name="pass">The frame's pass winding.</param>
    /// <param name="mightSee">The frame's mask.</param>
    /// <param name="node">The frame's repair-tree node, or -1.</param>
    /// <param name="from">The first candidate index handed off.</param>
    /// <param name="to">One past the last.</param>
    void Split(
        int basePortal,
        int cluster,
        int depth,
        ReadOnlySpan<Vec3> source,
        ReadOnlySpan<Vec3> pass,
        ReadOnlySpan<ulong> mightSee,
        int node,
        int from,
        int to);
}

/// <summary>
/// A copy of part of one flow frame, split off by one worker for another:
/// the windings and mask the frame was entered with and a range of its
/// cluster's candidates. Pooled; one per split in flight.
/// </summary>
internal sealed class VisFrameTask
{
    private readonly Vec3[] _source = new Vec3[VisClip.MaxPointsOnWinding];
    private readonly Vec3[] _pass = new Vec3[VisClip.MaxPointsOnWinding];
    private readonly ulong[] _mightSee;
    private int _sourceCount;
    private int _passCount;

    /// <summary>Creates an empty task for vectors of a given length.</summary>
    /// <param name="words">The length of one portal vector, in words.</param>
    internal VisFrameTask(int words) => _mightSee = new ulong[words];

    /// <summary>The rank of the portal being flowed.</summary>
    internal int Rank { get; private set; }

    /// <summary>The portal being flowed.</summary>
    internal int BasePortal { get; private set; }

    /// <summary>The frame's cluster.</summary>
    internal int Cluster { get; private set; }

    /// <summary>The frame's depth.</summary>
    internal int Depth { get; private set; }

    /// <summary>The frame's repair-tree node, or -1.</summary>
    internal int Node { get; private set; }

    /// <summary>The first candidate index.</summary>
    internal int From { get; private set; }

    /// <summary>One past the last candidate index.</summary>
    internal int To { get; private set; }

    /// <summary>The frame's source winding.</summary>
    internal ReadOnlySpan<Vec3> Source => _source.AsSpan(0, _sourceCount);

    /// <summary>The frame's pass winding (empty at the head frame).</summary>
    internal ReadOnlySpan<Vec3> Pass => _pass.AsSpan(0, _passCount);

    /// <summary>The frame's mask.</summary>
    internal ReadOnlySpan<ulong> MightSee => _mightSee;

    /// <summary>Copies a frame in.</summary>
    /// <param name="rank">The rank of the portal being flowed.</param>
    /// <param name="basePortal">The portal being flowed.</param>
    /// <param name="cluster">The frame's cluster.</param>
    /// <param name="depth">The frame's depth.</param>
    /// <param name="source">The frame's source winding.</param>
    /// <param name="pass">The frame's pass winding.</param>
    /// <param name="mightSee">The frame's mask.</param>
    /// <param name="node">The frame's repair-tree node, or -1.</param>
    /// <param name="from">The first candidate index.</param>
    /// <param name="to">One past the last.</param>
    internal void Set(
        int rank,
        int basePortal,
        int cluster,
        int depth,
        ReadOnlySpan<Vec3> source,
        ReadOnlySpan<Vec3> pass,
        ReadOnlySpan<ulong> mightSee,
        int node,
        int from,
        int to)
    {
        Rank = rank;
        BasePortal = basePortal;
        Cluster = cluster;
        Depth = depth;
        Node = node;
        From = from;
        To = to;
        source.CopyTo(_source);
        _sourceCount = source.Length;
        pass.CopyTo(_pass);
        _passCount = pass.Length;
        mightSee.CopyTo(_mightSee);
    }
}
