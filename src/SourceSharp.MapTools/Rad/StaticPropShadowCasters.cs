using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad;

/// <summary>One studio vertex's texture coordinate.</summary>
/// <param name="U">The horizontal coordinate.</param>
/// <param name="V">The vertical coordinate.</param>
/// <remarks>
/// A pair of floats rather than the file's <c>FloatArray2</c> so that the
/// transparency delegate's signature does not drag an inline-array struct --
/// which can only be indexed through a variable -- across a public boundary.
/// </remarks>
public readonly record struct PropTexCoord(float U, float V);

/// <summary>What the texture-shadow pass decided about one triangle.</summary>
/// <param name="Coverage">
/// How much light the triangle blocks, stock's <c>color.x</c>. Only read when
/// <paramref name="MaterialIndex"/> is not negative, which is stock's own
/// Arithmetic: <c>color</c> starts at
/// <c>vec3_origin</c> and is written only inside the <c>coverage &lt; 1.0f</c>
/// branch.
/// </param>
/// <param name="MaterialIndex">
/// The entry in the shadow-texture material table, or -1 when the triangle is
/// fully opaque and needs no per-ray alpha test.
/// </param>
public readonly record struct PropTriangleShadow(float Coverage, int MaterialIndex);

/// <summary>
/// Decides one render-mesh triangle's transparency, from its material and its
/// texture coordinates.
/// </summary>
/// <param name="modelPath">The prop model the triangle belongs to.</param>
/// <param name="materialName">
/// The mesh's material name, as <c>mstudiotexture_t::pszName</c> gives it: no
/// path and no extension.
/// </param>
/// <param name="uv0">The first vertex's texture coordinate.</param>
/// <param name="uv1">The second vertex's.</param>
/// <param name="uv2">The third vertex's.</param>
/// <returns>The coverage and material index to attach to the triangle.</returns>
/// <remarks>
/// <para>
/// This is <c>g_ShadowTextureList.ComputeCoverageForTriangle</c> followed by
/// <c>AddMaterialEntry</c> behind a
/// delegate, so that the static prop loader does not depend on the shadow
/// texture list. An implementation MUST return a negative
/// <see cref="PropTriangleShadow.MaterialIndex"/> when the coverage is 1 --
/// Stock's <c>else materialIndex = -1</c> -- because that is
/// what keeps a fully opaque alpha-tested triangle out of the per-ray
/// transparency test.
/// </para>
/// <para>
/// It is called ONCE PER DISTINCT MODEL, not once per prop: see
/// <see cref="StaticPropShadowCasters.AddAsync"/> for the cache stock keeps
/// and the defect that comes with it.
/// </para>
/// </remarks>
public delegate PropTriangleShadow StaticPropTriangleTransparency(
    VPath modelPath,
    string materialName,
    PropTexCoord uv0,
    PropTexCoord uv1,
    PropTexCoord uv2);

/// <summary>
/// Why the static prop caster pass stopped before it ran out of props.
/// </summary>
/// <remarks>
/// BOTH VALUES ARE STOCK DEFECTS. <c>AddPolysForRayTrace</c> has two
/// <c>return</c> statements where every reading of the surrounding code wants
/// a <c>continue</c>, and each one silently drops every prop after the one
/// that tripped it -- not the prop, the REST OF THE MAP. They are reproduced
/// because the caster set is gated against stock's, and reported here rather
/// than swallowed so that a compile can say it happened.
/// </remarks>
public enum StaticPropAbandonReason
{
    /// <summary>The pass ran to the end of the prop list.</summary>
    None,

    /// <summary>
    /// A prop's model has no studio header or no VTX
    /// Stock's comment is "must have
    /// model and its verts for decoding triangles", which is a reason to skip
    /// the prop; the code returns from the whole function.
    /// </summary>
    MissingModelOrVtx,

