using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Faces;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Bsp.Write;

/// <summary>
/// The write stage's half of displacements: <c>mapdispinfo</c>'s base faces
/// into LUMP_FACES during <c>WriteBSP</c>, then
/// Phase 3f's lump builder at <c>EmitDispLMAlphaAndNeighbors</c>
/// </summary>
/// <remarks>
/// <para>
/// <c>mapdispinfo</c> is in side-load order, which is the order sides sit in
/// the side array: the displacement brush itself is dropped
/// (<c>b-&gt;numsides = 0</c>) but its sides stay, and
/// <c>DispGetFaceInfo</c> has put the brush's contents and entity onto each
/// displacement.
/// </para>
/// <para>
/// The call order is p3f-t's (<c>p3f-t-findings.md</c>): base faces during
/// the model loop; <see cref="DispVbspHooks.Face"/> and
/// <see cref="DisplacementLumpBuilder.Build"/> after the lightmap extents;
/// <see cref="DispVbspHooks.WriteLumps"/> once LUMP_FACES exists.
/// </para>
/// </remarks>
internal sealed class DisplacementStage
{
    private readonly List<(MapDisplacement Disp, MapBrushSide Side)> _displacements = [];
    private readonly List<Face> _faces = [];
    private readonly DisplacementLumps _lumps = new();
    private IReadOnlyList<DisplacementResult> _results = [];

    internal DisplacementStage(MapFile map)
    {
        foreach (MapBrushSide side in map.BrushSides)
        {
            if (side.Displacement is MapDisplacement disp)
            {
                _displacements.Add((disp, side));
            }
        }
    }

    /// <summary><c>nummapdispinfo</c>.</summary>
    internal int Count => _displacements.Count;

    /// <summary>
    /// <c>WriteBSP</c>'s displacement loop for one entity: weld the base face's
    /// four points and emit it, off-node.
    /// </summary>
    /// <param name="entityNumber">The entity whose model is being written.</param>
    /// <param name="faces">The face stage's context.</param>
    /// <param name="writer">The tree writer.</param>
    internal void EmitFaces(int entityNumber, FaceBuildContext faces, BspTreeWriter writer)
    {
        TJunctionFixer welder = new(faces);

        for (int i = 0; i < _displacements.Count; i++)
        {
            (MapDisplacement disp, MapBrushSide side) = _displacements[i];
            if (disp.EntityNumber != entityNumber)
            {
                continue;
            }

            // DispGetFaceInfo's copy of the side, with EmitInitialDispInfos'
            // dispinfo index.
            Face face = faces.Faces.Alloc();
            face.OriginalFace = side;
            face.TexInfo = side.TexInfo;
            face.DispInfo = i;
            face.PlaneNumber = side.PlaneNumber;
            face.Winding = faces.Windings.Copy(side.Winding);
            face.NumPoints = faces.Windings.Points(face.Winding).Length;
            face.Contents = disp.Contents;

            Face? head = null;
            welder.EmitFaceVertexes(ref head, face);
            writer.EmitFace(face, onNode: false);

            while (_faces.Count <= i)
            {
                _faces.Add(face);
            }

            _faces[i] = face;
        }
    }

    /// <summary>
    /// <c>EmitDispLMAlphaAndNeighbors</c>: every displacement's lump data, the
    /// texinfo each base face carries, and the world-bounds boxes
    /// <c>ComputeBoundsNoSkybox</c> reads.
    /// </summary>
    /// <param name="compile">The compile.</param>
    /// <param name="state">The emitted lumps; the base faces' texinfos are written here.</param>
    /// <param name="bounds">Receives one box per displacement.</param>
    /// <remarks>
    /// Runs BEFORE <c>UpdateAllFaceLightmapExtents</c>, one step earlier than
    /// stock, in p3f2's order: the build needs
    /// no face extents, and under Correct the base face must already carry
    /// its swapped texinfo when its lightmap mins are computed
    /// (<see cref="StockQuirk.DispLightmapSwapDropped"/>). Under Stock the
    /// texinfos come back unchanged, so the move is unobservable.
    /// </remarks>
    internal void Build(VbspContext compile, BspWriteState state, List<(Vec3 Mins, Vec3 Maxs)> bounds)
    {
        if (_displacements.Count == 0)
        {
            return;
        }

        List<MapDisplacement> disps = [];
        List<DisplacementFace> faces = [];
        List<int> faceTexInfos = [];
        for (int i = 0; i < _displacements.Count; i++)
        {
            (MapDisplacement disp, MapBrushSide side) = _displacements[i];
            Face face = i < _faces.Count ? _faces[i] : throw new MapCompileException(WriteCodes.Internal,
                $"displacement {i} (side {side.Id}) has no base face: its entity has no model");

            // The UNSWAPPED texinfo, as DispMapToCoreDispInfo reads it.
            TexInfo texInfo = compile.TexInfos[side.TexInfo];
            Vec3[] winding = [.. compile.Windings.Points(side.Winding)];

            disps.Add(disp);
            faces.Add(DispVbspHooks.Face(face.OutputNumber, winding, disp.Contents, texInfo));
            faceTexInfos.Add(state.DrawFaces[face.OutputNumber].TexInfo);
        }

        List<DispBox> boxes = [];
        _results = DisplacementLumpBuilder.Build(disps, faces, compile.Options, _lumps, compile.Diagnostics, boxes);

        // The swap bookkeeping: Correct appends one swapped copy per original,
        // in face order, to the texinfo table; Stock returns the input.
        List<TexInfo> table = [.. compile.TexInfos.TexInfos];
        int before = table.Count;
        int[] chosen = DispVbspHooks.FaceTexInfos(_results, faceTexInfos, table, compile.Options.Compliance);
        for (int i = before; i < table.Count; i++)
        {
            int added = compile.TexInfos.Add(table[i]);
            if (added != i)
            {
                throw new MapCompileException(WriteCodes.Internal, $"swapped texinfo {i} landed at {added}");
            }
        }

        for (int i = 0; i < faces.Count; i++)
        {
            int index = faces[i].FaceIndex;
            DFace face = state.DrawFaces[index];
            face.TexInfo = (short)chosen[i];
            state.DrawFaces[index] = face;
        }

        // ComputeDispInfoBounds' boxes, from the build's own cores.
        foreach (DispBox box in boxes)
        {
            bounds.Add((box.Min, box.Max));
        }
    }

    /// <summary>
    /// What reads per displacement, in
    /// <c>g_CoreDispInfos</c> order, once <see cref="Build"/> has run: the
    /// built surface, <c>mapdispinfo[i].contents</c>, and the index of its
    /// base face (whose texinfo is <c>mapdispinfo[i].face.texinfo</c>).
    /// </summary>
    /// <returns>One entry per displacement.</returns>
    internal IEnumerable<(CoreDispInfo Core, int Contents, int FaceIndex)> CollisionSources()
    {
        for (int i = 0; i < _results.Count; i++)
        {
            yield return (_results[i].Core, _displacements[i].Disp.Contents, _faces[i].OutputNumber);
        }
    }

    /// <summary>The four lumps and each face's lightmap size, once LUMP_FACES exists.</summary>
    /// <param name="bsp">The assembled file.</param>
    internal void WriteLumps(BspData bsp)
    {
        if (_displacements.Count == 0)
        {
            return;
        }

        DispVbspHooks.WriteLumps(bsp, _results, _lumps);
    }
}
