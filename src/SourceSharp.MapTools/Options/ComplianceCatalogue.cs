using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace SourceSharp.MapTools.Options;

/// <summary>
/// The three stock tools, as a set.
/// </summary>
[Flags]
public enum CompileTools
{
    /// <summary>No tool.</summary>
    None = 0,

    /// <summary><c>vbsp</c>.</summary>
    Vbsp = 1,

    /// <summary><c>vvis</c>.</summary>
    Vvis = 2,

    /// <summary><c>vrad</c>.</summary>
    Vrad = 4,
}

/// <summary>
/// One <see cref="StockQuirk"/>, described as data: what it is, what the
/// reference implementation does, which tools it moves, and where this
/// implementation decides it.
/// </summary>
/// <param name="Quirk">The quirk.</param>
/// <param name="Summary">One sentence: what the reference implementation does wrong.</param>
/// <param name="Tools">The tools whose output the quirk can move.</param>
/// <param name="ManagedSites">
/// Every managed method that decides this quirk, as
/// <c>Namespace.Type.Method</c>. The compiler-generated bodies of async
/// methods, lambdas and local functions are named by the method they belong
/// to. A fact scans the built assembly and requires this list to be exactly
/// the set of methods that pass this quirk to
/// <see cref="ComplianceOptions.Emulates"/>.
/// </param>
/// <param name="Note">
/// Anything a user choosing a policy should know that does not fit the
/// summary, or null.
/// </param>
public sealed record ComplianceQuirkInfo(
    StockQuirk Quirk,
    string Summary,
    CompileTools Tools,
    ImmutableArray<string> ManagedSites,
    string? Note = null);

/// <summary>
/// Every <see cref="StockQuirk"/>, described. What <c>-listcompliance</c>
/// prints.
/// </summary>
/// <remarks>
/// <para>
/// <b>Completeness is enforced, not hoped for.</b> <see cref="Describe"/> is a
/// switch with a throwing default arm, and a fact walks every enum member
/// through it, so a new <see cref="StockQuirk"/> without an entry here fails the
/// suite. A second fact scans the built assembly and
/// checks <see cref="ComplianceQuirkInfo.ManagedSites"/> against the methods
/// that actually consult the switch.
/// </para>
/// <para>
/// No static state: <see cref="All"/> is rebuilt on every call, so the
/// catalogue cannot be mutated by one compile under another.
/// </para>
/// </remarks>
public static class ComplianceCatalogue
{
    /// <summary>Every quirk's entry, in enum order.</summary>
    public static IReadOnlyList<ComplianceQuirkInfo> All =>
        [.. Enum.GetValues<StockQuirk>().Select(Describe)];

    /// <summary>The entry for one quirk.</summary>
    /// <param name="quirk">The quirk.</param>
    /// <returns>Its entry.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="quirk"/> has no entry. That is a bug in this file, and
    /// <c>ComplianceCatalogueTests</c> exists to catch it first.
    /// </exception>
    public static ComplianceQuirkInfo Describe(StockQuirk quirk) => quirk switch
    {
        StockQuirk.BaseWindingNormalise => new(
            quirk,
            "BaseWindingForPlane normalises with the rsqrtss estimate plus one Newton step, "
            + "not a divide, and a zero-epsilon clip then keeps or drops slivers on its last bits.",
            CompileTools.Vbsp | CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Geometry.WindingArena.BaseWindingForPlane",
            ],
            "The Stock side is not reproducible across CPU models: rsqrtss may differ between "
            + "them. vrad's own VectorNormalize calls are VradVectorNormalise."),