    /// <summary>
    /// A strip is not a triangle list
    /// Stock prints "unexpected strips
    /// found", asserts, and returns -- so on a debug build it is a crash and
    /// on a release build it is a map whose props stop casting shadows part
    /// way through.
    /// </summary>
    NonTriangleListStrip,
}

/// <summary>
/// What one <see cref="StaticPropShadowCasters.AddAsync"/> pass did.
/// </summary>
/// <param name="PropsConsidered">How many props the lump held.</param>
/// <param name="PropsSkippedNoShadow">
/// How many carried <see cref="StaticPropFlags.NoShadow"/> and were skipped
/// Before anything else was decided.
/// </param>
/// <param name="PropsFromCollision">
/// How many contributed collision triangles through
/// <see cref="IPropCollisionSource"/>.
/// </param>
/// <param name="PropsFromHullBox">
/// How many fell to the axis-aligned hull box
/// </param>
/// <param name="PropsFromRenderMesh">
/// How many contributed render-mesh triangles under
/// <c>-StaticPropPolys</c>.
/// </param>
/// <param name="TrianglesAdded">How many triangles reached the builder.</param>
/// <param name="Abandoned">Whether one of stock's two early returns fired.</param>
/// <param name="AbandonedAtProp">
/// The index of the prop that tripped it, or -1.
/// </param>
public readonly record struct StaticPropShadowCasterReport(
    int PropsConsidered,
    int PropsSkippedNoShadow,
    int PropsFromCollision,
    int PropsFromHullBox,
    int PropsFromRenderMesh,
    int TrianglesAdded,
    StaticPropAbandonReason Abandoned,
    int AbandonedAtProp);

/// <summary>
/// The switches <c>AddPolysForRayTrace</c> reads out of vrad's globals.
/// </summary>
public sealed class StaticPropShadowCasterOptions
{
    /// <summary>
    /// <c>-StaticPropPolys</c>: cast from the render mesh rather than from
    /// collision(<c>g_bStaticPropPolys</c>).
    /// </summary>
    /// <remarks>
    /// It is not a refinement of the default, it is a different pipeline: the
    /// default reads a <c>.phy</c> through vphysics and this reads the
    /// <c>.dx80.vtx</c> and <c>.vvd</c>. On this project's golden map stock
    /// emits 17,304 triangles by default and 92,582 with the switch.
    /// </remarks>
    public bool StaticPropPolys { get; init; }

    /// <summary>
    /// The <c>noshadow</c> names from <c>lights.rad</c>
    /// (<c>g_NonShadowCastingMaterialStrings</c>).
    /// </summary>
    /// <remarks>
    /// Matched as a CASE-INSENSITIVE SUBSTRING of a mesh's material name
    /// (<c>Q_stristr</c>), not as an equality
    /// and not as a glob. So <c>noshadow glass</c> silences every material
    /// with "glass" anywhere in its name, which is deliberate on the reference build's part
    /// and is why the list entries are short. The <c>.vmt</c> extension is
    /// stripped when the line is parsed, so entries never carry one.
    /// </remarks>
    public IReadOnlyList<string> NoShadowMaterials { get; init; } = [];

    /// <summary>
    /// The models <c>lights.rad</c> named in <c>forcetextureshadow</c> lines
    /// </summary>
    /// <remarks>
    /// A model takes part in the texture-shadow pass if it is named here OR it
    /// carries <c>STUDIOHDR_FLAGS_CAST_TEXTURE_SHADOWS</c>
    /// Compared after
    /// <see cref="StaticPropModel.CleanModelName"/> on both sides, which is
    /// what stock's <c>IsModelTextureShadowsForced</c> does.
    /// </remarks>
    public IReadOnlyList<string> ForcedTextureShadowModels { get; init; } = [];

    /// <summary>
    /// Decides one triangle's transparency, or null when
    /// <c>-textureshadows</c> is off.
    /// </summary>
    /// <remarks>
    /// Null is the ordinary case and is exactly stock with
    /// <c>g_bTextureShadows</c> clear: the dictionary's
    /// <c>m_textureShadowIndex</c> table is empty, <c>shadowTextureIndex</c>
    /// stays -1, and every render-mesh triangle is added opaque.
    /// </remarks>
    public StaticPropTriangleTransparency? Transparency { get; init; }
}

