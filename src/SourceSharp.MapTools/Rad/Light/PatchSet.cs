//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// Every patch in a map, plus the four index tables that thread them:
/// <c>g_Patches</c>, <c>g_FacePatches</c>, <c>faceParents</c> and
/// <c>clusterChildren</c>.
/// </summary>
/// <remarks>
/// <para>
/// The four tables are heads of singly-linked lists whose <c>next</c> pointers
/// live in the patches themselves, so they are inseparable from the patch array
/// and belong on the same object. Stock declares all four as file-scope
/// <c>CUtlVector</c>s and clears none of them, which is the other reason vrad
/// cannot light two maps in one process.
/// </para>
/// <para>
/// <b>Appending during iteration is the normal case.</b>
/// <see cref="PatchSubdivider"/> walks the patch array while
/// <see cref="Add"/> grows it, and holds INDICES rather than references
/// precisely so a reallocation is harmless. <see cref="At"/> returns a
/// <c>ref</c>, which must not be held across an <see cref="Add"/>; every call
/// site below re-fetches.
/// </para>
/// <para>
/// <b>Stored in fixed-size segments, not one growing array.</b> How many
/// patches a map ends up with is decided by the subdivision recursion itself,
/// so there is no count to size one array from before it runs. Doubling a
/// single array made every earlier array garbage on the large-object heap
/// (on a map the size of 2fort, some 150 MB of patches were allocated and
/// thrown away to keep the last array of about the same size) and left up to
/// half of the kept array unused for the rest of the compile. Segments are
/// never copied once full, so the storage allocated is the storage kept, give
/// or take the unfilled tail of the last segment. The first segment still
/// starts small and doubles up to the full segment length, so a small map (or
/// a unit test) does not pay for a whole segment.
/// </para>
/// </remarks>
public sealed class PatchSet
{
    /// <summary>log2 of the patches in a full segment.</summary>
    internal const int SegmentShift = 10;

    /// <summary>The patches in a full segment.</summary>
    internal const int SegmentLength = 1 << SegmentShift;

    private const int SegmentMask = SegmentLength - 1;

    private Patch[][] _segments;
    private int _count;

    /// <summary>Creates an empty set sized for a map.</summary>
    /// <param name="faceCount">How many faces the map has.</param>
    /// <param name="clusterCount">How many vis clusters it has.</param>
    /// <param name="arena">Where patch windings are allocated.</param>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is null.</exception>
    public PatchSet(int faceCount, int clusterCount, WindingArena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);

        // The first segment is sized from the faces (one root patch each,
        // plus room for their first splits), capped at a full segment.
        _segments = [new Patch[Math.Min(Math.Max(faceCount * 2, 64), SegmentLength)]];
        Arena = arena;
        Centroids = new FaceCentroids(faceCount);

        FacePatches = new int[faceCount];
        FaceParents = new int[faceCount];
        FaceOffsets = new Vec3[faceCount];
        FaceEntities = new int[faceCount];
        ClusterChildren = new int[Math.Max(clusterCount, 0)];

        Array.Fill(FacePatches, Patch.Invalid);
        Array.Fill(FaceParents, Patch.Invalid);
        Array.Fill(ClusterChildren, Patch.Invalid);
    }

    /// <summary>How many patches exist.</summary>
    public int Count => _count;

    /// <summary>The arena holding every patch winding.</summary>
    public WindingArena Arena { get; }

    /// <summary><c>face_centroids</c>, filled as patches are made.</summary>
    public FaceCentroids Centroids { get; }

    /// <summary>
    /// <c>g_FacePatches</c>: the head of each face's patch list, children
    /// first after subdivision.
    /// </summary>
    public int[] FacePatches { get; }

    /// <summary>
    /// <c>faceParents</c>: the head of each face's ROOT patch list, walked
    /// through <see cref="Patch.NextParent"/>.
    /// </summary>
    public int[] FaceParents { get; }

    /// <summary>
    /// <c>clusterChildren</c>: the head of each cluster's leaf patch list.
    /// </summary>
    public int[] ClusterChildren { get; }

    /// <summary>
    /// <c>face_offset</c>: the origin of the brush model each face belongs to.
    /// </summary>
    public Vec3[] FaceOffsets { get; }

    /// <summary>
    /// <c>face_entity</c>, as an index into the parsed entity list.
    /// </summary>
    public int[] FaceEntities { get; }

    /// <summary>
    /// <c>totalarea</c>: the summed area of every root patch, in square world
    /// units.
    /// </summary>
    /// <remarks>
    /// vrad's own progress line prints this as square feet and square inches
    /// Which is the most sensitive single number the
    /// patch pass produces: it moves if any face's winding differs by a sliver.
    /// <b>A <c>float</c>, not a double</b> -- declares it
    /// <c>float</c>, and on a map with thousands of faces the accumulator's
    /// rounding is part of the printed answer.
    /// </remarks>
    public float TotalArea { get; internal set; }

    /// <summary>
    /// <c>num_degenerate_faces</c>: faces whose winding came out with no area.
    /// </summary>
    public int DegenerateFaces { get; internal set; }

    /// <summary>One patch, by index, for reading and writing.</summary>
    /// <param name="index">The patch.</param>
    /// <returns>A reference into the backing array.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such patch.</exception>
    /// <remarks>
    /// The reference is invalidated by the next <see cref="Add"/>. That is the
    /// same contract as <c>CUtlVector::Element</c>, which stock violates in
    /// <c>SubdividePatch</c> and then repairs by re-fetching
    /// (with a comment).
    /// </remarks>
    public ref Patch At(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
        return ref _segments[index >> SegmentShift][index & SegmentMask];
    }

    /// <summary>A copy of every patch, in creation order.</summary>
    /// <returns>A new array of exactly <see cref="Count"/> patches.</returns>
    /// <remarks>
    /// For inspection (tests and diagnostics). The storage is segmented, so
    /// there is no single span over it; the compile itself walks the patches
    /// by index through <see cref="At"/>.
    /// </remarks>
    public Patch[] ToArray()
    {
        Patch[] result = new Patch[_count];
        for (int copied = 0, segment = 0; copied < _count; segment++)
        {
            int n = Math.Min(_segments[segment].Length, _count - copied);
            _segments[segment].AsSpan(0, n).CopyTo(result.AsSpan(copied));
            copied += n;
        }

        return result;
    }

    /// <summary>How many patches the allocated segments can hold.</summary>
    internal int Capacity
    {
        get
        {
            int capacity = 0;
            foreach (Patch[]? segment in _segments)
            {
                capacity += segment?.Length ?? 0;
            }

            return capacity;
        }
    }

    /// <summary>
    /// Appends a patch: <c>g_Patches.AddToTail()</c>.
    /// </summary>
    /// <param name="patch">The patch to append.</param>
    /// <returns>Its index.</returns>
    public int Add(Patch patch)
    {
        int segment = _count >> SegmentShift;
        int slot = _count & SegmentMask;
        if (segment == 0)
        {
            // The first segment doubles up to a full segment's length; a ref
            // into it is invalidated by that, as the remarks on At say.
            if (slot == _segments[0].Length)
            {
                Array.Resize(ref _segments[0], Math.Min(_segments[0].Length * 2, SegmentLength));
            }
        }
        else
        {
            if (segment == _segments.Length)
            {
                Array.Resize(ref _segments, _segments.Length * 2);
            }

            _segments[segment] ??= new Patch[SegmentLength];
        }

        _segments[segment][slot] = patch;
        return _count++;
    }
}