        StockQuirk.EdgeBevelNormalise => new(
            quirk,
            "AddBrushBevels normalises the edge and the candidate bevel normal with the rsqrtss "
            + "estimate, so a normal whose |x| and |z| are equal is stored as the wrong plane type.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.MapFile.get_StockEdgeBevels",
            ],
            "The same rule applies to the bevel of the candidate normal itself. The PLANES lump "
            + "matches the reference implementation only when this and BaseWindingNormalise are "
            + "both on the Stock side."),

        StockQuirk.AreaportalLeakWalk => new(
            quirk,
            "ReportAreaportalLeak's `continue` skips the assignment of s, so the walk steps "
            + "into the other node's portal list and the leak report names the wrong portals.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Portals.AreaFlood.ReportAreaportalLeak",
            ]),

        StockQuirk.WindingIsTinyEdgePromotion => new(
            quirk,
            "EDGE_LENGTH is the double 0.2, so WindingIsTiny counts an edge of exactly 0.2f "
            + "as long.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Portals.TreePortals.IsTiny",
                "SourceSharp.MapTools.Bsp.Csg.BrushGeometry.WindingIsTiny",
            ]),

        StockQuirk.LeakFileUnnudgedOrigin => new(
            quirk,
            "The .lin file ends at the entity's origin key, not at the origin + 1z the flood "
            + "actually started from.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Portals.LeakTrace.Trace",
            ]),

        StockQuirk.AddQuadSecondTriangleId => new(
            quirk,
            "AddQuad tags its second triangle id + 1, so half of every static prop's box "
            + "shadow is attributed to the next prop.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.ShadowCasterBuilder.AddQuad",
            ]),

        StockQuirk.LeafAmbientSampleCountAxes => new(
            quirk,
            "ComputeAmbientForLeaf clamps ySize and zSize to xSize, so a leaf's candidate "
            + "sample count is xSize cubed whatever its shape.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Ambient.LeafAmbientBuilder.CandidateSampleCount",
            ]),

        StockQuirk.AmbientCubeReciprocalEstimate => new(
            quirk,
            "The leaf-ambient surface-light term uses the rcpss and rsqrtss estimates, so "
            + "which lights reach the ambient cubes, and how bright they are, depends on the CPU.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Ambient.LeafAmbientBuilder.BuildAsync",
                "SourceSharp.MapTools.Rad.Ambient.TracerLineVisibility..ctor",
                "SourceSharp.MapTools.Rad.Ambient.AmbientCube.AddEmitSurfaceLights",
            ],
            "The reference implementation’s inverse-length helper is the estimate, not a divide."),

        StockQuirk.AmbientSampleTieBreakNeverFires => new(
            quirk,
            "The ambient sample list's tie-break compares against a running total that is never "
            + "assigned, so on a distance tie the earlier sample is always evicted.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Ambient.AmbientSampleList.Add",
            ]),

        StockQuirk.DispVertNormalise => new(
            quirk,
            "A displacement vertex's stored direction is normalised with the rsqrtss estimate, "
            + "so LUMP_DISP_VERTS depends on the CPU and is not a unit vector.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Disp.DisplacementLumpBuilder.Build",
                "SourceSharp.MapTools.Disp.DisplacementLumpBuilder.ComputeDispInfoBounds",
                "SourceSharp.MapTools.Disp.DispLightingLoader.Load",
                "SourceSharp.MapTools.Bsp.Detail.MapDisplacementSurfaces.PositionOnSurface",
            ],
            "The displacement surface loader reads the same table."),

        StockQuirk.DispLightmapSwapDropped => new(
            quirk,
            "A displacement's lightmap-axis swap repoints only the map face, after the BSP face "
            + "was emitted, so the swapped texinfo never reaches LUMP_FACES and is compacted away.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Disp.DispVbspHooks.FaceTexInfos",
            ],
            "The face record is written before the swap would run, so the swap is too late to land."),

        StockQuirk.DispWorldBoundsBaseQuad => new(
            quirk,
            "A displacement adds its flat base quad (puffed 0.1) to world_mins/world_maxs, not "
            + "its displaced surface, so its height never counts.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Disp.DisplacementLumpBuilder.Build",
                "SourceSharp.MapTools.Disp.DisplacementLumpBuilder.ComputeDispInfoBounds",
            ],
            "The reference leak-bounds walk reads this box. The compile takes the box from the lump "
            + "build's own cores (DisplacementLumpBuilder.Build, plan 3p); ComputeDispInfoBounds is "
            + "the same rule on a fresh core."),

        StockQuirk.DispVertexNormalMeanUnnormalised => new(
            quirk,
            "A displacement vertex normal is the mean of unit fan normals and is never "
            + "renormalised, so crease normals are short and weigh less in vrad's blends.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Disp.DisplacementLumpBuilder.Build",
                "SourceSharp.MapTools.Disp.DispLightingLoader.Load",
            ],
            "vbsp writes no normal; vrad blends one in when it samples the surface."),

        StockQuirk.WaterLeafSortTie => new(
            quirk,
            "IsLowerLeaf's near-equal branch returns true either way, so equal water surfaces "
            + "sort in reverse discovery order rather than by distance.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Collision.WaterVolumeBuilder.IsLowerLeaf",
            ]),

        StockQuirk.FluidSurfacePropIgnored => new(
            quirk,
            "A fluid's surface property is always \"water\": the material override reads a "
            + "texinfo id hard-coded to -1.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Collision.PhysCollisionEmitter.FluidSurfaceProp",
            ]),

        StockQuirk.ShellMassSentinelArea => new(
            quirk,
            "A brush entity's shell mass counts proplist's placeholder areas (1, and 2 for a "
            + "faceless model) as surface.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Collision.PhysCollisionEmitter.MassAndMaterial",
            ]),

        StockQuirk.WaterBrushNotClippedAtSurface => new(
            quirk,
            "A brush crossing a water surface is added whole to the fluid's collision, "
            + "so the fluid reaches above its own surface.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Collision.PhysCollisionEmitter+PlaneList.AddBrushes",
            ]),

        StockQuirk.NodeAreaWrittenBeforeSet => new(
            quirk,
            "The node emit copies the area into LUMP_NODES before the area-index pass has set "
            + "it, so every dnode_t::area is 0.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Write.AreaPortalEmitter.Emit",
            ],
            "The reference implementation assigns node areas at the end of the area-portal emit."),

        StockQuirk.FogVolumeLoopOverNoFaces => new(
            quirk,
            "WriteFogVolumeIDs loops up to firstface + numfaces while numfaces is still 0, so no face "
            + "gets a fog volume and no water top is retextured.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Write.WaterVolumes.WriteFogVolumeIds",
            ],
            "The reference model-close step is what sets the face count."),

        StockQuirk.InverseQuadraticReciprocal => new(
            quirk,
            "The falloff solve for _fifty_percent_distance lights divides by the determinant "
            + "through a reciprocal (the /fp:fast binary), so the attenuation terms differ in "
            + "their last bits from the three divides the source writes.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.DirectLightBuilder.ApplyFalloff",
            ],
            "Observed in stock's LUMP_WORLDLIGHTS on p4c's texlight fixture; a C probe of "
            + "both forms reproduces stock only with the reciprocal."),

        StockQuirk.CrtCosineAtRightAngle => new(
            quirk,
            "A light's direction takes sin(pi) = 1.2246469e-16 as the cosine of a right angle, "
            + "where cos(pi/2) rounds to 6.123234e-17: a spot pointing straight down differs.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.DirectLightBuilder.ParseGeneric",
            ],
            "Same for the pitch: the C runtime's cosine, not a table."),

        StockQuirk.VradVectorNormalise => new(
            quirk,
            "vrad's own normals (PairEdges corners, phong, bump basis, light targets) are "
            + "normalised with the rsqrtss estimate, not a divide.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.LightGeometry.get_StockNormalise",
                "SourceSharp.MapTools.Rad.Light.DirectLightingSettings.get_StockNormalise",
                "SourceSharp.MapTools.Rad.Light.DirectLightBuilder.ParseGeneric",
                "SourceSharp.MapTools.Rad.Final.LuxelDensity.Apply",
                "SourceSharp.MapTools.Rad.Props.StaticPropLighting.LightModel",
            ],
            "The same estimate reaches bump-vector construction, the static prop's push toward "
            + "the light, form factors, and the direct-light gather."),

        StockQuirk.GatherReciprocalEstimate => new(
            quirk,
            "The direct-light gather, the four-wide phong normal and the skybox recursion divide "
            + "with the rcpps/rsqrtps estimates plus one Newton step, so lightmaps depend on the CPU.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.DirectLightGatherer..ctor",
                "SourceSharp.MapTools.Rad.Light.LightGeometry.get_StockEstimates",
                "SourceSharp.MapTools.Rad.Props.PropLightSampler..ctor",
            ],
            "The reference reciprocal / reciprocal-sqrt helpers are the estimate. Detail and "
            + "static prop lighting call the same gather, and a trace direction is under it too."),

        StockQuirk.SpotExponentQuarterSteps => new(
            quirk,
            "A spotlight's _exponent is applied through PowSIMD's fixed point with two fractional "
            + "bits, so 1.3 behaves as 1.25.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.DirectLightGatherer..ctor",
            ],
            "The reference SIMD power and its fixed-point exponent share the exponent trick."),

        StockQuirk.SkyboxRecursionFromLaneZero => new(
            quirk,
            "A sky ray's 3D-skybox recursion is decided by the first of four samples' leaf for "
            + "all four.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.DirectLightGatherer..ctor",
            ]),

        StockQuirk.SkyProbeTailDoubleCount => new(
            quirk,
            "CanLeafTraceToSky clamps the last group of four directions to index 161, casting "
            + "g_anorms[161] three times.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.RadWorld.CanLeafTraceToSky",
            ],
            "Changes the ray count only: the answer is 'any ray reached sky'."),

        StockQuirk.SecondSunSpreadAngleWins => new(
            quirk,
            "A second light_environment, whose light is discarded, still overwrites the sun's "
            + "SunSpreadAngle.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.DirectLightBuilder.ParseLightEnvironment",
            ]),

        StockQuirk.MonotonicDerivativeAtOne => new(
            quirk,
            "The monotonic falloff fit tests the slope at x = 1 (2a + b) where its comment says "
            + "the start point.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.DirectLightBuilder.ApplyFalloff",
            ]),

        StockQuirk.CubemapDistanceNormalise => new(
            quirk,
            "The nearest env_cubemap in front of a specular side is chosen on a distance from the "
            + "rsqrtss estimate, so a near tie can pick a different cubemap patch on another CPU.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Cubemaps.CubemapFixups.FindClosestCubemap",
            ],
            "The reference fallback loop uses a full length and is exact either way."),

        StockQuirk.DetailOrientationNormalise => new(
            quirk,
            "A conforming detail prop's tangent basis is normalised with the rsqrtss estimate, "
            + "so the angles stored in dprp depend on the CPU.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Detail.DetailPropEmitter.ConformingAngles",
            ],
            "The same swap appears in the two detail-object cross products."),

        StockQuirk.CubemapIgnoresPatchMaterials => new(
            quirk,
            "A patch VMT is read raw when vbsp asks whether a material has $envmap, so a patched "
            + "specular material never gets its per-cubemap patch.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.MaterialPatch.MaterialPatcher.LoadOriginalAsync",
            ],
            "Material patch construction reads the same field three ways."),

        StockQuirk.PatchExpandInsertDropsReplace => new(
            quirk,
            "ExpandPatchFile looks the replace section up in the included material once an insert "
            + "has been applied, so a patch with both sections loses its replace.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.MaterialPatch.MaterialPatcher.ExpandPatchAsync",
            ]),

        StockQuirk.PatchExpandEmptyPatchNeverResolves => new(
            quirk,
            "ExpandPatchFile never resolves a patch with neither insert nor replace; it stays a "
            + "patch after ten passes.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.MaterialPatch.MaterialPatcher.ExpandPatchAsync",
            ]),

        StockQuirk.CubemapUnreferencedNeverMatches => new(
            quirk,
            "Cubemap_AddUnreferencedCubemaps compares a texture name with file names, never matches, "
            + "and re-adds every sample; the pak writer hides the duplicates.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Cubemaps.CubemapFixups.AddUnreferencedCubemaps",
            ]),

        StockQuirk.OverlayFaceLimitOffByOne => new(
            quirk,
            "An overlay face list that exactly fills its 64 (water: 256) slots is refused.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Overlays.OverlaySet.TooMany",
            ],
            "Water overlays take the same path."),

        StockQuirk.OverlayMaterialNotReplaced => new(
            quirk,
            "-replacematerials is not applied to an info_overlay's material, although it is to "
            + "brush sides and water overlays.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Overlays.OverlaySet.AddFromEntity",
            ]),

        StockQuirk.StudioVersionSlam => new(
            quirk,
            "Every studio model version is overwritten with 48 before it is checked, so a model of "
            + "an incompatible version is read as garbage instead of refused.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Props.StudioModelCheck.LoadAsync",
            ],
            "The reference header declares the field without the bits; the liveness check misses them."),


        StockQuirk.IndirectSurfaceEnumeratorReused => new(
            quirk,
            "ComputeIndirectLightingAtPoint reuses one CLightSurface across its sample rays and "
            + "never resets m_HitFrac, so each ray ignores leaf and displacement hits beyond the "
            + "previous ray's hit.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Props.PropIndirectLighting.Compute",
            ]),

        StockQuirk.StaticPropBadVertexDropsPropFlags => new(
            quirk,
            "A static prop vertex embedded in solid is relit with default arguments, dropping "
            + "the prop's IGNORE_NORMALS and NO_SELF_SHADOWING flags for that vertex only.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Props.StaticPropLighting.LightModel",
            ]),

        StockQuirk.FormFactorSineAboveOne => new(
            quirk,
            "A polygon form factor whose edge sine rounds past 1 is discarded whole, and the "
            + "patch-to-patch transfer with it.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Bounce.FormFactors.PolyToDiff",
            ],
            "Correct clamps the sine to 1. The reference transfer step drops a form factor <= 0."),

        StockQuirk.TransferRayReciprocalEstimate => new(
            quirk,
            "The patch-to-patch visibility rays are normalised with the rcpps estimate plus a "
            + "Newton step, not a divide.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Bounce.VisMatrix.MakeRays",
            ],
            "The reference reciprocal helper is the estimate: machine-dependent on the Stock side."),

        StockQuirk.VisPlaneTestPhongNormal => new(
            quirk,
            "TestPatchToPatch tests a smoothed patch's phong normal against its flat plane's "
            + "distance, a plane tilted about the world origin.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Bounce.VisMatrix.get_StockPlaneTest",
            ],
            "Correct uses the patch's plane normal. No effect on a face that is not smoothed."),

        StockQuirk.DispFastSamplesPastEdge => new(
            quirk,
            "-fast samples a displacement's luxels half a luxel off and its last row and column "
            + "off the surface, which leaves them black.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Displacement.DispSampleBuilder.BuildSamplesAndLuxelsFast",
            ],
            "The reference displacement surface lookup returns early past u = 1."),

        StockQuirk.DispFastSampleAreaZero => new(
            quirk,
            "-fast never sets a displacement sample's area, so its patches get no direct light "
            + "and it reflects nothing.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Displacement.DispSampleBuilder.BuildSamplesAndLuxelsFast",
            ],
            "The reference sample weight is area-weighted; the flat fast path sets it directly."),

        StockQuirk.SampleRadialEdgeOffByOne => new(
            quirk,
            "SampleRadial's edge test lets u == w and v == h through and reads the next row's "
            + "first luxel, or the zeroed tail of the grid.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Final.LuxelRadial.IsOffGrid",
            ]),

        StockQuirk.PatchRadialNeighbourBumpFromSelf => new(
            quirk,
            "The bounce filter takes a neighbour's bumpiness from the face being filtered, so an "
            + "unbumped neighbour's zero bump light darkens a bumped face's bump maps.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Final.FinalLightFace.NeighbourPatchBumpmap",
            ]),

        StockQuirk.FastFinalLightStyleZero => new(
            quirk,
            "Under -fast every light style of a face is written from style slot zero.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Final.FinalLightFace.FastLuxel",
            ]),

        StockQuirk.DegreesToRadiansByReciprocal => new(
            quirk,
            "A light's angles are converted with the float reciprocal of 180 (the /fp:fast "
            + "binary), not a division: ss_sandbox's sun direction moves by six float ulps.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.DirectLightBuilder.ParseGeneric",
                "SourceSharp.MapTools.Rad.Light.DirectLightBuilder.ParseLightSpot",
            ],
            "The same estimate is used for the pitch and for spot cones."),

        StockQuirk.SupersampleGradientReadsUninitialised => new(
            quirk,
            "The supersampling gradient compares edge samples with the uninitialised intensity of "
            + "luxels that have no sample, so which edges are supersampled depends on stack garbage.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.FaceLightJob.SupersampleReadsUninitialised",
            ],
            "Undefined behaviour: the Stock side is a model (always supersample), not a reproduction."),

        StockQuirk.LuxelDensityLeavesHdrFacesStale => new(
            quirk,
            "-luxeldensity recomputes the extents of the LDR faces only, so an -hdr pass lights "
            + "faces whose extents belong to the old lightmap axes.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Final.LuxelDensity.StaleHdrFaces",
            ]),

        StockQuirk.CollisionCookerSinglePrecision => new(
            quirk,
            "The reference collision cooker computes in float with the rsqrtss/rsqrtps estimates, "
            + "so the physics lump depends on the CPU; the later reference build cooks in double.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Phys.Managed.ManagedCollisionCooker.Create",
            ],
            "The reference’s internal physics width is float in the earlier build and double "
            + "in the later one. Stock = the earlier arithmetic, correct = the later (plan Q18)."),

        StockQuirk.CollisionInertiaZeroLengthEdge => new(
            quirk,
            "The reference inertia integral divides 0 by 0 on a zero-length ledge edge and writes "
            + "NaN into rotation_inertia (6 of dm_lockdown's 2,239 brushes).",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Phys.Managed.ManagedCollisionCooker.Create",
            ],
            "Inside the reference inertia solver: the per-axis divide is taken on NaN."),

        StockQuirk.CollisionPolysoupMaterialOverrun => new(
            quirk,
            "The polysoup conversion's material fix-up runs past a ledge whose first triangle "
            + "has material 0 and clears the exponent bits of its first points' x coordinates.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Phys.Managed.ManagedCollisionCooker.Create",
            ],
            "Inside the reference polysoup conversion’s material fix-up. Only the "
            + "-novirtualmesh / power-4 displacement road reaches it."),

        StockQuirk.KdZeroDirectionReachCut => new(
            quirk,
            "The KD tracer saturates a zero direction component to FLT_EPSILON, so an "
            + "axis-parallel ray starting just inside a plane is cut short and misses what lies "
            + "beyond.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Tracing.KdRayTracer.ZeroSubstitute",
            ],
            "The reference saturates a zero component to FLT_EPSILON. Correct substitutes "
            + "2^-60. Only rays with an exactly zero direction component are affected."),

        _ => throw new ArgumentOutOfRangeException(
            nameof(quirk), quirk, "no ComplianceCatalogue entry for this StockQuirk"),
    };

    /// <summary>
    /// Renders the catalogue the way <c>-listcompliance</c> prints it.
    /// </summary>
    /// <param name="tool">
    /// The tool that was asked. Its quirks are listed first. The rest follow
    /// under their own heading, because an earlier stage's quirk is baked into
    /// the map a later stage reads.
    /// </param>
    /// <returns>The text, lines separated by <c>\n</c>, ending in a newline.</returns>
    public static string Format(CompileTools tool)
    {
        StringBuilder text = new();
        IReadOnlyList<ComplianceQuirkInfo> all = All;

        List<ComplianceQuirkInfo> mine = [.. all.Where(q => (q.Tools & tool) != 0)];
        List<ComplianceQuirkInfo> others = [.. all.Where(q => (q.Tools & tool) == 0)];

        text.Append(CultureInfo.InvariantCulture,
            $"-compliance correct|stock (default correct). {mine.Count} of {all.Count} stock quirks affect {Name(tool)}:\n");
        AppendAll(text, mine);

        if (others.Count > 0)
        {
            text.Append("Other tools' quirks (already in any map this tool reads):\n");
            AppendAll(text, others);
        }

        return text.ToString();
    }

    private static void AppendAll(StringBuilder text, List<ComplianceQuirkInfo> quirks)
    {
        if (quirks.Count == 0)
        {
            text.Append("  (none)\n");
            return;
        }

        foreach (ComplianceQuirkInfo q in quirks)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {q.Quirk} [{Name(q.Tools)}]\n");
            text.Append(CultureInfo.InvariantCulture, $"      {q.Summary}\n");

            if (q.Note is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $"      Note: {q.Note}\n");
            }
        }
    }

    private static string Name(CompileTools tools)
    {
        List<string> names = [];

        if ((tools & CompileTools.Vbsp) != 0)
        {
            names.Add("vbsp");
        }

        if ((tools & CompileTools.Vvis) != 0)
        {
            names.Add("vvis");
        }

        if ((tools & CompileTools.Vrad) != 0)
        {
            names.Add("vrad");
        }

        return names.Count == 0 ? "none" : string.Join(",", names);
    }
}