/// <summary>
/// <c>CVradStaticPropMgr::AddPolysForRayTrace</c>,
/// Every static prop's shadow casting
/// geometry, in stock's order.
/// </summary>
public static class StaticPropShadowCasters
{
    /// <summary>
    /// <c>STRIP_IS_TRILIST</c>.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than taken from
    /// <see cref="VtxStripFlags.IsTriList"/> only in this comment: the value
    /// is 0x01 and the enum is the one used below.
    /// </remarks>
    private const byte StripIsTriList = (byte)VtxStripFlags.IsTriList;

    /// <summary>
    /// Adds every static prop's caster triangles to a builder.
    /// </summary>
    /// <param name="props">The map's <c>sprp</c> lump.</param>
    /// <param name="content">Where the prop models live.</param>
    /// <param name="collision">Where a model's collision triangles come from.</param>
    /// <param name="options">The switches.</param>
    /// <param name="builder">The caster set under construction.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What the pass did.</returns>
    /// <exception cref="ArgumentNullException">Any argument but the token is null.</exception>
    /// <exception cref="InvalidBspException">
    /// A prop names a model outside the lump's own dictionary.
    /// </exception>
    /// <exception cref="InvalidStudioException">
    /// A model's render mesh is needed and its <c>.vvd</c> is not in the
    /// content. Stock's equivalent is <c>Error()</c>
    /// Which aborts the compile.
    /// </exception>
    public static async ValueTask<StaticPropShadowCasterReport> AddAsync(
        StaticPropLump props,
        IContentFileSystem content,
        IPropCollisionSource collision,
        StaticPropShadowCasterOptions options,
        ShadowCasterBuilder builder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(props);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(collision);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(builder);

        builder.BeginSource(ShadowCasterSource.StaticProp);

        int count = props.Props.Count;
        if (count == 0)
        {
            // -- "nothing to do", before the model
            // dictionary is touched at all.
            return new StaticPropShadowCasterReport(
                0, 0, 0, 0, 0, 0, StaticPropAbandonReason.None, -1);
        }

        StaticPropModelLoader loader =
            new(content, collision, options.ForcedTextureShadowModels);
        IReadOnlyList<StaticPropModel> models =
            await loader.LoadDictionaryAsync(props.ModelNames, cancellationToken)
                .ConfigureAwait(false);

        Pass pass = new(props, models, options, builder);
        pass.Run(cancellationToken);
        return pass.Report;
    }

    /// <summary>
    /// One run of the prop loop, with the two caches stock keeps on its
    /// dictionary entries.
    /// </summary>
    /// <remarks>
    /// A class rather than a pile of locals because the caches are what make
    /// the pass correct, not just fast: the material-index cache is stock's
    /// own and is observable in the output.
    /// </remarks>
    private sealed class Pass
    {
        private readonly StaticPropLump _props;
        private readonly IReadOnlyList<StaticPropModel> _models;
        private readonly StaticPropShadowCasterOptions _options;
        private readonly ShadowCasterBuilder _builder;

        // Stock's dict.m_triangleMaterialIndex,
        // keyed by dictionary index. Created empty on first use so that
        // "is this the first prop of this model?" is Count == 0, which is
        // exactly what stock's bInitTriangles tests.
        private readonly Dictionary<int, List<int>> _materialIndices = [];

        // This port's own: the mesh walk of one model is identical for every
        // prop that uses it, so it is done once. The plan records where a
        // non-trilist strip stopped it, so that stock's early return still
        // fires at the prop it would have fired at.
        private readonly Dictionary<int, RenderPlan> _plans = [];

