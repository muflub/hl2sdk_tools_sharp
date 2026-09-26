//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp.Faces;

/// <summary>
/// The three primitive tables a face can point into: <c>g_primitives</c>,
/// <c>g_primindices</c> and <c>g_primverts</c>
/// (filled).
/// </summary>
/// <remarks>
/// <para>
/// A primitive is what a face falls back to when its polygon cannot be drawn
/// as a fan of its own vertices. Two things in this stage produce one: a
/// t-junction-fixed world face with no clean starting vertex, which gets a
/// triangle LIST that sews its cracks (<c>FixFaceEdges</c>,
///), and a water face with a <c>$subdivsize</c>, which
/// gets a triangle STRIP over a grid of sub-windings
/// (<c>SubdivideFaceBySubdivSize</c>).
/// </para>
/// <para>
/// <b>The two kinds index different things and that is not a mistake.</b> A
/// crack-sewing list's indices are into the FACE's own vertex list, and its
/// <c>vertCount</c> is zero; a water strip's indices are into
/// <see cref="Vertices"/>, its own private vertex array. The lump has one
/// index array for both.
/// </para>
/// </remarks>
public sealed class PrimitiveTable
{
    /// <summary><c>MAX_MAP_PRIMITIVES</c>.</summary>
    public const int MaxPrimitives = 32768;

    /// <summary><c>MAX_MAP_PRIMVERTS</c>.</summary>
    public const int MaxPrimVerts = 65536;

    /// <summary><c>MAX_MAP_PRIMINDICES</c>.</summary>
    public const int MaxPrimIndices = 65536;

    private readonly List<DPrimitive> _primitives = [];
    private readonly List<ushort> _indices = [];
    private readonly List<Vec3> _vertices = [];

    /// <summary><c>g_primitives</c>, in emission order.</summary>
    public IReadOnlyList<DPrimitive> Primitives => _primitives;

    /// <summary><c>g_primindices</c>.</summary>
    public IReadOnlyList<ushort> Indices => _indices;

    /// <summary><c>g_primverts</c>, as positions; the lump's struct is one <c>Vector</c>.</summary>
    public IReadOnlyList<Vec3> Vertices => _vertices;

    /// <summary>
    /// Appends a triangle-list primitive over a face's own vertices
    /// (<c>FixFaceEdges</c>).
    /// </summary>
    /// <param name="indices">Triangle indices into the face's vertex list.</param>
    /// <returns>The index of the new primitive.</returns>
    /// <exception cref="InvalidOperationException">Either table overflowed.</exception>
    /// <remarks>
    /// Stock bumps <c>g_numprimitives</c> BEFORE the overflow check here and
    /// after it in the water path, so the two report at slightly different
    /// points. Both limits are checked before anything is stored here, which
    /// changes only which message a map that is over the limit gets.
    /// </remarks>
    public int AddTriangleList(IReadOnlyList<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);

        if (_primitives.Count + 1 > MaxPrimitives || _indices.Count + indices.Count > MaxPrimIndices)
        {
            throw new InvalidOperationException(
                $"Too many t-junctions to fix up! ({_primitives.Count + 1} prims, max {MaxPrimitives} "
                + $":: {_indices.Count + indices.Count} indices, max {MaxPrimIndices})");
        }

        DPrimitive primitive = new()
        {
            Type = (byte)PrimitiveType.TriList,
            FirstIndex = (ushort)_indices.Count,
            FirstVert = (ushort)_vertices.Count,
            IndexCount = (ushort)indices.Count,
            VertCount = 0,
        };

        foreach (int index in indices)
        {
            _indices.Add((ushort)index);
        }

