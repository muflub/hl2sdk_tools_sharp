using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapTools.Bsp.Collision;

/// <summary>
/// <c>EmitPhysCollision</c> (<c>ivp.cpp:1498</c>) with the displacement
/// collision of <c>disp_ivp.cpp</c>: LUMP_PHYSCOLLIDE and LUMP_PHYSDISP.
/// </summary>
/// <remarks>
/// <para>
/// The cooking runs on the cooker's thread, one work item per brush model,
/// so cancellation is observed between models and a compile never holds the
/// one cooker for the whole map. Everything else -- which brushes, which
/// planes, the shrink decisions, the material table, masses, the keydata
/// text and the record framing -- is managed code and is identical whatever
/// cooker is plugged in.
/// </para>
/// <para>
/// Entry point for the vbsp driver: see <see cref="PhysCollisionInput"/> for
/// where stock calls it and what it writes back.
/// </para>
/// </remarks>
public static class PhysCollisionEmitter
{
    /// <summary><c>NO_SHRINK</c>, <c>ivp.cpp:31</c>.</summary>
    public const float NoShrink = 0f;

    /// <summary><c>VPHYSICS_SHRINK</c>, <c>ivp.cpp:37</c>: brush entities shrink by half an inch.</summary>
    public const float VPhysicsShrink = 0.5f;

    /// <summary><c>VPHYSICS_MERGE</c>, <c>ivp.cpp:38</c>.</summary>
    public const float VPhysicsMerge = 0.01f;

    /// <summary><c>VPHYSICS_MAX_MASS</c>, <c>vphysics_interface.h:51</c>.</summary>
    public const float MaxMass = 5e4f;

    /// <summary>
    /// <c>CUBIC_METERS_PER_CUBIC_INCH</c>, <c>vphysics_interface.h:41</c>:
    /// <c>METERS_PER_INCH</c> cubed, in float, left to right.
    /// </summary>
    public const float CubicMetersPerCubicInch = 0.0254f * 0.0254f * 0.0254f;

    /// <summary>A brush-entity's mask: <c>MASK_SOLID|CONTENTS_PLAYERCLIP|CONTENTS_MONSTERCLIP|MASK_WATER</c> (<c>ivp.cpp:1535</c>).</summary>
    public const int BrushModelMask = CollisionContents.MaskSolid | CollisionContents.PlayerClip
        | CollisionContents.MonsterClip | CollisionContents.MaskWater;