        private int _skippedNoShadow;
        private int _fromCollision;
        private int _fromHullBox;
        private int _fromRenderMesh;
        private int _added;
        private StaticPropAbandonReason _abandoned = StaticPropAbandonReason.None;
        private int _abandonedAt = -1;

        public Pass(
            StaticPropLump props,
            IReadOnlyList<StaticPropModel> models,
            StaticPropShadowCasterOptions options,
            ShadowCasterBuilder builder)
        {
            _props = props;
            _models = models;
            _options = options;
            _builder = builder;
        }

        public StaticPropShadowCasterReport Report => new(
            _props.Props.Count,
            _skippedNoShadow,
            _fromCollision,
            _fromHullBox,
            _fromRenderMesh,
            _added,
            _abandoned,
            _abandonedAt);

        public void Run(CancellationToken cancellationToken)
        {
            for (int nProp = 0; nProp < _props.Props.Count; nProp++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                StaticProp prop = _props.Props[nProp];

                int modelIndex = prop.PropType;
                if ((uint)modelIndex >= (uint)_models.Count)
                {
                    // Stock indexes m_StaticPropDict with m_ModelIdx and never
                    // range checks it, so a lump
                    // whose dictionary and props disagree reads whatever
                    // follows the array. Refused here instead.
                    throw new InvalidBspException(
                        $"static prop {nProp} names model {modelIndex} of a "
                        + $"{_models.Count}-entry dictionary");
                }

                StaticPropModel model = _models[modelIndex];

                // FIRST, before the model is looked
                // at: a NO_SHADOW prop with a broken model is not a warning.
                //
                // Note what is NOT tested anywhere in this function:
                // prop.Solid. vrad parses m_Solid out of the lump
                // And the shadow path never reads it, so a
                // SOLID_NONE prop -- one the player walks through -- still
                // casts a full shadow. Pinned by a fact rather than fixed.
                if ((prop.Flags & StaticPropFlags.NoShadow) != 0)
                {
                    _skippedNoShadow++;
                    continue;
                }

                int id = TraceId.StaticProp | nProp;

                if (!_options.StaticPropPolys)
                {
                    AddDefault(id, prop, model);
                    continue;
                }

                if (!AddRenderMesh(nProp, id, prop, model, modelIndex))
                {
                    // And -- RETURN, not
                    // continue. Every prop after this one is dropped.
                    return;
                }
            }
        }

        /// <summary>
        /// The default path: collision triangles, or the hull box
        /// </summary>
        private void AddDefault(int id, StaticProp prop, StaticPropModel model)
        {
            if (model.Collision is not null)
            {
                // xform.SetupMatrixOrgAngles( prop.m_Origin, prop.m_Angles ),
                // Then VMul4x3 per vertex.
                //
                // Built from AngleMatrix's element order rather than
                // SetupMatrixAnglesInternal's. The two are
                // algebraically the same matrix -- sr*sp*cy + cr*-sy against
                // sp*(sr*cy) - cr*sy -- and differ only in how the products
                // are associated, so they can disagree in the last bit of a
                // float. VMul4x3 is Vector3DMultiplyPosition, which is
                // VectorTransform.
                InstanceTransform transform =
                    InstanceTransform.FromAngles(prop.Angles, prop.Origin);

                PropCollisionMesh mesh = model.Collision;
                for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
                {
                    Vec3 v0 = transform.TransformPoint(mesh.Vertices[mesh.Indices[i]]);
                    Vec3 v1 = transform.TransformPoint(mesh.Vertices[mesh.Indices[i + 1]]);
                    Vec3 v2 = transform.TransformPoint(mesh.Vertices[mesh.Indices[i + 2]]);

                    // fullCoverage: a Vector whose x is 1 and whose y and z
                    // were never initialised. Only
                    // x is ever read.
                    _builder.AddTriangle(id, v0, v1, v2, 1.0f);
                    _added++;
                }

                _fromCollision++;
                return;
            }

            //
            // THE ANGLES ARE IGNORED. The box is the model's hull translated
            // by the prop's origin and nothing else, so a prop rotated 45
            // degrees casts the shadow of its unrotated bounding box -- and a
            // tall thin prop lying on its side casts a tall thin upright one.
            // A stock defect, reproduced; pinned by a fact so that rotating
            // the hull later is a decision.
            //
            // Note also that in stock this branch is reachable ONLY when the
            // model failed to load, in which case both hull corners were
            // zeroed and the "box" is a degenerate point at the
            // prop's origin. This port also lands here when the collision
            // source has nothing for a model that loaded fine, where the hull
            // is real -- see NullPropCollisionSource.
            Vec3 mins = model.HullMin + prop.Origin;
            Vec3 maxs = model.HullMax + prop.Origin;

            _builder.AddAxisAlignedRectangularSolid(id, mins, maxs, 1.0f);
            _added += 12;
            _fromHullBox++;
        }

