//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Faces;
using SourceSharp.MapTools.Bsp.Portals;

namespace SourceSharp.MapTools.Bsp.Write;

/// <summary>
/// The <c>d*</c> arrays of one compile: every lump vbsp's write stage fills,
/// before it is serialised.
/// </summary>
/// <remarks>
/// <para>
/// Stock keeps these as ~110 MB of process-wide globals in the reference implementation
/// with a bare counter beside each (<c>dnodes</c>/<c>numnodes</c>, ...). Here
/// they are one object per compile, so two compiles in one process cannot see
/// each other's lumps. <see cref="BeginBspFile"/> is <c>BeginBSPFile</c>
/// And seeds exactly the entries stock reserves.
/// </para>
/// <para>
/// The vertex, edge and primitive tables are the face stage's, shared rather
/// than copied: vertices are welded during <c>FixTjuncs</c> and edges are
/// shared during <c>WriteBSP</c>, both into the one table per map.
/// </para>
/// </remarks>
internal sealed class BspWriteState
{
    internal BspWriteState(FaceBuildContext faces)
    {
        ArgumentNullException.ThrowIfNull(faces);
        Faces = faces;
        Edges = new EdgeTable(faces.Counters);
    }

    /// <summary>The face stage's context: vertex weld, primitives, face lists.</summary>
    internal FaceBuildContext Faces { get; }

    /// <summary><c>dedges</c> + <c>edgefaces</c>.</summary>
    internal EdgeTable Edges { get; }

    internal List<DPlane> Planes { get; } = [];

    internal List<DNode> Nodes { get; } = [];

    internal List<DLeaf> Leafs { get; } = [];

    /// <summary>The tree node each emitted leaf came from, index for index (leaf 0 has none).</summary>
    internal List<IBspNode?> LeafNodes { get; } = [];

    internal List<ushort> LeafFaces { get; } = [];

    internal List<ushort> LeafBrushes { get; } = [];

    internal List<DFace> DrawFaces { get; } = [];

    /// <summary>
    /// <c>dfacenodes</c>: the water leaf each emitted
    /// face was generated in, for the fog volume pass.
    /// </summary>
    internal List<IBspNode?> FaceNodes { get; } = [];

    internal List<DFaceId> FaceIds { get; } = [];

    internal List<DFace> OrigFaces { get; } = [];

    internal List<int> SurfEdges { get; } = [];

    internal List<DModel> Models { get; } = [];

    internal List<DBrush> Brushes { get; } = [];

    internal List<DBrushSide> BrushSides { get; } = [];

    internal List<DArea> Areas { get; } = [];

    internal List<DAreaPortal> AreaPortals { get; } = [];

    internal List<Vec3> ClipPortalVerts { get; } = [];

    internal List<DLeafWaterData> LeafWaterData { get; } = [];

    internal List<DOccluderData> Occluders { get; } = [];

    internal List<DOccluderPolyData> OccluderPolys { get; } = [];

    internal List<int> OccluderVertexIndices { get; } = [];

    internal List<Vec3> VertNormals { get; } = [];

    internal List<int> VertNormalIndices { get; } = [];

    internal List<ushort> FaceMacroTextureInfos { get; } = [];

    /// <summary><c>g_SkyAreas</c>: the areas holding a <c>sky_camera</c>.</summary>
    internal List<int> SkyAreas { get; } = [];

    /// <summary>The texinfo table after <c>CompactTexinfos</c>; null until then.</summary>
    internal List<TexInfo>? CompactedTexInfo { get; set; }

    /// <summary>The texdata table after <c>CompactTexinfos</c>; null until then.</summary>
    internal List<DTexData>? CompactedTexData { get; set; }

    /// <summary>The string table after <c>CompactTexinfos</c>; null until then.</summary>
    internal TexDataStringTable? CompactedStrings { get; set; }

    /// <summary>The ENTITIES lump text, NUL included; null until <c>UnparseEntities</c>.</summary>
    internal byte[]? EntityData { get; set; }

    /// <summary><c>firstmodeledge</c>.</summary>
    internal int FirstModelEdge { get; set; }

    /// <summary>The face stage's vertex table, which is <c>dvertexes</c>.</summary>
    internal VertexWeld Vertices => Faces.Vertices;

    /// <summary>
    /// <c>BeginBSPFile</c>: clear everything and reserve the entries the file
    /// format leaves as errors.
    /// </summary>
    /// <remarks>
    /// Edge 0 is not used "because 0 can't be negated", vertex 0 is "an
    /// error" (reserved by <see cref="VertexWeld"/> itself) and leaf 0 is an
    /// error leaf whose contents are <c>CONTENTS_SOLID</c> and every other
    /// field zero -- including its CLUSTER, 0
    /// rather than -1, because <c>dleafs</c> is a zeroed global and
    /// <c>SaveClusters_r</c> starts writing at leaf 1.
    /// </remarks>
    internal void BeginBspFile()
    {
        // edge 0 is not used, because 0 can't be negated. Its face slot is a
        // placeholder that nothing ever reads: GetEdge2 and CreateOrigFace both
        // search from firstmodeledge, which is 1 or more.
        Edges.AddEdge(0, 0, PlaceholderFace);

        // leave leaf 0 as an error
        DLeaf error = default;
        error.Contents = (int)Materials.BrushContents.Solid;
        Leafs.Add(error);
        LeafNodes.Add(null);
    }

    // Edge 0's face slot. Stock's edgefaces[0] is a zeroed global (NULL); the
    // edge table requires a face, and none of the readers can reach index 0.
    private static Face PlaceholderFace => new(-1);
}
