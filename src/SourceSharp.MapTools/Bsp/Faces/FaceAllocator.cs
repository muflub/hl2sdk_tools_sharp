//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Bsp.Faces;

/// <summary>
/// <c>AllocFace</c>, <c>NewFaceFromFace</c>, <c>FreeFace</c> and
/// <c>FreeFaceList</c>, with the
/// <c>c_faces</c> balance they keep.
/// </summary>
/// <remarks>
/// <para>
/// The counter is not decoration. Stock increments it on every allocation and
/// decrements it on every free, so a positive value at the end of a model is
/// the number of faces still alive — and the face lists are meant to stay alive
/// until the tree is freed, so this is a leak detector only across a whole
/// compile, not within one.
/// </para>
/// <para>
/// <b>The winding is owned by the face.</b> <c>FreeFace</c> frees
/// <c>f-&gt;w</c>, so a caller that handed the same winding to two faces gets a
/// double free. In the arena that is caught rather than silently reused, which
/// is the entire reason the arena checks.
/// </para>
/// </remarks>
public sealed class FaceAllocator
{
    private readonly WindingArena _arena;
    private int _nextId;

    /// <summary>Creates an allocator over one compile's winding arena.</summary>
    /// <param name="arena">The arena faces take their windings from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is null.</exception>
    public FaceAllocator(WindingArena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);
        _arena = arena;
    }

    /// <summary><c>c_faces</c>: allocations minus frees.</summary>
    public int LiveFaces { get; private set; }

    /// <summary>How many faces have ever been allocated, <c>s_FaceId</c>.</summary>
    public int TotalAllocations => _nextId;

    /// <summary>Allocates a zeroed face (<c>AllocFace</c>).</summary>
    /// <returns>The new face.</returns>
    public Face Alloc()
    {
        Face face = new(_nextId);
        _nextId++;
        LiveFaces++;
        return face;
    }

    /// <summary>
    /// Allocates a copy of a face with no winding and no merge or split links
    /// (<c>NewFaceFromFace</c>).
    /// </summary>
    /// <param name="face">The face to copy.</param>
    /// <returns>The copy.</returns>
    /// <remarks>
    /// The copy carries the SOURCE's id, because <c>*newf = *f</c> overwrites
    /// the one <c>AllocFace</c> just stamped. See <see cref="Face.Id"/>.
    /// </remarks>
    public Face NewFaceFromFace(Face face)
    {
        ArgumentNullException.ThrowIfNull(face);

        Face copy = Alloc();
        copy.CopyFrom(face);
        copy.Merged = null;
        copy.Split[0] = null;
        copy.Split[1] = null;
        copy.Winding = Winding.Null;
        return copy;
    }

    /// <summary>
    /// Allocates a copy of a face together with a copy of its winding
    /// (<c>CopyFace</c>).
    /// </summary>
    /// <param name="face">The face to copy.</param>
    /// <returns>The copy.</returns>
    public Face CopyFace(Face face)
    {
        ArgumentNullException.ThrowIfNull(face);

        Face copy = NewFaceFromFace(face);
        copy.Winding = _arena.Copy(face.Winding);
        return copy;
    }

    /// <summary>Frees a face and its winding (<c>FreeFace</c>).</summary>
    /// <param name="face">The face to free.</param>
    public void Free(Face face)
    {
        ArgumentNullException.ThrowIfNull(face);

        if (!face.Winding.IsNull)
        {
            _arena.Free(face.Winding);
            face.Winding = Winding.Null;
        }

        LiveFaces--;
    }

    /// <summary>Frees a whole <c>next</c> chain (<c>FreeFaceList</c>).</summary>
    /// <param name="faces">The head of the chain, or null.</param>
    public void FreeList(Face? faces)
    {
        while (faces is not null)
        {
            Face? next = faces.Next;
            Free(faces);
            faces = next;
        }
    }
}