        /// <summary>
        /// The <c>-StaticPropPolys</c> path
        /// </summary>
        /// <returns>False when one of stock's two early returns fired.</returns>
        private bool AddRenderMesh(
            int nProp,
            int id,
            StaticProp prop,
            StaticPropModel model,
            int modelIndex)
        {
            if (model.Mdl is null || model.Vtx is null)
            {
                _abandoned = StaticPropAbandonReason.MissingModelOrVtx;
                _abandonedAt = nProp;
                return false;
            }

            if (!_plans.TryGetValue(modelIndex, out RenderPlan? plan))
            {
                plan = RenderPlan.Build(model, _options);
                _plans[modelIndex] = plan;
            }

            // AngleMatrix( prop.m_Angles, prop.m_Origin, matrix ),
            // -- which stock rebuilds INSIDE the
            // per-triangle loop, from two values that do not change. Hoisted
            // here: the matrix is a pure function of the prop's angles and
            // origin, so every triangle gets the bit-identical matrix stock
            // would have built for it, and the only difference is how many
            // times six transcendentals are evaluated.
            InstanceTransform transform =
                InstanceTransform.FromAngles(prop.Angles, prop.Origin);

            // dict.m_triangleMaterialIndex.
            if (!_materialIndices.TryGetValue(modelIndex, out List<int>? materialIndices))
            {
                materialIndices = [];
                _materialIndices[modelIndex] = materialIndices;
            }

            bool initTriangles = materialIndices.Count == 0;
            int triangleIndex = 0;

            foreach (PlannedTriangle triangle in plan.Triangles)
            {
                Vec3 p1 = transform.TransformPoint(model.VertexPosition(triangle.V0));
                Vec3 p2 = transform.TransformPoint(model.VertexPosition(triangle.V1));
                Vec3 p3 = transform.TransformPoint(model.VertexPosition(triangle.V2));

                // Colour starts at
                // vec3_origin, flags at 0, material index at -1. So WITHOUT
                // -textureshadows every render-mesh caster triangle carries
                // coverage ZERO, not 1 -- unlike the default path's
                // fullCoverage. Harmless, because coverage is only read for a
                // triangle flagged FCACHETRI_TRANSPARENT, but it is stock's
                // number and it is reproduced rather than tidied.
                float coverage = 0.0f;
                byte flags = 0;
                int materialIndex = -1;

                if (triangle.HasShadowTexture && _options.Transparency is not null)
                {
                    if (initTriangles)
                    {
                        PropTriangleShadow shadow = _options.Transparency(
                            model.Path,
                            plan.MaterialNames[triangle.Material],
                            model.VertexTexCoord(triangle.V0),
                            model.VertexTexCoord(triangle.V1),
                            model.VertexTexCoord(triangle.V2));

                        materialIndex = shadow.MaterialIndex;
                        if (materialIndex >= 0)
                        {
                            coverage = shadow.Coverage;
                        }

                        materialIndices.Add(materialIndex);
                    }
                    else
                    {
                        // A STOCK DEFECT worth
                        // naming: the replay branch restores the material
                        // index but NOT the coverage, which stays vec3_origin.
                        // So the first prop to use a model gets the real
                        // coverage and every later prop sharing that model
                        // gets zero -- identical triangles shadowing
                        // differently depending on which prop index drew them
                        // first. Reproduced; pinned by a fact.
                        materialIndex = materialIndices[triangleIndex];
                        triangleIndex++;
                    }

                    if (materialIndex >= 0)
                    {
                        flags = TracedTriangle.Transparent;
                    }
                }

                _builder.AddTriangle(id, p1, p2, p3, coverage, flags, materialIndex);
                _added++;
            }

            _fromRenderMesh++;

            if (plan.NonTriangleListStrip)
            {
                // The triangles emitted above
                // are the ones stock had already added when it hit the bad
                // strip; then it returns out of the whole function.
                _abandoned = StaticPropAbandonReason.NonTriangleListStrip;
                _abandonedAt = nProp;
                return false;
            }

            return true;
        }
    }