        _primitives.Add(primitive);
        return _primitives.Count - 1;
    }

    /// <summary>
    /// Starts a primitive whose vertices and indices are appended piecemeal, as
    /// the water subdivider does.
    /// </summary>
    /// <param name="type">Triangle list or strip.</param>
    /// <returns>A builder over this table.</returns>
    public PrimitiveBuilder Begin(PrimitiveType type) => new(this, type);

    internal int VertexCount => _vertices.Count;

    internal int IndexCount => _indices.Count;

    internal int PrimitiveCount => _primitives.Count;

    internal Vec3 VertexAt(int index) => _vertices[index];

    internal void SetVertexAt(int index, Vec3 value)
    {
        while (_vertices.Count <= index)
        {
            _vertices.Add(default);
        }

        _vertices[index] = value;
    }

    internal void AddIndex(ushort index)
    {
        if (_indices.Count + 1 > MaxPrimIndices)
        {
            throw new InvalidOperationException(
                "Exceeded max water indicies.\nIncrease surface subdivision size! "
                + $"({_indices.Count + 1}>{MaxPrimIndices})");
        }

        _indices.Add(index);
    }

    internal void CheckVertexLimit()
    {
        if (_vertices.Count > MaxPrimVerts)
        {
            throw new InvalidOperationException(
                "Exceeded max water verts.\nIncrease surface subdivision size or lower your "
                + $"subdivision size in vmt files! ({_vertices.Count}>{MaxPrimVerts})");
        }
    }

    internal int Commit(DPrimitive primitive)
    {
        _primitives.Add(primitive);

        if (_primitives.Count > MaxPrimitives)
        {
            throw new InvalidOperationException(
                "Exceeded max water primitives.\nIncrease surface subdivision size! "
                + $"({_primitives.Count}>{MaxPrimitives})");
        }

        return _primitives.Count - 1;
    }
}

/// <summary>
/// A primitive under construction: <c>dprimitive_t &amp;newPrim =
/// g_primitives[g_numprimitives]</c> before the count is bumped.
/// </summary>
/// <remarks>
/// Stock writes the struct into the array first and increments the count only
/// once the primitive turns out to be real (: "don't
/// increment until we get here and are sure that we have a primitive"). The
/// builder is that shape made explicit: a primitive that is abandoned is never
/// committed, and the indices and vertices it did write stay — which stock also
/// leaves behind.
/// </remarks>
public sealed class PrimitiveBuilder
{
    private readonly PrimitiveTable _table;

    internal PrimitiveBuilder(PrimitiveTable table, PrimitiveType type)
    {
        _table = table;
        Type = type;
        FirstIndex = table.IndexCount;
        FirstVert = table.VertexCount;
        Id = table.PrimitiveCount;
    }

    /// <summary>The index this primitive will get if it is committed.</summary>
    public int Id { get; }

    /// <summary>Triangle list or strip.</summary>
    public PrimitiveType Type { get; }

    /// <summary><c>firstIndex</c>.</summary>
    public int FirstIndex { get; }

    /// <summary><c>firstVert</c>.</summary>
    public int FirstVert { get; }

    /// <summary><c>indexCount</c>, as it grows.</summary>
    public int IndexCount { get; private set; }

    /// <summary><c>vertCount</c>, as it grows.</summary>
    public int VertCount { get; set; }

    /// <summary>
    /// Adds a winding's points to the primitive's vertex array, welding to
    /// points it already holds
    /// (<c>AddWindingToPrimverts</c>).
    /// </summary>
    /// <param name="points">The winding's points.</param>
    /// <param name="indices">Filled with one index per point.</param>
    /// <exception cref="InvalidOperationException">The vertex table overflowed.</exception>
    /// <remarks>
    /// The weld is a linear scan of this primitive's own vertices with a
    /// squared-distance test against <see cref="VertexWeld.PointEpsilon"/>, so
    /// two water tiles that share an edge share its vertices — but only within
    /// one face's primitive.
    /// </remarks>
    public void AddWinding(ReadOnlySpan<Vec3> points, Span<ushort> indices)
    {
        const double epsilon = VertexWeld.PointEpsilon * VertexWeld.PointEpsilon;

        for (int i = 0; i < points.Length; i++)
        {
            int j;

            for (j = FirstVert; j < FirstVert + VertCount; j++)
            {
                if ((_table.VertexAt(j) - points[i]).LengthSquared() < epsilon)
                {
                    indices[i] = (ushort)j;
                    break;
                }
            }

            if (j >= FirstVert + VertCount)
            {
                indices[i] = (ushort)j;
                _table.SetVertexAt(j, points[i]);
                VertCount++;
                _table.CheckVertexLimit();
            }
        }
    }

    /// <summary>Appends one index to the primitive's run.</summary>
    /// <param name="index">The index, into <see cref="PrimitiveTable.Vertices"/>.</param>
    public void AddIndex(ushort index)
    {
        _table.AddIndex(index);
        IndexCount++;
    }

    /// <summary>Writes the primitive into the table.</summary>
    /// <returns>Its index.</returns>
    public int Commit() => _table.Commit(new DPrimitive
    {
        Type = (byte)Type,
        FirstIndex = (ushort)FirstIndex,
        FirstVert = (ushort)FirstVert,
        IndexCount = (ushort)IndexCount,
        VertCount = (ushort)VertCount,
    });
}