    /// <summary>Builds both lumps.</summary>
    /// <param name="input">The finished BSP tables and the compile's side data.</param>
    /// <param name="cooker">The collision cooker.</param>
    /// <param name="cache">
    /// The per-model cooked-collision cache, or null (plan_maptools.md 10a):
    /// a model whose keyed inputs are unchanged is replayed instead of
    /// cooked, byte-identically to a fresh cook.
    /// </param>
    /// <param name="cancellationToken">Cancels between models.</param>
    /// <returns>The lumps and the leaf fix-ups.</returns>
    /// <exception cref="MapCompileException">A displacement has degenerate triangles (<c>disp_ivp.cpp:303</c>).</exception>
    public static async Task<PhysCollisionResult> EmitAsync(
        PhysCollisionInput input,
        ICollisionCooker cooker,
        ICollisionModelCache? cache = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(cooker);

        Context context = new(input);
        context.ClearLeafWaterData();

        List<PhysCollideModel> records = [];
        byte[]? physDisp = null;

        // The cache seam is ONE ask per model, around the cook. A hit
        // replaces the entries, the finished text and (world) the three
        // write-backs with the stored bytes; a miss cooks and offers
        // (plan_maptools.md 10a). The asks happen up front, in model order,
        // so a hit never starts the cook it would throw away.
        CachedCollisionModel?[] cached = new CachedCollisionModel?[input.Models.Count];
        if (cache is not null)
        {
            for (int i = 0; i < cached.Length; i++)
            {
                cached[i] = await cache.TryGetAsync(input, i, cancellationToken).ConfigureAwait(false);
            }
        }

        // Above one thread every uncached model's cook is started up front (plan 3p):
        // a brush model reads only the finished BSP, and only the world writes
        // the context (its material table and the leaf water ids), so the cooks
        // are independent. The results are still taken, and the records built,
        // in model order below; one at a time is stock's loop (ivp.cpp:1525-1537).
        Task<(List<PhysCollisionEntry>, bool, byte[]?)>?[] started =
            new Task<(List<PhysCollisionEntry>, bool, byte[]?)>?[input.Models.Count];
        if (input.MaxDegree > 1)
        {
            SemaphoreSlim gate = new(input.MaxDegree);
            for (int i = 0; i < input.Models.Count; i++)
            {
                if (cached[i] is null)
                {
                    started[i] = CookGatedAsync(gate, cooker, context, i, cancellationToken);
                }
            }
        }

        for (int i = 0; i < input.Models.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int modelIndex = i;

            if (cached[modelIndex] is { } hit && hit.ModelNumber == modelIndex)
            {
                if (modelIndex == 0)
                {
                    physDisp = hit.PhysDisp;
                    RestoreWorld(context, hit);
                }

                if (hit.Solids.Count == 0)
                {
                    continue;
                }

                records.Add(new PhysCollideModel(modelIndex, [.. hit.Solids], hit.Text));
                continue;
            }

            (List<PhysCollisionEntry> entries, bool virtualTerrain, byte[]? disp) = started[i] is { } cooking
                ? await cooking.ConfigureAwait(false)
                : await cooker.RunAsync(session => Cook(context, session, modelIndex), cancellationToken).ConfigureAwait(false);

            if (modelIndex == 0)
            {
                physDisp = disp;
            }

            if (entries.Count == 0)
            {
                // An empty model is a product too: caching it means the next
                // compile replays "nothing" instead of re-walking the tree.
                if (cache is not null)
                {
                    await cache.CookedAsync(
                        input,
                        modelIndex,
                        Offer(modelIndex, [], [],
                            modelIndex == 0 ? (short[])context.LeafWaterDataIds.Clone() : null,
                            modelIndex == 0 ? [.. context.WorldPropList] : null,
                            modelIndex == 0 ? disp : null),
                        cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            CollisionTextBuffer text = new();
            for (int j = 0; j < entries.Count; j++)
            {
                entries[j].WriteText(text, j);
            }

            // These sections only appear in the world's collision text (ivp.cpp:1558).
            if (modelIndex == 0)
            {
                if (virtualTerrain)
                {
                    text.WriteText("virtualterrain {}\n");
                }

                if (context.WorldPropList.Count > 0)
                {
                    text.WriteText("materialtable {\n");
                    for (int j = 0; j < context.WorldPropList.Count; j++)
                    {
                        int prop = context.WorldPropList[j];
                        text.WriteIntKey(prop < 0 ? "default" : input.SurfaceProps.GetPropName(prop) ?? string.Empty, j + 1);
                    }

                    text.WriteText("}\n");
                }
            }

            text.Terminate();
            byte[] keyData = text.ToArray();
            records.Add(new PhysCollideModel(modelIndex, [.. entries.Select(e => e.Blob)], keyData));

            if (cache is not null)
            {
                await cache.CookedAsync(
                    input,
                    modelIndex,
                    Offer(modelIndex, [.. entries.Select(e => e.Blob)], keyData,
                        modelIndex == 0 ? (short[])context.LeafWaterDataIds.Clone() : null,
                        modelIndex == 0 ? [.. context.WorldPropList] : null,
                        modelIndex == 0 ? disp : null),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return new PhysCollisionResult(
            PhysCollideLump.Write(records),
            physDisp,
            records,
            context.LeafWaterDataIds,
            context.LeafContents,
            [.. context.WorldPropList]);
    }

    // One model's cook: BuildWorldPhysModel for model 0, ConvertModelToPhysCollide
    // for a brush model (ivp.cpp:1529-1536).
    private static (List<PhysCollisionEntry>, bool, byte[]?) Cook(Context context, ICollisionSession session, int modelIndex) =>
        modelIndex == 0
            ? context.BuildWorld(session)
            : (context.BuildBrushModel(session, modelIndex), false, null);

    private static async Task<(List<PhysCollisionEntry>, bool, byte[]?)> CookGatedAsync(
        SemaphoreSlim gate, ICollisionCooker cooker, Context context, int modelIndex, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await cooker.RunAsync(session => Cook(context, session, modelIndex), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Applies a cached world model's three write-backs to the run context.</summary>
    private static void RestoreWorld(Context context, CachedCollisionModel cached)
    {
        if (cached.LeafWaterDataIds is { } water)
        {
            water.AsSpan(0, Math.Min(water.Length, context.LeafWaterDataIds.Length))
                .CopyTo(context.LeafWaterDataIds);
        }

        if (cached.WorldPropList is { } props)
        {
            context.WorldPropList.Clear();
            context.WorldPropList.AddRange(props);
        }
    }

    /// <summary>Wraps one model's finished products for the cache offer.</summary>
    private static CachedCollisionModel Offer(
        int modelIndex,
        IReadOnlyList<byte[]> solids,
        byte[] text,
        short[]? water,
        int[]? props,
        byte[]? physDisp) =>
        new(modelIndex, solids, text, water, props, physDisp);

    /// <summary>
    /// <c>PropIndex</c>, <c>ivp.cpp:375</c>: a surface property's 1-based slot
    /// in the world's material table, added if new, 0 once the table holds 126.
    /// </summary>
    /// <param name="propList">The table.</param>
    /// <param name="propIndex">The surface property (-1 allowed: "default").</param>
    /// <returns>The 7-bit material index.</returns>
    public static int PropIndex(List<int> propList, int propIndex)
    {
        ArgumentNullException.ThrowIfNull(propList);
        for (int i = 0; i < propList.Count; i++)
        {
            if (propList[i] == propIndex)
            {
                return i + 1;
            }
        }

        if (propList.Count < 126)
        {
            propList.Add(propIndex);
            return propList.Count;
        }

        return 0;
    }

    /// <summary>
    /// The drag-area epsilon for a brush model: 1% of its smallest bounding
    /// face, clamped to [1, 1024] (<c>ivp.cpp:1355-1371</c>).
    /// </summary>
    /// <param name="mins">The model's minimum.</param>
    /// <param name="maxs">The model's maximum.</param>
    /// <returns>The epsilon.</returns>
    public static float DragAreaEpsilon(Vec3 mins, Vec3 maxs)
    {
        Vec3 size = maxs - mins;
        float minSurfaceArea = -1.0f;
        for (int i = 0; i < 3; i++)
        {
            int other = (i + 1) % 3;
            int cross = (i + 2) % 3;
            float surfaceArea = size[other] * size[cross];
            if (minSurfaceArea < 0 || surfaceArea < minSurfaceArea)
            {
                minSurfaceArea = surfaceArea;
            }
        }

        return Math.Clamp(minSurfaceArea * 1e-2f, 1.0f, 1024.0f);
    }

    /// <summary>
    /// A brush model's mass and material from its faces
    /// (<c>ivp.cpp:1377-1470</c>): the surface property covering the most
    /// area wins, and the mass is either shell (area x thickness x density)
    /// or solid (volume x density), clamped to <see cref="MaxMass"/>.
    /// </summary>
    /// <param name="props">Surface property per face, in face order.</param>
    /// <param name="areas">Area per face.</param>
    /// <param name="noFacesProp">For a model with no faces: its first brush side's property; otherwise null.</param>
    /// <param name="totalVolume">The summed convex volumes.</param>
    /// <param name="surfaceProps">The database.</param>
    /// <param name="compliance">Whether <see cref="StockQuirk.ShellMassSentinelArea"/> is reproduced.</param>
    /// <returns>The material name and the mass.</returns>
    public static (string Material, float Mass) MassAndMaterial(
        IReadOnlyList<int> props,
        IReadOnlyList<float> areas,
        int? noFacesProp,
        float totalVolume,
        SurfacePropertyTable surfaceProps,
        ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(props);
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(surfaceProps);
        ArgumentNullException.ThrowIfNull(compliance);

        const int MaxProps = 256;
        List<(int Prop, float Area)> proplist = [(-1, 1f)];

        if (noFacesProp is int first)
        {
            proplist.Add((first, 2f));
        }

        for (int i = 0; i < props.Count; i++)
        {
            int prop = props[i];
            int j;
            for (j = 0; j < proplist.Count; j++)
            {
                if (proplist[j].Prop == prop)
                {
                    proplist[j] = (prop, proplist[j].Area + areas[i]);
                    break;
                }
            }

            if (j >= proplist.Count && proplist.Count < MaxProps)
            {
                proplist.Add((prop, areas[i]));
            }
        }

        int maxIndex = -1;
        float maxArea = 0;
        float totalArea = 0;
        for (int i = 0; i < proplist.Count; i++)
        {
            if (proplist[i].Area > maxArea)
            {
                maxIndex = i;
                maxArea = proplist[i].Area;
            }

            totalArea += proplist[i].Area;
        }

        float mass = 1.0f;
        string material = "default";
        if (maxIndex >= 0)
        {
            int prop = proplist[maxIndex].Prop;
            if (prop < 0)
            {
                prop = 0;
            }

            material = surfaceProps.GetPropName(prop) ?? "default";
            SurfacePhysics physics = surfaceProps.GetPhysicsProperties(prop);

            if (physics.Thickness != 0)
            {
                // "shell" material: area x thickness. Stock's totalArea
                // includes the placeholder areas seeded at :1384-1396.
                float area = totalArea;
                bool shell = true;
                if (!compliance.Emulates(StockQuirk.ShellMassSentinelArea))
                {
                    area = 0;
                    foreach (float faceArea in areas)
                    {
                        area += faceArea;
                    }

                    // A faceless model has no surface to weigh: take its volume.
                    shell = area > 0;
                }

                mass = shell
                    ? area * physics.Thickness * physics.Density * CubicMetersPerCubicInch
                    : totalVolume * physics.Density * CubicMetersPerCubicInch;
            }
            else
            {
                mass = totalVolume * physics.Density * CubicMetersPerCubicInch;
            }
        }

        if (mass > MaxMass)
        {
            mass = MaxMass;
        }

        return (material, mass);
    }

    /// <summary>
    /// A fluid's surface property (<c>ivp.cpp:1211-1221</c>): stock always
    /// writes <c>water</c> (<see cref="StockQuirk.FluidSurfacePropIgnored"/>);
    /// corrected, the water surface material's own property, <c>water</c>
    /// when it has none.
    /// </summary>
    /// <param name="input">The emitter input (texinfos, surface properties, compliance).</param>
    /// <param name="water">The volume.</param>
    /// <returns>The property name.</returns>
    public static string FluidSurfaceProp(PhysCollisionInput input, WaterModel water)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(water);

        if (input.Compliance.Emulates(StockQuirk.FluidSurfacePropIgnored)
            || water.SurfaceTexInfo < 0 || water.SurfaceTexInfo >= input.TexInfos.Count)
        {
            return "water";
        }

        int texData = input.TexInfos[water.SurfaceTexInfo].TexData;
        int prop = texData >= 0 && texData < input.SurfaceProperties.Count ? input.SurfaceProperties[texData] : -1;
        return prop >= 0 ? input.SurfaceProps.GetPropName(prop) ?? "water" : "water";
    }

    /// <summary>
    /// <c>TriangleNormal</c>, <c>ivp.cpp:1237</c>: <c>(p2-p0) x (p1-p0)</c>, normalised.
    /// </summary>
    /// <param name="p0">First corner.</param>
    /// <param name="p1">Second corner.</param>
    /// <param name="p2">Third corner.</param>
    /// <returns>The outward normal of a clockwise triangle.</returns>
    public static Vec3 TriangleNormal(Vec3 p0, Vec3 p1, Vec3 p2)
    {
        Vec3 e0 = p1 - p0;
        Vec3 e1 = p2 - p0;
        return Vec3.Cross(e1, e0).Normalise().Normalised;
    }

    private sealed class Context
    {
        private readonly PhysCollisionInput _in;

        public Context(PhysCollisionInput input)
        {
            _in = input;
            LeafWaterDataIds = new short[input.Leafs.Count];
            LeafContents = new int[input.Leafs.Count];
        }

        public PhysCollisionInput Input => _in;

        public List<int> WorldPropList { get; } = [];

        public short[] LeafWaterDataIds { get; }

        public int[] LeafContents { get; }

        /// <summary><c>ClearLeafWaterData</c>, <c>ivp.cpp:1475</c>.</summary>
        public void ClearLeafWaterData()
        {
            for (int i = 0; i < _in.Leafs.Count; i++)
            {
                LeafWaterDataIds[i] = -1;
                LeafContents[i] = _in.Leafs[i].Contents & ~CollisionContents.TestFogVolume;
            }
        }

        /// <summary><c>BuildWorldPhysModel</c>, <c>ivp.cpp:1314</c>.</summary>
        public (List<PhysCollisionEntry> Entries, bool VirtualTerrain, byte[]? PhysDisp) BuildWorld(ICollisionSession s)
        {
            List<PhysCollisionEntry> list = [];
            ConvertWorldBrushesToPhysCollide(s, list, NoShrink, VPhysicsMerge, CollisionContents.MaskSolid);
            ConvertWorldBrushesToPhysCollide(s, list, NoShrink, VPhysicsMerge, CollisionContents.PlayerClip);
            ConvertWorldBrushesToPhysCollide(s, list, NoShrink, VPhysicsMerge, CollisionContents.MonsterClip);

            bool noVirtualMesh = _in.NoVirtualMesh;
            if (!noVirtualMesh && _in.Displacements.Any(d => d.Core.Power > 3))
            {
                // "Map using power 4 displacements, terrain physics cannot be compressed" (ivp.cpp:1322).
                noVirtualMesh = true;
            }

            bool virtualTerrain = !noVirtualMesh && s.SupportsVirtualMesh();
            byte[]? physDisp = null;
            if (!virtualTerrain)
            {
                DispAddCollisionModels(s, list, CollisionContents.MaskSolid);
            }
            else
            {
                physDisp = DispBuildVirtualMesh(s, CollisionContents.MaskSolid);
            }

            ConvertWaterModelToPhysCollide(s, list, 0, NoShrink, VPhysicsMerge);
            return (list, virtualTerrain, physDisp);
        }

        /// <summary><c>ConvertModelToPhysCollide</c>, <c>ivp.cpp:1340</c>.</summary>
        public List<PhysCollisionEntry> BuildBrushModel(ICollisionSession s, int modelIndex)
        {
            List<PhysCollisionEntry> list = [];
            PlaneList planes = new(this, s, VPhysicsShrink, VPhysicsMerge) { ContentsMask = BrushModelMask };

            DModel model = _in.Models[modelIndex];
            VisitLeaves(planes, model.HeadNode);
            planes.AddBrushes();

            ConvertConvexParams parameters = ConvertConvexParams.Defaults with
            {
                BuildOuterConvexHull = planes.Convexes.Count > 1,
                BuildDragAxisAreas = true,
                DragAreaEpsilon = DragAreaEpsilon(model.Mins, model.Maxs),
            };

            CollideHandle collide = s.ConvertConvexToCollideParams([.. planes.Convexes], parameters);
            if (collide.IsNull)
            {
                return list;
            }

            List<int> props = [];
            List<float> areas = [];
            int? noFacesProp = null;

            // "NODRAW brushes no longer have any faces" (ivp.cpp:1388).
            if (model.NumFaces == 0)
            {
                int sideIndex = planes.GetFirstBrushSide();
                noFacesProp = PropOfTexInfo(_in.BrushSides[sideIndex].TexInfo);
            }

            for (int i = 0; i < model.NumFaces; i++)
            {
                DFace face = _in.Faces[model.FirstFace + i];
                props.Add(PropOfTexInfo(face.TexInfo));
                areas.Add(face.Area);
            }

            (string material, float mass) = MassAndMaterial(props, areas, noFacesProp, planes.TotalVolume, _in.SurfaceProps, _in.Compliance);
            list.Add(new PhysSolidEntry(TakeBytes(s, collide, out float volume), material, mass, volume));
            return list;
        }

        public int PropOfTexInfo(int texInfo) =>
            texInfo < 0 || texInfo >= _in.TexInfos.Count
                ? -1
                : PropOfTexData(_in.TexInfos[texInfo].TexData);

        private int PropOfTexData(int texData) =>
            texData >= 0 && texData < _in.SurfaceProperties.Count ? _in.SurfaceProperties[texData] : -1;

        /// <summary>The blob, the volume, and the collide destroyed: the whole life of an entry's native half.</summary>
        private static byte[] TakeBytes(ICollisionSession s, CollideHandle collide, out float volume)
        {
            volume = s.CollideVolume(collide);
            byte[] bytes = s.CollideWrite(collide);
            s.DestroyCollide(collide);
            return bytes;
        }

        private static byte[] TakeBytes(ICollisionSession s, CollideHandle collide)
        {
            byte[] bytes = s.CollideWrite(collide);
            s.DestroyCollide(collide);
            return bytes;
        }

        /// <summary><c>ConvertWorldBrushesToPhysCollide</c>, <c>ivp.cpp:1272</c>.</summary>
        private void ConvertWorldBrushesToPhysCollide(
            ICollisionSession s, List<PhysCollisionEntry> list, float shrink, float merge, int contentsMask)
        {
            PlaneList planes = new(this, s, shrink, merge) { ContentsMask = contentsMask };
            VisitLeaves(planes, _in.Models[0].HeadNode);
            planes.AddBrushes();

            if (planes.Convexes.Count == 0)
            {
                return;
            }

            CollideHandle collide = s.ConvertConvexToCollide([.. planes.Convexes]);

            // Per-triangle materials: the brush side facing the triangle's way (ivp.cpp:1286-1305).
            s.WithQueryModel(collide, query =>
            {
                int convexCount = query.ConvexCount;
                for (int i = 0; i < convexCount; i++)
                {
                    int triCount = query.TriangleCount(i);
                    int brushIndex = (int)query.GetGameData(i);
                    for (int j = 0; j < triCount; j++)
                    {
                        (Vec3 a, Vec3 b, Vec3 c) = query.GetTriangleVerts(i, j);
                        Vec3 normal = TriangleNormal(a, b, c);
                        int side = FindBrushSide(brushIndex, normal);
                        if (side >= 0 && _in.BrushSides[side].TexInfo != -1)
                        {
                            int prop = PropOfTexInfo(_in.BrushSides[side].TexInfo);
                            query.SetTriangleMaterialIndex(i, j, PropIndex(WorldPropList, prop));
                        }
                    }
                }
            });

            list.Add(new PhysStaticSolidEntry(TakeBytes(s, collide), contentsMask));
        }

        /// <summary><c>FindBrushSide</c>, <c>ivp.cpp:1249</c>: every side, bevels included, first best dot.</summary>
        private int FindBrushSide(int brushIndex, Vec3 normal)
        {
            DBrush brush = _in.Brushes[brushIndex];
            int best = -1;
            float bestDot = -1f;
            for (int i = 0; i < brush.NumSides; i++)
            {
                int sideIndex = brush.FirstSide + i;
                float dot = Vec3.Dot(normal, _in.Planes[_in.BrushSides[sideIndex].PlaneNum].Normal);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = sideIndex;
                }
            }

            return best;
        }

        /// <summary><c>ConvertWaterModelToPhysCollide</c>, <c>ivp.cpp:1165</c>.</summary>
        private void ConvertWaterModelToPhysCollide(
            ICollisionSession s, List<PhysCollisionEntry> list, int modelIndex, float shrink, float merge)
        {
            DModel model = _in.Models[modelIndex];
            foreach (WaterModel water in _in.WaterModels)
            {
                if (water.ModelIndex != modelIndex)
                {
                    continue;
                }

                PlaneList planes = new(this, s, shrink, merge)
                {
                    ContentsMask = water.Contents,
                    ClipPlane = water.HasSurface ? new CollisionPlane(water.SurfaceNormal, water.SurfaceDist) : null,
                    Compliance = _in.Compliance,
                };
                foreach (int leaf in water.Leaves)
                {
                    LeafWaterDataIds[leaf] = (short)water.FogVolumeIndex;
                    planes.ReferenceLeaf(leaf);
                }

                VisitLeaves(planes, model.HeadNode);

                // BUGBUG (stock, ivp.cpp:1196): a brush crossing the surface lands in two
                // volumes whole; corrected, AddBrushes cuts it at this one's surface.
                planes.AddBrushes();

                if (planes.Convexes.Count == 0)
                {
                    continue;
                }

                CollideHandle collide = s.ConvertConvexToCollide([.. planes.Convexes]);
                if (collide.IsNull)
                {
                    continue;
                }

                Vec3 normal = water.SurfaceNormal;
                float dist = water.SurfaceDist;
                if (!water.HasSurface)
                {
                    normal = new Vec3(0, 0, 1);
                    dist = s.CollideGetExtent(collide, Vec3.Zero, Vec3.Zero, normal).Z;
                }

                // The material override (ivp.cpp:1215) is dead: waterSurfaceTexInfoID is -1.
                list.Add(new PhysFluidEntry(TakeBytes(s, collide), FluidSurfaceProp(_in, water), 0.01f, normal, dist, water.Contents));
            }
        }

        /// <summary><c>VisitLeaves_r</c>, <c>ivp.cpp:664</c>.</summary>
        private void VisitLeaves(PlaneList planes, int node)
        {
            if (node < 0)
            {
                int leafIndex = -1 - node;
                if (planes.IsLeafReferenced(leafIndex))
                {
                    DLeaf leaf = _in.Leafs[leafIndex];
                    for (int i = 0; i < leaf.NumLeafBrushes; i++)
                    {
                        planes.ReferenceBrush(_in.LeafBrushes[leaf.FirstLeafBrush + i]);
                    }
                }

                return;
            }

            DNode n = _in.Nodes[node];
            VisitLeaves(planes, n.Children[0]);
            VisitLeaves(planes, n.Children[1]);
        }

        /// <summary><c>Disp_AddCollisionModels</c>, <c>disp_ivp.cpp:98</c>: the <c>-novirtualmesh</c> road.</summary>
        private void DispAddCollisionModels(ICollisionSession s, List<PhysCollisionEntry> list, int contentsMask)
        {
            List<(int Grid, List<int> Disps)> grids = [];
            for (int d = 0; d < _in.Displacements.Count; d++)
            {
                CollisionDisplacement disp = _in.Displacements[d];
                if ((disp.Contents & contentsMask) == 0)
                {
                    continue;
                }

                int grid = DispGridIndex(disp.Core);

                // FindOrInsertGrid searches from the end, disp_ivp.cpp:28.
                int found = -1;
                for (int g = grids.Count - 1; g >= 0; g--)
                {
                    if (grids[g].Grid == grid)
                    {
                        found = g;
                        break;
                    }
                }

                if (found < 0)
                {
                    grids.Add((grid, []));
                    found = grids.Count - 1;
                }

                grids[found].Disps.Add(d);
            }

            foreach ((_, List<int> disps) in grids)
            {
                int triCount = 0;
                PolysoupHandle soup = s.PolysoupCreate();
                foreach (int d in disps)
                {
                    CollisionDisplacement disp = _in.Displacements[d];
                    List<ushort> indices = DispTesselator.Tesselate(disp.Core);
                    int nTriCount = indices.Count / 3;
                    triCount += nTriCount;
                    if (triCount >= 65536)
                    {
                        // "don't put more than 64K tris in any single collision model"
                        CollideHandle full = s.ConvertPolysoupToCollide(soup, false);
                        if (!full.IsNull)
                        {
                            list.Add(new PhysStaticMeshEntry(TakeBytes(s, full)));
                        }

                        s.PolysoupDestroy(soup);
                        soup = s.PolysoupCreate();
                        triCount = nTriCount;
                    }

                    int nProp = PropOfTexInfo(disp.TexInfo);
                    for (int t = 0; t < nTriCount; t++)
                    {
                        float alphaTotal = 0.0f;
                        Vec3 v0 = disp.Core.Vert(indices[(t * 3) + 0]);
                        alphaTotal += disp.Core.Alphas[indices[(t * 3) + 0]];
                        Vec3 v1 = disp.Core.Vert(indices[(t * 3) + 1]);
                        alphaTotal += disp.Core.Alphas[indices[(t * 3) + 1]];
                        Vec3 v2 = disp.Core.Vert(indices[(t * 3) + 2]);
                        alphaTotal += disp.Core.Alphas[indices[(t * 3) + 2]];

                        int prop = nProp;
                        if (alphaTotal > DispAlphaPropDelta && disp.SurfaceProp2 != -1)
                        {
                            prop = disp.SurfaceProp2;
                        }

                        s.PolysoupAddTriangle(soup, v0, v1, v2, PropIndex(WorldPropList, prop));
                    }
                }

                CollideHandle collide = s.ConvertPolysoupToCollide(soup, false);
                if (!collide.IsNull)
                {
                    list.Add(new PhysStaticMeshEntry(TakeBytes(s, collide)));
                }

                s.PolysoupDestroy(soup);
            }
        }

        /// <summary><c>Disp_BuildVirtualMesh</c>, <c>disp_ivp.cpp:274</c>: LUMP_PHYSDISP.</summary>
        private byte[] DispBuildVirtualMesh(ICollisionSession s, int contentsMask)
        {
            byte[]?[] blobs = new byte[]?[_in.Displacements.Count];
            for (int i = 0; i < _in.Displacements.Count; i++)
            {
                CollisionDisplacement disp = _in.Displacements[i];
                if ((disp.Contents & contentsMask) == 0)
                {
                    continue;
                }

                VirtualMeshSource mesh = DispMeshEvent(disp.Core, i);
                CollideHandle collide = s.CreateVirtualMesh(mesh, buildOuterHull: true);
                blobs[i] = collide.IsNull ? null : TakeBytes(s, collide);
            }

            return PhysDispLump.Write(blobs);
        }
    }

    /// <summary><c>DISP_ALPHA_PROP_DELTA</c>, <c>builddisp.h:25</c>.</summary>
    public const float DispAlphaPropDelta = 382.5f;

    /// <summary>
    /// What <c>CDispMeshEvent</c> serves (<c>disp_ivp.cpp:212-233</c>): the
    /// tessellated indices REVERSED end for end, and the vertices up to the
    /// highest index used.
    /// </summary>
    /// <param name="core">The displacement.</param>
    /// <param name="dispIndex">Its index, for the diagnostic.</param>
    /// <returns>The mesh.</returns>
    /// <exception cref="MapCompileException">A triangle has two equal corners (<c>disp_ivp.cpp:296-307</c>).</exception>
    public static VirtualMeshSource DispMeshEvent(CoreDispInfo core, int dispIndex)
    {
        ArgumentNullException.ThrowIfNull(core);
        List<ushort> indices = DispTesselator.Tesselate(core);

        // "validate the collision data": stock Error()s out of the compile.
        for (int j = 0; j < indices.Count / 3; j++)
        {
            Vec3 v0 = core.Vert(indices[j * 3]);
            Vec3 v1 = core.Vert(indices[(j * 3) + 1]);
            Vec3 v2 = core.Vert(indices[(j * 3) + 2]);
            if (v0 == v1 || v1 == v2 || v2 == v0)
            {
                throw new MapCompileException(
                    $"Displacement {dispIndex} has bad geometry near {v0.X:F2} {v0.Y:F2} {v0.Z:F2}: "
                    + "Can't compile displacement physics.");
            }
        }

        int maxIndex = 0;
        foreach (ushort index in indices)
        {
            if (index > maxIndex)
            {
                maxIndex = index;
            }
        }

        ushort[] reversed = [.. indices];
        for (int i = 0; i < reversed.Length / 2; i++)
        {
            (reversed[i], reversed[reversed.Length - i - 1]) = (reversed[reversed.Length - i - 1], reversed[i]);
        }

        Vec3[] verts = new Vec3[maxIndex + 1];
        for (int i = 0; i < verts.Length; i++)
        {
            verts[i] = core.Vert(i);
        }

        return new VirtualMeshSource(verts, reversed);
    }

    /// <summary>
    /// <c>Disp_GridIndex</c>, <c>disp_ivp.cpp:50</c>: the displacement's
    /// bounding-box centre hashed into a 4096 x 4096 x 8192 grid.
    /// </summary>
    /// <param name="core">The displacement.</param>
    /// <returns><c>MAKEID( gridX, gridY, gridZ, 0 )</c>.</returns>
    /// <remarks>
    /// The box is node 0's (<see cref="CoreDispInfo.RootBounds"/>), fixed at
    /// <c>Create</c> time: vertices snapped afterwards do not move it, in
    /// stock or here.
    /// </remarks>
    public static int DispGridIndex(CoreDispInfo core)
    {
        ArgumentNullException.ThrowIfNull(core);
        DispBox box = core.RootBounds;
        Vec3 center = 0.5f * (box.Min + box.Max);
        center += new Vec3(CollisionContents.MaxCoordInteger, CollisionContents.MaxCoordInteger, CollisionContents.MaxCoordInteger);
        int gridX = (int)(center.X / 4096) & 0xFF;
        int gridY = (int)(center.Y / 4096) & 0xFF;
        int gridZ = (int)(center.Z / 8192) & 0xFF;
        return gridX | (gridY << 8) | (gridZ << 16);
    }

    /// <summary><c>CPlaneList</c>, <c>ivp.cpp:413</c>.</summary>
    private sealed class PlaneList
    {
        private readonly Context _context;
        private readonly ICollisionSession _s;
        private readonly bool[] _brushAdded;
        private readonly List<int> _leafList = [];

        public PlaneList(Context context, ICollisionSession session, float shrink, float merge)
        {
            _context = context;
            _s = session;
            Shrink = shrink;
            Merge = merge;
            _brushAdded = new bool[context.Input.Brushes.Count];
        }

        public int ContentsMask { get; init; } = CollisionContents.MaskSolid;

        /// <summary>A water volume's surface, for <see cref="StockQuirk.WaterBrushNotClippedAtSurface"/>; null elsewhere.</summary>
        public CollisionPlane? ClipPlane { get; init; }

        /// <summary>The compile's compliance.</summary>
        public ComplianceOptions Compliance { get; init; } = ComplianceOptions.Correct;

        public float Shrink { get; }

        public float Merge { get; }

        public List<ConvexHandle> Convexes { get; } = [];

        public float TotalVolume { get; private set; }

        private PhysCollisionInput In => _context.Input;

        public void ReferenceBrush(int brush)
        {
            if ((In.Brushes[brush].Contents & ContentsMask) == 0)
            {
                return;
            }

            _brushAdded[brush] = true;
        }

        public void ReferenceLeaf(int leaf) => _leafList.Add(leaf);

        public bool IsLeafReferenced(int leaf) => _leafList.Count == 0 || _leafList.Contains(leaf);

        /// <summary><c>AddBrushes</c>, <c>ivp.cpp:534</c>.</summary>
        public int AddBrushes()
        {
            int count = 0;
            for (int brush = 0; brush < _brushAdded.Length; brush++)
            {
                if (!_brushAdded[brush])
                {
                    continue;
                }

                ConvexHandle convex;
                if (Shrink != 0)
                {
                    // "Make sure shrinking won't swallow this brush."
                    ConvexHandle unshrunk = BuildConvexForBrush(brush, 0, default, 0);
                    CollideHandle test = _s.ConvertConvexToCollide([unshrunk]);
                    convex = BuildConvexForBrush(brush, Shrink, test, Shrink * 3);
                    _s.DestroyCollide(test);
                }
                else
                {
                    convex = BuildConvexForBrush(brush, Shrink, default, 1.0f);

                    // A water brush reaching above its volume's surface is cut
                    // there -- stock's UNDONE (ivp.cpp:1197-1199) -- unless the
                    // stock defect is being reproduced.
                    if (ClipPlane is CollisionPlane clip && !convex.IsNull
                        && !Compliance.Emulates(StockQuirk.WaterBrushNotClippedAtSurface))
                    {
                        CollideHandle test = _s.ConvertConvexToCollide([convex]);
                        Vec3 top = _s.CollideGetExtent(test, Vec3.Zero, Vec3.Zero, clip.Normal);
                        _s.DestroyCollide(test);
                        bool crosses = Vec3.Dot(top, clip.Normal) > clip.Dist + Merge;
                        convex = BuildConvexForBrush(brush, Shrink, default, 1.0f, crosses ? clip : null);
                    }
                }

                if (!convex.IsNull)
                {
                    count++;
                    _s.SetConvexGameData(convex, (uint)brush);
                    TotalVolume += _s.ConvexVolume(convex);
                    Convexes.Add(convex);
                }
            }

            return count;
        }

        /// <summary><c>GetFirstBrushSide</c>, <c>ivp.cpp:567</c>.</summary>
        public int GetFirstBrushSide()
        {
            for (int brush = 0; brush < _brushAdded.Length; brush++)
            {
                if (!_brushAdded[brush])
                {
                    continue;
                }

                DBrush b = In.Brushes[brush];
                for (int i = 0; i < b.NumSides; i++)
                {
                    int sideIndex = i + b.FirstSide;
                    if (In.BrushSides[sideIndex].Bevel == 0)
                    {
                        return sideIndex;
                    }
                }
            }

            return 0;
        }

        /// <summary><c>BuildConvexForBrush</c>, <c>ivp.cpp:492</c>.</summary>
        private ConvexHandle BuildConvexForBrush(int brush, float shrink, CollideHandle collideTest, float shrinkMinimum, CollisionPlane? extraPlane = null)
        {
            DBrush b = In.Brushes[brush];
            List<CollisionPlane> planes = new(32);
            IReadOnlyList<bool>? visible = In.SideVisible is { } all && brush < all.Count ? all[brush] : null;

            for (int i = 0; i < b.NumSides; i++)
            {
                DBrushSide side = In.BrushSides[i + b.FirstSide];
                if (side.Bevel != 0)
                {
                    continue;
                }

                DPlane plane = In.Planes[side.PlaneNum];
                float shrinkThisPlane = shrink;

                // "don't shrink brush sides with no visible components" (ivp.cpp:505).
                if (In.SideVisible is not null && visible is not null && i < visible.Count && !visible[i])
                {
                    shrinkThisPlane = 0;
                }

                // "Make sure shrinking won't swallow geometry along this axis."
                if (!collideTest.IsNull && shrinkThisPlane != 0)
                {
                    Vec3 start = _s.CollideGetExtent(collideTest, Vec3.Zero, Vec3.Zero, plane.Normal);
                    Vec3 end = _s.CollideGetExtent(collideTest, Vec3.Zero, Vec3.Zero, -plane.Normal);
                    float thick = Vec3.Dot(end - start, plane.Normal);
                    if (MathF.Abs(thick) < shrinkMinimum)
                    {
                        shrinkThisPlane = 0;
                    }
                }

                planes.Add(new CollisionPlane(plane.Normal, plane.Dist - shrinkThisPlane));
            }

            if (extraPlane is { } extra)
            {
                planes.Add(extra);
            }

            return _s.ConvexFromPlanes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(planes), Merge);
        }
    }
}

/// <summary>The contents bits and constants the collision code reads (<c>bspflags.h</c>, <c>worldsize.h</c>).</summary>
public static class CollisionContents
{
    /// <summary><c>CONTENTS_SOLID</c>.</summary>
    public const int Solid = 0x1;

    /// <summary><c>CONTENTS_WINDOW</c>.</summary>
    public const int Window = 0x2;

    /// <summary><c>CONTENTS_GRATE</c>.</summary>
    public const int Grate = 0x8;

    /// <summary><c>CONTENTS_SLIME</c>.</summary>
    public const int Slime = 0x10;

    /// <summary><c>CONTENTS_WATER</c>.</summary>
    public const int Water = 0x20;

    /// <summary><c>CONTENTS_TESTFOGVOLUME</c>, cleared from every leaf by <c>ClearLeafWaterData</c>.</summary>
    public const int TestFogVolume = 0x100;

    /// <summary><c>CONTENTS_MOVEABLE</c>.</summary>
    public const int Moveable = 0x4000;

    /// <summary><c>CONTENTS_PLAYERCLIP</c>.</summary>
    public const int PlayerClip = 0x10000;

    /// <summary><c>CONTENTS_MONSTERCLIP</c>.</summary>
    public const int MonsterClip = 0x20000;

    /// <summary><c>CONTENTS_MONSTER</c>.</summary>
    public const int Monster = 0x2000000;

    /// <summary><c>MASK_SOLID</c> = 33570827.</summary>
    public const int MaskSolid = Solid | Moveable | Window | Monster | Grate;

    /// <summary><c>MASK_WATER</c>.</summary>
    public const int MaskWater = Water | Moveable | Slime;

    /// <summary><c>MAX_COORD_INTEGER</c>, <c>worldsize.h:19</c>.</summary>
    public const int MaxCoordInteger = 16384;
}