    /// <summary>One triangle of a model's render mesh, resolved to LOD 0 vertices.</summary>
    /// <param name="V0">Global vertex index of the first corner.</param>
    /// <param name="V1">The second.</param>
    /// <param name="V2">The third.</param>
    /// <param name="Material">Index into <see cref="RenderPlan.MaterialNames"/>.</param>
    /// <param name="HasShadowTexture">
    /// Whether the mesh's material takes part in the texture-shadow pass --
    /// stock's <c>shadowTextureIndex &gt;= 0</c>.
    /// </param>
    private readonly record struct PlannedTriangle(
        int V0, int V1, int V2, int Material, bool HasShadowTexture);

    /// <summary>
    /// One model's render-mesh walk, done once and replayed for every prop
    /// that uses the model.
    /// </summary>
    private sealed class RenderPlan
    {
        private RenderPlan(
            IReadOnlyList<PlannedTriangle> triangles,
            IReadOnlyList<string> materialNames,
            bool nonTriangleListStrip)
        {
            Triangles = triangles;
            MaterialNames = materialNames;
            NonTriangleListStrip = nonTriangleListStrip;
        }

        /// <summary>The triangles, in stock's emission order.</summary>
        public IReadOnlyList<PlannedTriangle> Triangles { get; }

        /// <summary>The model's material names, indexed by <c>mstudiomesh_t::material</c>.</summary>
        public IReadOnlyList<string> MaterialNames { get; }

        /// <summary>
        /// Whether the walk stopped at a strip that was not a triangle list.
        /// </summary>
        /// <remarks>
        /// <see cref="Triangles"/> then holds exactly what stock had already
        /// added when it hit the bad strip, which is what makes reproducing
        /// its <c>return</c> at the right moment possible from a precomputed
        /// plan.
        /// </remarks>
        public bool NonTriangleListStrip { get; }

        /// <summary>
        /// Walks body parts, models, LOD 0, meshes, strip groups and strips
        /// </summary>
        public static RenderPlan Build(StaticPropModel model, StaticPropShadowCasterOptions options)
        {
            MdlFile mdl = model.Mdl!;
            VtxFile vtx = model.Vtx!;

            if (model.Vvd is null)
            {
                throw new InvalidStudioException(
                    $"\"{model.Path.Value}\" has no .vvd in the content, so its render mesh has "
                    + "no vertex positions; stock calls Error() here "
                    + " and aborts the compile");
            }

            string[] materialNames = new string[mdl.Header.NumTextures];
            for (int i = 0; i < materialNames.Length; i++)
            {
                materialNames[i] = mdl.TextureName(i);
            }

            bool textureShadows = model.CastsTextureShadows;
            List<PlannedTriangle> triangles = [];
            IReadOnlyList<(int Offset, VtxBodyPartHeader Header)> vtxParts = vtx.BodyParts();

            for (int bodyId = 0; bodyId < mdl.Header.NumBodyParts; bodyId++)
            {
                if (bodyId >= vtxParts.Count)
                {
                    break;
                }

                (int vtxPartOffset, VtxBodyPartHeader vtxPart) = vtxParts[bodyId];
                IReadOnlyList<(int Offset, VtxModelHeader Header)> vtxModels =
                    vtx.Models(vtxPartOffset, vtxPart);
                ReadOnlySpan<StudioModel> studioModels = mdl.Models(bodyId);

                for (int modelId = 0; modelId < studioModels.Length; modelId++)
                {
                    if (modelId >= vtxModels.Count)
                    {
                        break;
                    }

                    (int vtxModelOffset, VtxModelHeader vtxModel) = vtxModels[modelId];
                    StudioModel studioModel = studioModels[modelId];

                    // "assuming lod 0, could iterate if required",
                    // LOD 0 only, which is why the
                    // vertices are VerticesForLod(0) and not the raw block.
                    IReadOnlyList<(int Offset, VtxModelLodHeader Header)> lods =
                        vtx.Lods(vtxModelOffset, vtxModel);
                    if (lods.Count == 0)
                    {
                        continue;
                    }

                    (int lodOffset, VtxModelLodHeader lod) = lods[0];
                    IReadOnlyList<(int Offset, VtxMeshHeader Header)> vtxMeshes =
                        vtx.Meshes(lodOffset, lod);
                    ReadOnlySpan<StudioMesh> meshes = mdl.Meshes(bodyId, modelId);

                    for (int meshId = 0; meshId < meshes.Length; meshId++)
                    {
                        if (meshId >= vtxMeshes.Count)
                        {
                            break;
                        }

                        StudioMesh mesh = meshes[meshId];
                        string materialName = (uint)mesh.Material < (uint)materialNames.Length
                            ? materialNames[mesh.Material]
                            : string.Empty;

                        // Any of the noshadow
                        // strings appearing ANYWHERE in the material name,
                        // case-insensitively, silences the whole mesh.
                        if (IsNonShadowCasting(materialName, options.NoShadowMaterials))
                        {
                            continue;
                        }

                        int vertexBase = StaticPropModel.MeshVertexBase(studioModel, mesh);
                        (int vtxMeshOffset, VtxMeshHeader vtxMesh) = vtxMeshes[meshId];

                        foreach ((int groupOffset, VtxStripGroupHeader group)
                            in vtx.StripGroups(vtxMeshOffset, vtxMesh))
                        {
                            ReadOnlySpan<ushort> indices = vtx.Indices(groupOffset, group);
                            ReadOnlySpan<VtxVertex> vertices = vtx.Vertices(groupOffset, group);

                            foreach (VtxStripHeader strip in vtx.Strips(groupOffset, group))
                            {
                                if ((strip.Flags & StripIsTriList) == 0)
                                {
                                    return new RenderPlan(triangles, materialNames, true);
                                }

                                for (int i = 0; i + 2 < strip.NumIndices; i += 3)
                                {
                                    int at = strip.IndexOffset + i;

                                    // The two-step every studio reader gets
                                    // wrong once: the strip's index names a
                                    // GROUP vertex, and that group vertex's
                                    // origMeshVertID names a MESH vertex,
                                    // which is then based into the model's
                                    // run and the file's block.
                                    int v0 = vertexBase + vertices[indices[at]].OrigMeshVertId;
                                    int v1 = vertexBase + vertices[indices[at + 1]].OrigMeshVertId;
                                    int v2 = vertexBase + vertices[indices[at + 2]].OrigMeshVertId;

                                    triangles.Add(new PlannedTriangle(
                                        v0, v1, v2, mesh.Material, textureShadows));
                                }
                            }
                        }
                    }
                }
            }

            return new RenderPlan(triangles, materialNames, false);
        }

        private static bool IsNonShadowCasting(
            string materialName,
            IReadOnlyList<string> noShadowMaterials)
        {
            foreach (string candidate in noShadowMaterials)
            {
                if (candidate.Length != 0
                    && materialName.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
