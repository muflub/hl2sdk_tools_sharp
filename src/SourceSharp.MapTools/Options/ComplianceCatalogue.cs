//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

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
/// How a <see cref="StockQuirk"/>'s difference was established.
/// </summary>
public enum QuirkObservation
{
    /// <summary>
    /// Compared against stock's own output (a stock-compiled BSP, log or
    /// file): only the <see cref="CompliancePolicy.Stock"/> side reproduces it.
    /// </summary>
    Measured,

    /// <summary>
    /// A fact runs both sides on constructed input and shows them disagree;
    /// no stock output was compared.
    /// </summary>
    Demonstrated,

    /// <summary>
    /// Read from the reference implementation. No fact yet shows the two
    /// sides disagree.
    /// </summary>
    ReadFromReference,
}

/// <summary>
/// One <see cref="StockQuirk"/>, described as data: what it is, what the
/// reference implementation does, what this implementation does instead, how
/// the difference was observed, which tools it moves, and where this
/// implementation decides it.
/// </summary>
/// <param name="Quirk">The quirk.</param>
/// <param name="Title">A short name a person can scan a list by.</param>
/// <param name="Stock">One sentence: what the reference implementation does wrong.</param>
/// <param name="Correct">
/// One sentence: what <see cref="CompliancePolicy.Correct"/> does instead.
/// </param>
/// <param name="Tools">The tools whose output the quirk can move.</param>
/// <param name="ManagedSites">
/// Every managed method that decides this quirk, as
/// <c>Namespace.Type.Method</c>. The compiler-generated bodies of async
/// methods, lambdas and local functions are named by the method they belong
/// to. A fact scans the built assembly and requires this list to be exactly
/// the set of methods that pass this quirk to
/// <see cref="ComplianceOptions.Emulates"/>.
/// </param>
/// <param name="Observation">How the difference was established.</param>
/// <param name="Observed">One or two sentences: what was seen, and on what.</param>
/// <param name="Facts">
/// The test facts that show the difference, as <c>Class.Method</c> in the
/// test assembly. A fact requires every one to exist, and requires the list to
/// be empty exactly when <see cref="Observation"/> is
/// <see cref="QuirkObservation.ReadFromReference"/>.
/// </param>
/// <param name="Note">
/// Anything a user choosing a policy should know that does not fit the
/// other fields, or null.
/// </param>
public sealed record ComplianceQuirkInfo(
    StockQuirk Quirk,
    string Title,
    string Stock,
    string Correct,
    CompileTools Tools,
    ImmutableArray<string> ManagedSites,
    QuirkObservation Observation,
    string Observed,
    ImmutableArray<string> Facts,
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
/// that actually consult the switch. A third resolves every
/// <see cref="ComplianceQuirkInfo.Facts"/> entry in the test assembly, so an
/// observation cannot cite a fact that was renamed or deleted.
/// </para>
/// <para>
/// No static state: <see cref="All"/> is rebuilt on every call, so the
/// catalogue cannot be mutated by one compile under another.
/// </para>
/// <para>
/// <b>Not a quirk: elementary functions.</b> The reference takes
/// <c>sin</c>, <c>cos</c>, <c>pow</c>, <c>asin</c> and the rest from its
/// own C runtime, whose last bits no other runtime reproduces. Both policies
/// compute them correctly rounded
/// (<see cref="MapFormats.Numerics.DetMath"/>,
/// <see cref="MapFormats.Numerics.DetMathF"/>), which matches the reference
/// wherever its runtime is correctly rounded and is the same on every OS and
/// CPU. There is nothing for <see cref="CompliancePolicy.Stock"/> to switch
/// to: the host's C library would reproduce neither the reference nor
/// another host.
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
            "Base winding normalised with the rsqrt estimate",
            "BaseWindingForPlane normalises with the rsqrtss estimate plus one Newton step, "
            + "not a divide, and a zero-epsilon clip then keeps or drops slivers on its last bits.",
            "BaseWindingForPlane normalises with an exact divide, so the clip keeps the same "
            + "slivers on every CPU.",
            CompileTools.Vbsp | CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Geometry.WindingArena.BaseWindingForPlane",
            ],
            QuirkObservation.Measured,
            "Stock's world-brush shadow casters differ from Correct's by 53 triangles lost and 49 "
            + "gained, and the octahedron's PLANES lump matches stock only with this and "
            + "EdgeBevelNormalise both on the Stock side.",
            [
                "StockLoadCatalogueTests.NeitherNormaliseQuirkAloneMakesTheOctahedronsPlanesMatch",
                "ComplianceQuirkEffectTests.BaseWindingForPlaneMovesWhenOnlyItsNormaliseIsCorrected",
            ],
            "The Stock side is not reproducible across CPU models: rsqrtss may differ between "
            + "them. vrad's own VectorNormalize calls are VradVectorNormalise."),

        StockQuirk.EdgeBevelNormalise => new(
            quirk,
            "Edge bevels normalised with the rsqrt estimate",
            "AddBrushBevels normalises the edge and the candidate bevel normal with the rsqrtss "
            + "estimate, so a normal whose |x| and |z| are equal is stored as the wrong plane type.",
            "AddBrushBevels normalises exactly, so a bevel whose |x| and |z| are equal is stored "
            + "as PLANE_ANYX.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.MapFile.get_StockEdgeBevels",
            ],
            QuirkObservation.Measured,
            "The PLANES lump of 30 catalogue maps matches stock only with this and "
            + "BaseWindingNormalise both on the Stock side; with only one, the octahedron's plane "
            + "28 still disagrees.",
            [
                "StockLoadCatalogueTests.TheStoredTypeFollowsTheEdgeBevelNormaliseAndTheDistanceTheBaseWindingOne",
                "ComplianceQuirkEffectTests.TheOctahedronsBevelPlanesMoveWhenOnlyTheEdgeBevelNormaliseIsCorrected",
            ],
            "The same rule applies to the bevel of the candidate normal itself. The PLANES lump "
            + "matches the reference implementation only when this and BaseWindingNormalise are "
            + "both on the Stock side."),

        StockQuirk.AreaportalLeakWalk => new(
            quirk,
            "Areaportal leak report walks the wrong portal",
            "ReportAreaportalLeak's `continue` skips the assignment of s, so the walk steps "
            + "into the other node's portal list and the leak report names the wrong portals.",
            "The walk assigns s on every step, the link convention every other portal walk "
            + "uses, so the report names the portals the leak actually crosses.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Portals.AreaFlood.ReportAreaportalLeak",
            ],
            QuirkObservation.Demonstrated,
            "No catalogue map reaches it, so stock cannot be made to print the right answer; a "
            + "fact builds an areaportal leak and shows the corrected walk takes a different path.",
            [
                "ComplianceQuirkEffectTests.TheAreaportalLeakLineGoesRoundTheSlabWhenTheWalkIsCorrected",
            ]),

        StockQuirk.WindingIsTinyEdgePromotion => new(
            quirk,
            "Tiny-winding edge test compares in double",
            "EDGE_LENGTH is the double 0.2, so WindingIsTiny counts an edge of exactly 0.2f "
            + "as long.",
            "The edge length is compared with 0.2 in float, so an edge of exactly 0.2f is short.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Portals.TreePortals.IsTiny",
                "SourceSharp.MapTools.Bsp.Csg.BrushGeometry.WindingIsTiny",
            ],
            QuirkObservation.Demonstrated,
            "A fact shows WindingIsTiny counts an edge of exactly 0.2f as short only when the "
            + "quirk is corrected.",
            [
                "ComplianceQuirkEffectTests.BrushGeometrysWindingIsTinyCountsItAsShortWhenTheQuirkIsCorrected",
            ]),

        StockQuirk.LeakFileUnnudgedOrigin => new(
            quirk,
            "Leak file ends at the un-nudged origin",
            "The .lin file ends at the entity's origin key, not at the origin + 1z the flood "
            + "actually started from.",
            "The .lin file ends at origin + 1z, the point the flood started from.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Portals.LeakTrace.Trace",
            ],
            QuirkObservation.Measured,
            "The leaked fixture's .lin file matches the one stock wrote only on the Stock side, "
            + "and its last point moves when the quirk is corrected.",
            [
                "PortalCatalogueTests.TheLeakedFixtureProducesTheLineFileStockWrote",
                "PortalCatalogueTests.TheLeakedFixturesLastPointMovesWhenTheOriginQuirkIsCorrected",
            ]),

        StockQuirk.AddQuadSecondTriangleId => new(
            quirk,
            "Prop box's second triangle gets the next id",
            "AddQuad tags its second triangle id + 1, so half of every static prop's box "
            + "shadow is attributed to the next prop.",
            "Both triangles of a quad carry the prop's own id.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.ShadowCasterBuilder.AddQuad",
            ],
            QuirkObservation.Demonstrated,
            "A fact shows both triangles of a quad carry the same id only when the quirk is "
            + "corrected.",
            [
                "ComplianceQuirkEffectTests.AddQuadGivesBothTrianglesTheSameIdWhenTheQuirkIsCorrected",
            ]),

        StockQuirk.LeafAmbientSampleCountAxes => new(
            quirk,
            "Leaf ambient sample count uses x for all axes",
            "ComputeAmbientForLeaf clamps ySize and zSize to xSize, so a leaf's candidate "
            + "sample count is xSize cubed whatever its shape.",
            "Each axis is clamped to its own value, so a leaf draws about one candidate sample "
            + "per player-sized volume.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Ambient.LeafAmbientBuilder.CandidateSampleCount",
            ],
            QuirkObservation.Measured,
            "On a stock-compiled map, 706 of 2,286 leaves draw a different number of samples "
            + "(9,164 stock, 30,899 correct); only the Stock side reproduces its "
            + "LUMP_LEAF_AMBIENT_LIGHTING byte for byte.",
            [
                "LeafAmbientFixtureTests.TheSampleCountQuirkMovesTheLump",
                "LeafAmbientStockParityTests.TheOracleRespondsToTheSampleCountQuirk",
            ]),

        StockQuirk.AmbientCubeReciprocalEstimate => new(
            quirk,
            "Leaf ambient light term uses rcp/rsqrt estimates",
            "The leaf-ambient surface-light term uses the rcpss and rsqrtss estimates, so "
            + "which lights reach the ambient cubes, and how bright they are, depends on the CPU.",
            "The surface-light term and the 512-unit threshold divide and normalise exactly, the "
            + "same on every CPU.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Ambient.LeafAmbientBuilder.BuildAsync",
                "SourceSharp.MapTools.Rad.Ambient.TracerLineVisibility..ctor",
                "SourceSharp.MapTools.Rad.Ambient.AmbientCube.AddEmitSurfaceLights",
                "SourceSharp.MapTools.Rad.Ambient.AmbientSampler..ctor",
            ],
            QuirkObservation.Demonstrated,
            "A fact bakes one surface light into an ambient cube under both policies and shows "
            + "the cube sides differ bit for bit.",
            [
                "ComplianceMatrixEvidenceTests.TheAmbientBakeOfASurfaceLightDiffersUnderTheReciprocalEstimate",
            ],
            "The reference implementation’s inverse-length helper is the estimate, not a divide."),

        StockQuirk.AmbientSampleTieBreakNeverFires => new(
            quirk,
            "Ambient sample tie-break never fires",
            "The ambient sample list's tie-break compares against a running total that is never "
            + "assigned, so on a distance tie the earlier sample is always evicted.",
            "On a distance tie the sample with less colour variation is evicted, as the tie-break "
            + "intends.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Ambient.AmbientSampleList.Add",
            ],
            QuirkObservation.Measured,
            "Flipping this quirk alone moves a stock-compiled map's leaf ambient lump off stock's "
            + "bytes, and a fact with seventeen tied samples shows the two sides evict different "
            + "ones.",
            [
                "LeafAmbientFixtureTests.TheTieBreakQuirkMovesTheLump",
                "AmbientSampleListTests.StockNeverUsesTheTieBreak",
                "AmbientSampleListTests.CorrectBreaksADistanceTieOnColourVariation",
            ]),

        StockQuirk.DispVertNormalise => new(
            quirk,
            "Displacement vertex direction uses the rsqrt estimate",
            "A displacement vertex's stored direction is normalised with the rsqrtss estimate, "
            + "so LUMP_DISP_VERTS depends on the CPU and is not a unit vector.",
            "The stored direction is normalised with an exact divide: a unit vector, the same on "
            + "every CPU.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Disp.DisplacementLumpBuilder.Build",
                "SourceSharp.MapTools.Disp.DisplacementLumpBuilder.ComputeDispInfoBounds",
                "SourceSharp.MapTools.Disp.DispLightingLoader.Load",
                "SourceSharp.MapTools.Bsp.Detail.MapDisplacementSurfaces.PositionOnSurface",
            ],
            QuirkObservation.Measured,
            "LUMP_DISP_VERTS matches stock element for element on the 16-map displacement "
            + "catalogue only on the Stock side; the distances agree either way.",
            [
                "DispStockLumpTests.TheDispVertsLumpMatchesStockElementForElement",
                "DispVertNormaliseQuirkTests.CorrectDiffersFromStockSomewhere",
            ],
            "The displacement surface loader reads the same table."),

        StockQuirk.DispLightmapSwapDropped => new(
            quirk,
            "Displacement lightmap-axis swap is dropped",
            "A displacement's lightmap-axis swap repoints only the map face, after the BSP face "
            + "was emitted, so the swapped texinfo never reaches LUMP_FACES and is compacted away.",
            "The BSP face is repointed at the swapped texinfo copy, so the swap survives "
            + "compaction.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Disp.DispVbspHooks.FaceTexInfos",
            ],
            QuirkObservation.Demonstrated,
            "On p3f_swap the texinfo projects to 9 x 33 luxels while the face stores 33 x 9; a "
            + "fact shows the corrected face points at a swapped copy.",
            [
                "DispComplianceEffectTests.CorrectingTheSwapRepointsTheFaceAtASwappedCopy",
            ],
            "The face record is written before the swap would run, so the swap is too late to land."),

        StockQuirk.DispWorldBoundsBaseQuad => new(
            quirk,
            "World bounds use the flat displacement base",
            "A displacement adds its flat base quad (puffed 0.1) to world_mins/world_maxs, not "
            + "its displaced surface, so its height never counts.",
            "The world box takes the box of the displaced vertices, so raised terrain is inside "
            + "it.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Disp.DisplacementLumpBuilder.Build",
                "SourceSharp.MapTools.Disp.DisplacementLumpBuilder.ComputeDispInfoBounds",
            ],
            QuirkObservation.Demonstrated,
            "A fact raises a displacement above every brush and shows only the corrected world "
            + "box reaches it.",
            [
                "DispComplianceEffectTests.CorrectingTheBoundsReachesTheRaisedSurface",
                "DisplacementLumpBuilderTests.UnderStockTheWorldBoundsBoxIgnoresTheDisplacement",
            ],
            "The reference leak-bounds walk reads this box. The compile takes the box from the lump "
            + "build's own cores (DisplacementLumpBuilder.Build, plan 3p); ComputeDispInfoBounds is "
            + "the same rule on a fresh core."),

        StockQuirk.DispVertexNormalMeanUnnormalised => new(
            quirk,
            "Displacement vertex normal is left short",
            "A displacement vertex normal is the mean of unit fan normals and is never "
            + "renormalised, so crease normals are short and weigh less in vrad's blends.",
            "The mean of the fan normals is normalised, so a crease vertex weighs as much as its "
            + "neighbours.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Disp.DisplacementLumpBuilder.Build",
                "SourceSharp.MapTools.Disp.DispLightingLoader.Load",
            ],
            QuirkObservation.Demonstrated,
            "Facts show a crease vertex's stock normal is shorter than one, and that vrad's blend "
            + "leans toward the flank under Stock.",
            [
                "CoreDispInfoTests.UnderStockACreaseVertexNormalIsShorterThanOne",
                "DispComplianceEffectTests.CorrectingTheMeanGivesTheBuilderAUnitCreaseNormal",
                "VradDispSurfaceTests.UnderStockTheCreaseBlendLeansTowardTheFlank",
            ],
            "vbsp writes no normal; vrad blends one in when it samples the surface."),

        StockQuirk.WaterLeafSortTie => new(
            quirk,
            "Equal water leaves sort in reverse order",
            "IsLowerLeaf's near-equal branch returns true either way, so equal water surfaces "
            + "sort in reverse discovery order rather than by distance.",
            "Water leaves at the same height sort lower distance first.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Collision.WaterVolumeBuilder.IsLowerLeaf",
            ],
            QuirkObservation.Demonstrated,
            "Facts run IsLowerLeaf over equal surfaces and show Stock visits them in reverse tree "
            + "order.",
            [
                "WaterVolumeBuilderTests.StockVisitsEqualSurfaceLeavesInReverseTreeOrder",
                "WaterVolumeBuilderTests.CorrectVisitsEqualSurfaceLeavesInTreeOrder",
            ]),

        StockQuirk.FluidSurfacePropIgnored => new(
            quirk,
            "Fluid surface property is always water",
            "A fluid's surface property is always \"water\": the material override reads a "
            + "texinfo id hard-coded to -1.",
            "A fluid takes its water surface material's surface property, falling back to water.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Collision.PhysCollisionEmitter.FluidSurfaceProp",
            ],
            QuirkObservation.Demonstrated,
            "Facts emit a water volume with its own surface material and show only the corrected "
            + "fluid takes its property.",
            [
                "PhysCollisionEmitterTests.StockFluidsAreAlwaysWater",
                "PhysCollisionEmitterTests.CorrectFluidsTakeTheSurfaceMaterialsProperty",
            ]),

        StockQuirk.ShellMassSentinelArea => new(
            quirk,
            "Shell mass counts placeholder areas",
            "A brush entity's shell mass counts proplist's placeholder areas (1, and 2 for a "
            + "faceless model) as surface.",
            "Shell mass sums only the faces, and a faceless model is weighed by its volume.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Collision.PhysCollisionEmitter.MassAndMaterial",
            ],
            QuirkObservation.Demonstrated,
            "Facts weigh a shell model both ways: stock adds one square inch, and three for a "
            + "faceless model.",
            [
                "PhysCollisionEmitterTests.StockShellMassCountsTheSentinelArea",
                "PhysCollisionEmitterTests.CorrectShellMassWeighsOnlyTheFaces",
            ]),

        StockQuirk.WaterBrushNotClippedAtSurface => new(
            quirk,
            "Water brush is not clipped at the surface",
            "A brush crossing a water surface is added whole to the fluid's collision, "
            + "so the fluid reaches above its own surface.",
            "A brush reaching above the water surface is cut at the surface plane before it joins "
            + "the fluid.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Collision.PhysCollisionEmitter+PlaneList.AddBrushes",
            ],
            QuirkObservation.Demonstrated,
            "A fact adds a brush reaching z = 32 to a fluid whose surface is z = 20 and shows "
            + "only Correct cuts it.",
            [
                "PhysCollisionEmitterTests.StockAddsAWaterBrushCrossingTheSurfaceWhole",
                "PhysCollisionEmitterTests.CorrectCutsAWaterBrushCrossingTheSurface",
            ]),

        StockQuirk.NodeAreaWrittenBeforeSet => new(
            quirk,
            "Node areas are written before they are set",
            "The node emit copies the area into LUMP_NODES before the area-index pass has set "
            + "it, so every dnode_t::area is 0.",
            "LUMP_NODES carries each node's area (or -1 where its children disagree).",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Write.AreaPortalEmitter.Emit",
            ],
            QuirkObservation.Measured,
            "Every node of l1_areaportal, l1_sealed_room and l2_areaportal_between_pools reads "
            + "area 0 in stock's own output, and a compile under Stock does the same.",
            [
                "VbspCompileTests.UnderStockEveryNodeAreaIsZero",
            ],
            "The reference implementation assigns node areas at the end of the area-portal emit."),

        StockQuirk.FogVolumeLoopOverNoFaces => new(
            quirk,
            "Fog volume loop runs over no faces",
            "WriteFogVolumeIDs loops up to firstface + numfaces while numfaces is still 0, so no face "
            + "gets a fog volume and no water top is retextured.",
            "Each water face gets its leaf's fog volume, and the water top is retextured.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Write.WaterVolumes.WriteFogVolumeIds",
            ],
            QuirkObservation.Measured,
            "Every face of three water maps has surfaceFogVolumeID -1 in stock's output; a "
            + "compile under Correct gives the water surface face its fog volume.",
            [
                "VbspCollisionWiringTests.UnderStockNoFaceGetsAFogVolume",
                "VbspCollisionWiringTests.UnderCorrectTheWaterSurfaceFaceGetsItsFogVolume",
            ],
            "The reference model-close step is what sets the face count."),

        StockQuirk.InverseQuadraticReciprocal => new(
            quirk,
            "Falloff solve divides through a reciprocal",
            "The falloff solve for _fifty_percent_distance lights divides by the determinant "
            + "through a reciprocal (the /fp:fast binary), so the attenuation terms differ in "
            + "their last bits from the three divides the source writes.",
            "The falloff solve divides by the determinant three times, as the source says.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.DirectLightBuilder.ApplyFalloff",
            ],
            QuirkObservation.Measured,
            "Stock's LUMP_WORLDLIGHTS on the texlight fixture holds the reciprocal form's "
            + "attenuation terms, which the three divides miss in the last bits.",
            [
                "MathSolverTests.TheReciprocalFormIsTheStockBinarysAnswer",
            ],
            "Observed in stock's LUMP_WORLDLIGHTS on p4c's texlight fixture; a C probe of "
            + "both forms reproduces stock only with the reciprocal."),

        StockQuirk.CrtCosineAtRightAngle => new(
            quirk,
            "Cosine of a right angle is sin(pi)",
            "A light's direction takes sin(pi) = 1.2246469e-16 as the cosine of a right angle, "
            + "where cos(pi/2) rounds to 6.123234e-17: a spot pointing straight down differs.",
            "A light's direction uses the correctly rounded cosine, 6.123234e-17 at a right "
            + "angle.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.DirectLightBuilder.ParseGeneric",
            ],
            QuirkObservation.Measured,
            "Stock's LUMP_WORLDLIGHTS on the texlight fixture holds 1.2246469e-16 for two "
            + "downward spots.",
            [
                "LightNormalTests.TheStockCosineOfARightAngleIsSinPi",
                "LightNormalTests.TheCorrectCosineOfARightAngleIsTheDoublesDistanceFromPi",
            ],
            "Same for the pitch: the C runtime's cosine, not a table."),

        StockQuirk.VradVectorNormalise => new(
            quirk,
            "vrad normals use the rsqrt estimate",
            "vrad's own normals (PairEdges corners, phong, bump basis, light targets) are "
            + "normalised with the rsqrtss estimate, not a divide.",
            "vrad's normals are normalised with an exact divide, the same on every CPU.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.LightGeometry.get_StockNormalise",
                "SourceSharp.MapTools.Rad.Light.DirectLightingSettings.get_StockNormalise",
                "SourceSharp.MapTools.Rad.Light.DirectLightBuilder.ParseGeneric",
                "SourceSharp.MapTools.Rad.Final.LuxelDensity.Apply",
                "SourceSharp.MapTools.Rad.Props.StaticPropLighting.StockNormalise",
            ],
            QuirkObservation.Demonstrated,
            "A fact normalises the same vectors under both policies and shows the Stock side "
            + "takes the estimate.",
            [
                "ComplianceMatrixEvidenceTests.StockPolicyNormalisesVectorsTheStockWay",
            ],
            "The same estimate reaches bump-vector construction, the static prop's push toward "
            + "the light, form factors, and the direct-light gather."),

        StockQuirk.GatherReciprocalEstimate => new(
            quirk,
            "Direct-light gather uses rcp/rsqrt estimates",
            "The direct-light gather, the four-wide phong normal and the skybox recursion divide "
            + "with the rcpps/rsqrtps estimates plus one Newton step, so lightmaps depend on the CPU.",
            "The gather divides and takes square roots exactly and takes the spot power correctly "
            + "rounded, so lightmaps depend on neither the CPU nor the OS.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.DirectLightGatherer..ctor",
                "SourceSharp.MapTools.Rad.Light.LightGeometry.get_StockEstimates",
                "SourceSharp.MapTools.Rad.Props.PropLightSampler..ctor",
            ],
            QuirkObservation.Demonstrated,
            "A fact finds inputs where the stock estimate is not the exact reciprocal.",
            [
                "GatherTests.TheStockEstimateIsNotAlwaysTheExactReciprocal",
            ],
            "The reference reciprocal / reciprocal-sqrt helpers are the estimate. Detail and "
            + "static prop lighting call the same gather, and a trace direction is under it too."),

        StockQuirk.SpotExponentQuarterSteps => new(
            quirk,
            "Spot exponent rounded to quarter steps",
            "A spotlight's _exponent is applied through PowSIMD's fixed point with two fractional "
            + "bits, so 1.3 behaves as 1.25.",
            "A spotlight's _exponent is applied as a real power.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.DirectLightGatherer..ctor",
            ],
            QuirkObservation.Demonstrated,
            "A fact shows the Stock power with exponent 1.3 is the power 1.25.",
            [
                "GatherTests.AStockSpotExponentIsRoundedDownToAQuarter",
            ],
            "The reference SIMD power and its fixed-point exponent share the exponent trick."),

        StockQuirk.SkyboxRecursionFromLaneZero => new(
            quirk,
            "Skybox recursion decided by the first sample",
            "A sky ray's 3D-skybox recursion is decided by the first of four samples' leaf for "
            + "all four.",
            "Each of the four samples decides its own skybox recursion.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.DirectLightGatherer..ctor",
            ],
            QuirkObservation.Demonstrated,
            "A fact puts lane 0 in the skybox's area and the other three in the world: Stock "
            + "recurses for none, Correct for three.",
            [
                "SkyRecursionTests.AStockGroupRecursesByLaneZerosArea",
            ]),

        StockQuirk.SkyProbeTailDoubleCount => new(
            quirk,
            "Sky probe casts its last direction three times",
            "CanLeafTraceToSky clamps the last group of four directions to index 161, casting "
            + "g_anorms[161] three times.",
            "Each of the 162 sky directions is cast once. The answer is the same either way; only "
            + "the ray count moves.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.RadWorld.CanLeafTraceToSky",
            ],
            QuirkObservation.Demonstrated,
            "A fact counts one probe's rays: 164 under Stock, 162 under Correct.",
            [
                "QuirkEffectTests.StockCastsTheTailDirectionThreeTimes",
                "QuirkEffectTests.CorrectCastsEachOfTheHundredAndSixtyTwoOnce",
            ],
            "Changes the ray count only: the answer is 'any ray reached sky'."),

        StockQuirk.SecondSunSpreadAngleWins => new(
            quirk,
            "Second light_environment sets the sun's spread",
            "A second light_environment, whose light is discarded, still overwrites the sun's "
            + "SunSpreadAngle.",
            "The sun's spread comes only from the light_environment that becomes the sun.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.DirectLightBuilder.ParseLightEnvironment",
            ],
            QuirkObservation.Demonstrated,
            "Facts parse two light_environments and show which one's SunSpreadAngle each side "
            + "keeps.",
            [
                "QuirkEffectTests.StockLetsTheSecondSunsSpreadWin",
                "QuirkEffectTests.CorrectKeepsTheFirstSunsSpread",
            ]),

        StockQuirk.MonotonicDerivativeAtOne => new(
            quirk,
            "Monotonic falloff fit tests the slope at x = 1",
            "The monotonic falloff fit tests the slope at x = 1 (2a + b) where its comment says "
            + "the start point.",
            "The fit tests the slope at its start point (2a·x1 + b), as its comment says.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.DirectLightBuilder.ApplyFalloff",
            ],
            QuirkObservation.Demonstrated,
            "A fact fits an extreme falloff and shows Stock caps it where the curve turns under "
            + "its x = 1 test.",
            [
                "LightFalloffTests.AnExtremeFalloffIsCappedWhereTheCurveTurnsUnderStocksSlopeTest",
            ]),

        StockQuirk.CubemapDistanceNormalise => new(
            quirk,
            "Nearest cubemap chosen on an estimated distance",
            "The nearest env_cubemap in front of a specular side is chosen on a distance from the "
            + "rsqrtss estimate, so a near tie can pick a different cubemap patch on another CPU.",
            "The nearest cubemap is chosen on an exact length, so a near tie resolves the same on "
            + "every CPU.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Cubemaps.CubemapFixups.FindClosestCubemap",
            ],
            QuirkObservation.ReadFromReference,
            "Read from the reference: the nearest-sample loop measures with the rsqrt estimate "
            + "while its fallback loop uses an exact length. No fact yet shows the two sides pick "
            + "differently.",
            [],
            "The reference fallback loop uses a full length and is exact either way."),

        StockQuirk.DetailOrientationNormalise => new(
            quirk,
            "Detail prop basis uses the rsqrt estimate",
            "A conforming detail prop's tangent basis is normalised with the rsqrtss estimate, "
            + "so the angles stored in dprp depend on the CPU.",
            "A conforming detail prop's basis is normalised exactly, so its stored angles are the "
            + "same on every CPU.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Detail.DetailPropEmitter.ConformingAngles",
            ],
            QuirkObservation.Demonstrated,
            "A fact orients one conforming detail prop under both policies and shows its angles "
            + "differ.",
            [
                "ComplianceMatrixEvidenceTests.AConformingDetailOrientationDiffersUnderTheStockNormalise",
            ],
            "The same swap appears in the two detail-object cross products."),

        StockQuirk.CubemapIgnoresPatchMaterials => new(
            quirk,
            "Patch materials never get cubemap patches",
            "A patch VMT is read raw when vbsp asks whether a material has $envmap, so a patched "
            + "specular material never gets its per-cubemap patch.",
            "The patch is expanded before the $envmap test, so a patched specular material gets "
            + "its per-cubemap patch.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.MaterialPatch.MaterialPatcher.LoadOriginalAsync",
            ],
            QuirkObservation.Measured,
            "On l2_cubemap_on_water_and_patch stock writes no cubemap patch for the patched "
            + "walls; a fact shows Correct patches them.",
            [
                "CubemapTests.UnderStockAPatchMaterialIsNeverTreatedAsSpecular",
                "CubemapTests.APatchMaterialIsPatchedWhenTheQuirkIsCorrected",
            ],
            "Material patch construction reads the same field three ways."),

        StockQuirk.PatchExpandInsertDropsReplace => new(
            quirk,
            "Patch with insert loses its replace",
            "ExpandPatchFile looks the replace section up in the included material once an insert "
            + "has been applied, so a patch with both sections loses its replace.",
            "Both the insert and the replace sections are taken from the patch.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.MaterialPatch.MaterialPatcher.ExpandPatchAsync",
            ],
            QuirkObservation.Demonstrated,
            "A fact expands a patch with both sections and shows only Correct keeps both.",
            [
                "MaterialPatcherTests.APatchWithInsertAndReplaceKeepsBothWhenTheQuirkIsCorrected",
            ]),

        StockQuirk.PatchExpandEmptyPatchNeverResolves => new(
            quirk,
            "Empty patch never resolves",
            "ExpandPatchFile never resolves a patch with neither insert nor replace; it stays a "
            + "patch after ten passes.",
            "A patch with neither section resolves to its include, as the engine's reader does.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.MaterialPatch.MaterialPatcher.ExpandPatchAsync",
            ],
            QuirkObservation.Demonstrated,
            "A fact expands a patch with neither section and shows only Correct resolves it.",
            [
                "MaterialPatcherTests.APatchWithNeitherSectionIsItsIncludeWhenTheQuirkIsCorrected",
            ]),

        StockQuirk.CubemapUnreferencedNeverMatches => new(
            quirk,
            "Unreferenced-cubemap check never matches",
            "Cubemap_AddUnreferencedCubemaps compares a texture name with file names, never matches, "
            + "and re-adds every sample; the pak writer hides the duplicates.",
            "File names are compared with file names, so each used sample is added once.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Cubemaps.CubemapFixups.AddUnreferencedCubemaps",
            ],
            QuirkObservation.Demonstrated,
            "The pak is identical under both policies on all 11 3g entries, because the pak "
            + "writer hides the duplicates; a fact shows Correct adds a used sample once.",
            [
                "CubemapTests.AddUnreferencedAddsAUsedSampleOnceWhenTheQuirkIsCorrected",
            ]),

        StockQuirk.OverlayFaceLimitOffByOne => new(
            quirk,
            "Full overlay face list is refused",
            "An overlay face list that exactly fills its 64 (water: 256) slots is refused.",
            "Only a face list that does not fit (over 64, or 256 for water) is refused.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Overlays.OverlaySet.TooMany",
            ],
            QuirkObservation.Demonstrated,
            "A fact gives an overlay exactly 64 faces and shows only Correct accepts it.",
            [
                "OverlayTests.SixtyFourFacesFitWhenTheQuirkIsCorrected",
            ],
            "Water overlays take the same path."),

        StockQuirk.OverlayMaterialNotReplaced => new(
            quirk,
            "Overlay material skips -replacematerials",
            "-replacematerials is not applied to an info_overlay's material, although it is to "
            + "brush sides and water overlays.",
            "-replacematerials applies to info_overlay materials too.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Overlays.OverlaySet.AddFromEntity",
            ],
            QuirkObservation.Demonstrated,
            "A fact shows only Correct applies the replacement table to an overlay's material.",
            [
                "OverlayTests.AnOverlayMaterialIsReplacedWhenTheQuirkIsCorrected",
            ]),

        StockQuirk.StudioVersionSlam => new(
            quirk,
            "Any studio model version is accepted",
            "Every studio model version is overwritten with 48 before it is checked, so a model of "
            + "an incompatible version is read as garbage instead of refused.",
            "Versions 44 to 48 are accepted and any other is refused as unreadable.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Props.StudioModelCheck.LoadAsync",
            ],
            QuirkObservation.Demonstrated,
            "A fact loads a version 37 model and shows only Correct refuses it.",
            [
                "StaticPropTests.AVersion37ModelIsRefusedWhenTheQuirkIsCorrected",
            ],
            "The reference header declares the field without the bits; the liveness check misses them."),


        StockQuirk.IndirectSurfaceEnumeratorReused => new(
            quirk,
            "Prop indirect rays share one enumerator",
            "ComputeIndirectLightingAtPoint reuses one CLightSurface across its sample rays and "
            + "never resets m_HitFrac, so each ray ignores leaf and displacement hits beyond the "
            + "previous ray's hit.",
            "Every indirect sample ray starts from a fresh surface enumerator.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Props.PropIndirectLighting.Compute",
            ],
            QuirkObservation.Measured,
            "The fixture's stock .vhv files match byte for byte on the Stock side and move off "
            + "them when this quirk alone is corrected.",
            [
                "StaticPropLightingTests.AFreshEnumeratorPerRayMovesTheFilesOffStocks",
                "StaticPropUnitTests.AReusedEnumeratorChangesTheIndirectTermSomewhere",
            ]),

        StockQuirk.StaticPropBadVertexDropsPropFlags => new(
            quirk,
            "Relit prop vertex drops the prop's flags",
            "A static prop vertex embedded in solid is relit with default arguments, dropping "
            + "the prop's IGNORE_NORMALS and NO_SELF_SHADOWING flags for that vertex only.",
            "A vertex in solid is relit with the prop's IGNORE_NORMALS and NO_SELF_SHADOWING "
            + "flags.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Props.StaticPropLighting.PrepareModel",
            ],
            QuirkObservation.Measured,
            "The fixture's stock .vhv files move off stock's bytes when this quirk alone is "
            + "corrected.",
            [
                "StaticPropLightingTests.RelightingABadVertexWithThePropsFlagsMovesTheFilesOffStocks",
                "StaticPropChunkingTests.RelightingABadVertexWithThePropsFlagsChangesOnlyAPropWithFlags",
            ]),

        StockQuirk.FormFactorSineAboveOne => new(
            quirk,
            "Form factor dropped when a sine rounds past 1",
            "A polygon form factor whose edge sine rounds past 1 is discarded whole, and the "
            + "patch-to-patch transfer with it.",
            "The sine is clamped to 1 and the other edges keep summing, so the transfer is kept.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Bounce.FormFactors.PolyToDiff",
            ],
            QuirkObservation.Measured,
            "The bounce gate shows Correct keeps transfers stock drops, and unit facts show the "
            + "Stock side returns 0.",
            [
                "RadWorldBounceGateTests.CorrectKeepsTheTransfersStockDropsOnASineAboveOne",
                "FormFactorsTests.StockReturnsZeroWhenAnEdgeSineRoundsAboveOne",
                "FormFactorsTests.CorrectClampsTheSineAndKeepsSumming",
            ],
            "Correct clamps the sine to 1. The reference transfer step drops a form factor <= 0."),

        StockQuirk.TransferRayReciprocalEstimate => new(
            quirk,
            "Transfer rays use the rcp estimate",
            "The patch-to-patch visibility rays are normalised with the rcpps estimate plus a "
            + "Newton step, not a divide.",
            "Each transfer ray's direction is divided exactly, so which transfers exist does not "
            + "depend on the CPU.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Bounce.VisMatrix.MakeRays",
            ],
            QuirkObservation.Measured,
            "Against stock's log, an exact divide moves up to 73 of 195,455 transfers on "
            + "l1_func_detail.",
            [
                "TransferRayTests.CorrectDividesTheDirection",
                "TransferRayTests.TheEstimateIsCloseToTheDivide",
            ],
            "The reference reciprocal helper is the estimate: machine-dependent on the Stock side."),

        StockQuirk.VisPlaneTestPhongNormal => new(
            quirk,
            "Transfer plane test mixes phong and flat",
            "TestPatchToPatch tests a smoothed patch's phong normal against its flat plane's "
            + "distance, a plane tilted about the world origin.",
            "The patch-to-patch test uses the patch's own plane normal; unsmoothed faces are "
            + "unchanged.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Bounce.VisMatrix.get_StockPlaneTest",
            ],
            QuirkObservation.Demonstrated,
            "Facts tilt a smoothed patch's shading normal and show only Stock's plane test "
            + "follows it.",
            [
                "VisMatrixBuildTests.StockPlaneTestFollowsTheShadingNormal",
                "VisMatrixBuildTests.CorrectPlaneTestIgnoresTheShadingNormal",
            ],
            "Correct uses the patch's plane normal. No effect on a face that is not smoothed."),

        StockQuirk.DispFastSamplesPastEdge => new(
            quirk,
            "-fast displacement samples miss the edge",
            "-fast samples a displacement's luxels half a luxel off and its last row and column "
            + "off the surface, which leaves them black.",
            "Each -fast displacement luxel is sampled at its own position, so the last row and "
            + "column are lit.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Displacement.DispSampleBuilder.BuildSamplesAndLuxelsFast",
            ],
            QuirkObservation.Measured,
            "On the 18 p3f-t displacement maps every displacement's last row and column is zero "
            + "in stock's -fast output and in none of its full compiles; the -fast gate matches "
            + "stock on the Stock side.",
            [
                "DispStockGateTests.FastDirectLightAgreesWithStockOnDisplacementLuxels",
                "DispSampleBuilderTests.UnderStockFastTheLastColumnIsLeftAtTheOrigin",
                "DispSampleBuilderTests.UnderCorrectFastEverySampleIsOnItsLuxel",
            ],
            "The reference displacement surface lookup returns early past u = 1."),

        StockQuirk.DispFastSampleAreaZero => new(
            quirk,
            "-fast displacement samples have no area",
            "-fast never sets a displacement sample's area, so its patches get no direct light "
            + "and it reflects nothing.",
            "Each -fast displacement sample gets the world area per luxel, as the flat -fast path "
            + "does.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Displacement.DispSampleBuilder.BuildSamplesAndLuxelsFast",
            ],
            QuirkObservation.Demonstrated,
            "Facts build -fast displacement samples and show only Correct gives them an area.",
            [
                "DispSampleBuilderTests.UnderStockFastSamplesHaveNoArea",
                "DispSampleBuilderTests.UnderCorrectFastSamplesHaveTheWorldAreaPerLuxel",
            ],
            "The reference sample weight is area-weighted; the flat fast path sets it directly."),

        StockQuirk.SampleRadialEdgeOffByOne => new(
            quirk,
            "Radial sample edge test is off by one",
            "SampleRadial's edge test lets u == w and v == h through and reads the next row's "
            + "first luxel, or the zeroed tail of the grid.",
            "A point at u == w or v == h is off the grid.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Final.LuxelRadial.IsOffGrid",
            ],
            QuirkObservation.Demonstrated,
            "Facts sample one past the last column and show only Stock lets it through.",
            [
                "LuxelRadialTests.StocksEdgeTestLetsAPointOnePastTheLastColumnThrough",
                "LuxelRadialTests.TheCorrectEdgeTestPutsAPointOnePastTheLastColumnOffTheGrid",
            ]),

        StockQuirk.PatchRadialNeighbourBumpFromSelf => new(
            quirk,
            "Bounce filter asks the face, not the neighbour",
            "The bounce filter takes a neighbour's bumpiness from the face being filtered, so an "
            + "unbumped neighbour's zero bump light darkens a bumped face's bump maps.",
            "The bounce filter asks each neighbour whether it is bumped.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Final.FinalLightFace.NeighbourPatchBumpmap",
            ],
            QuirkObservation.Demonstrated,
            "Facts show Stock asks the face being filtered and Correct asks the neighbour.",
            [
                "FinalLightFaceTests.StocksBounceFilterTakesANeighboursBumpinessFromTheFaceItself",
                "FinalLightFaceTests.TheCorrectBounceFilterAsksTheNeighbour",
            ]),

        StockQuirk.FastFinalLightStyleZero => new(
            quirk,
            "-fast writes every style from style zero",
            "Under -fast every light style of a face is written from style slot zero.",
            "Under -fast each light style is written from its own light, so switchable lights "
            + "work.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Final.FinalLightFace.FastLuxel",
            ],
            QuirkObservation.Demonstrated,
            "Facts write a two-style face under -fast and show which slot each side reads.",
            [
                "FinalLightFaceTests.StocksFastBranchReadsStyleSlotZeroForEveryStyle",
                "FinalLightFaceTests.TheCorrectFastBranchReadsTheStyleBeingWritten",
            ]),

        StockQuirk.DegreesToRadiansByReciprocal => new(
            quirk,
            "Degrees converted through a float reciprocal",
            "A light's angles are converted with the float reciprocal of 180 (the /fp:fast "
            + "binary), not a division: ss_sandbox's sun direction moves by six float ulps.",
            "A light's degrees are divided by 180, as the source says.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.DirectLightBuilder.ParseGeneric",
                "SourceSharp.MapTools.Rad.Light.DirectLightBuilder.ParseLightSpot",
            ],
            QuirkObservation.Measured,
            "Stock's sun direction on ss_sandbox (yaw 300, pitch -40) is reproduced only by the "
            + "reciprocal; the exact division misses by six and two float ulps.",
            [
                "StockRayTests.StockConvertsDegreesWithTheFloatReciprocalOf180",
            ],
            "The same estimate is used for the pitch and for spot cones."),

        StockQuirk.SupersampleGradientReadsUninitialised => new(
            quirk,
            "Supersample gradient reads uninitialised luxels",
            "The supersampling gradient compares edge samples with the uninitialised intensity of "
            + "luxels that have no sample, so which edges are supersampled depends on stack garbage.",
            "Luxels with no sample are left out of the gradient.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Light.FaceLightJob.SupersampleReadsUninitialised",
            ],
            QuirkObservation.Demonstrated,
            "Undefined in stock, so not reproducible bit for bit; a fact shows the Stock model "
            + "supersamples next to a missing luxel and Correct does not.",
            [
                "ComplianceMatrixEvidenceTests.StockSupersamplesAgainstUninitialisedGradientMemoryAndCorrectDoesNot",
            ],
            "Undefined behaviour: the Stock side is a model (always supersample), not a reproduction."),

        StockQuirk.LuxelDensityLeavesHdrFacesStale => new(
            quirk,
            "-luxeldensity leaves HDR faces stale",
            "-luxeldensity recomputes the extents of the LDR faces only, so an -hdr pass lights "
            + "faces whose extents belong to the old lightmap axes.",
            "-luxeldensity recomputes the extents of both the LDR and the HDR faces.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Rad.Final.LuxelDensity.StaleHdrFaces",
            ],
            QuirkObservation.Demonstrated,
            "A fact shows the Stock side leaves the HDR faces' extents stale and Correct updates "
            + "them.",
            [
                "ComplianceMatrixEvidenceTests.StockLeavesTheHdrFaceExtentsStaleAndCorrectUpdatesThem",
            ]),

        StockQuirk.CollisionCookerSinglePrecision => new(
            quirk,
            "Collision cooked in single precision",
            "The reference collision cooker computes in float with the rsqrtss/rsqrtps estimates, "
            + "so the physics lump depends on the CPU; the later reference build cooks in double.",
            "Collision is cooked in double precision, identical on every CPU.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Phys.Managed.ManagedCollisionCooker.Create",
            ],
            QuirkObservation.Measured,
            "Comparing the two reference physics builds: a cube's mass_center and "
            + "rotation_inertia differ in 15 of 440 bytes, and each side reproduces its build bit "
            + "for bit.",
            [
                "ManagedCollisionCookerTests.StockComplianceCooksInSinglePrecision",
                "ManagedCollisionCookerTests.CorrectComplianceCooksInDoublePrecision",
            ],
            "The reference’s internal physics width is float in the earlier build and double "
            + "in the later one. Stock = the earlier arithmetic, correct = the later (plan Q18)."),

        StockQuirk.CollisionInertiaZeroLengthEdge => new(
            quirk,
            "Zero-length ledge edge makes inertia NaN",
            "The reference inertia integral divides 0 by 0 on a zero-length ledge edge and writes "
            + "NaN into rotation_inertia (6 of dm_lockdown's 2,239 brushes).",
            "A zero-length edge is skipped, so rotation_inertia stays finite.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Phys.Managed.ManagedCollisionCooker.Create",
            ],
            QuirkObservation.Measured,
            "6 of dm_lockdown's 2,239 brushes come out of the reference with NaN "
            + "rotation_inertia; Stock reproduces all six and Correct writes finite values.",
            [
                "ManagedCollisionCookerTests.CorrectComplianceWritesFiniteInertiaWhereTf2WritesNaN",
                "ManagedCookerParityTests.CorrectModeDiffersFromTf2OnlyByTheNaNInertiaItFixes",
            ],
            "Inside the reference inertia solver: the per-axis divide is taken on NaN."),

        StockQuirk.CollisionPolysoupMaterialOverrun => new(
            quirk,
            "Polysoup material fix-up overruns",
            "The polysoup conversion's material fix-up runs past a ledge whose first triangle "
            + "has material 0 and clears the exponent bits of its first points' x coordinates.",
            "The ledge's material is copied to its own triangles only, so its points keep their "
            + "coordinates.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Phys.Managed.ManagedCollisionCooker.Create",
            ],
            QuirkObservation.Measured,
            "The golden polysoups are byte-exact against both reference builds only with the "
            + "overrun reproduced.",
            [
                "ManagedCollisionCookerTests.StockPolysoupsOverrunIntoTheirPointsOnMaterialZero",
            ],
            "Inside the reference polysoup conversion’s material fix-up. Only the "
            + "-novirtualmesh / power-4 displacement road reaches it."),

        StockQuirk.KdZeroDirectionReachCut => new(
            quirk,
            "Zero ray component cuts the ray short",
            "The KD tracer saturates a zero direction component to FLT_EPSILON, so an "
            + "axis-parallel ray starting just inside a plane is cut short and misses what lies "
            + "beyond.",
            "A zero direction component is replaced with 2^-60, so no ray a map can hold is cut "
            + "short.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Tracing.KdRayTracer.ZeroSubstitute",
            ],
            QuirkObservation.Measured,
            "In stock's own tracer a +Y shadow ray from one ulp inside x = 100 misses a wall "
            + "1,000 units away, and hits it with -0; Correct hits it either way.",
            [
                "KdRayTracerZeroDirectionTests.StockCutsAPositiveZeroRayShortOfTheWall",
                "KdRayTracerZeroDirectionTests.CorrectLetsAPositiveZeroRayReachTheWall",
            ],
            "The reference saturates a zero component to FLT_EPSILON. Correct substitutes "
            + "2^-60. Only rays with an exactly zero direction component are affected."),

        StockQuirk.VbspVectorNormalise => new(
            quirk,
            "vbsp face and disp normals use the rsqrt estimate",
            "Face subdivision, t-junction fixing, the face merge test and the displacement "
            + "surface's normals normalise with the rsqrtss estimate, so the vertices they make "
            + "depend on the CPU.",
            "Those normalises divide exactly, so the face lumps are the same on every CPU.",
            CompileTools.Vbsp | CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Bsp.Faces.FaceSubdivider..ctor",
                "SourceSharp.MapTools.Bsp.Faces.TJunctionFixer..ctor",
                "SourceSharp.MapTools.Bsp.Faces.FaceMerger..ctor",
                "SourceSharp.MapTools.Disp.DisplacementLumpBuilder.Build",
                "SourceSharp.MapTools.Disp.DisplacementLumpBuilder.ComputeDispInfoBounds",
                "SourceSharp.MapTools.Bsp.Detail.MapDisplacementSurfaces.PositionOnSurface",
                "SourceSharp.MapTools.Disp.DispLightingLoader.Load",
            ],
            QuirkObservation.Measured,
            "Valve's SDK 2fort compiled under the default policy with the estimate at these sites "
            + "gave different Vertexes, Faces, Edges and six more face lumps on an AMD Ryzen 9950X "
            + "and an Intel Xeon. On the Xeon the exact divide moves exactly those lumps, and this "
            + "quirk alone on the Stock side restores the old bytes.",
            [
                "VbspVectorNormaliseTests.StockSubdivisionCutsAtTheEstimatedDistance",
                "VbspVectorNormaliseTests.CorrectSubdivisionCutsAtTheExactDistance",
                "VbspVectorNormaliseTests.StockEdgeFixingProjectsOntoTheEstimatedDirection",
                "VbspVectorNormaliseTests.CorrectEdgeFixingProjectsOntoTheExactDirection",
                "VbspVectorNormaliseTests.StockMergeTestTakesTheEstimatedNormal",
                "VbspVectorNormaliseTests.CorrectMergeTestTakesTheExactNormal",
                "CoreDispSurfaceNormaliseTests.StockLoadTakesTheEstimatedBaseQuadNormal",
                "CoreDispSurfaceNormaliseTests.CorrectLoadTakesTheExactBaseQuadNormal",
                "CoreDispSurfaceNormaliseTests.StockLongestInUBreaksATieOnTheEstimate",
                "CoreDispSurfaceNormaliseTests.CorrectLongestInUKeepsTheExactTie",
                "VbspCpuIndependenceTests.TheCorrectSandboxBspIsPinnedOnEveryCpu",
                "VbspCpuIndependenceTests.TheCorrectDisplacementBspIsPinnedOnEveryCpu",
            ],
            "Only vrad's displacement loader reaches this quirk in vrad, through the base quad's "
            + "normal; vrad's own normalises are VradVectorNormalise. The ambient tracer's "
            + "displacement collision set builds the same surface but reads nothing the "
            + "normalise decides, and always divides exactly."),

        StockQuirk.KdTracerReciprocalEstimate => new(
            quirk,
            "KD tracer uses the rcp and rsqrt estimates",
            "The KD tracer takes each ray direction's reciprocal with the rcpps estimate plus a "
            + "Newton step, and normalises each triangle's plane with the rsqrtss estimate, so "
            + "which occluder a shadow ray meets depends on the CPU.",
            "Traversal divides and the planes are normalised with a divide, so every hit is the "
            + "same on every CPU.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Tracing.KdRayTracer.StockReciprocal",
            ],
            QuirkObservation.Measured,
            "With the estimate in the KD tracer under the default policy, static-prop lighting "
            + "of the leaf-ambient fixture gave four different digests on Apple Silicon than on "
            + "x86, and on an AMD host this quirk alone moves ss_sandbox's Lighting and "
            + "LeafAmbientLighting lumps (1,290 bytes with two bounces); with the divide, those digests hold "
            + "whatever the estimate returns.",
            [
                "KdTracerReciprocalEstimateTests.StockTraversalTakesTheEstimatedReciprocal",
                "KdTracerReciprocalEstimateTests.CorrectTraversalDivides",
                "KdTracerReciprocalEstimateTests.StockTrianglePlanesTakeTheEstimatedNormal",
                "KdTracerReciprocalEstimateTests.CorrectTrianglePlanesTakeTheExactNormal",
                "KdTracerReciprocalEstimateTests.ThePoliciesTraceTheSameRaysToDifferentDistances",
                "VradCpuIndependenceTests.TheCorrectKdSceneDistancesArePinnedOnEveryCpu",
            ],
            "The zero-component substitute taken before the reciprocal is "
            + "KdZeroDirectionReachCut, decided separately. The GPU tracer never took either "
            + "estimate. The Stock side still throws on a CPU with neither SSE nor AdvSimd; the "
            + "Correct side runs anywhere."),

        StockQuirk.SkyWindingNormalise => new(
            quirk,
            "Sky test normalises with the rsqrt estimate",
            "The leaf-ambient walk's sky test normalises the sky face's edges and edge crosses "
            + "with the rsqrtss estimate over a length biased by 1e-10, so whether a ray near a "
            + "sky face's edge sees the sky depends on the CPU.",
            "Those normalises divide, so the sky windings and the point-in-winding test are the "
            + "same on every CPU.",
            CompileTools.Vrad,
            [
                "SourceSharp.MapTools.Tracing.BspTraceGeometry.Build",
            ],
            QuirkObservation.Demonstrated,
            "Three colinear points 1e-5 units apart: the biased estimate shortens both edges to "
            + "about 0.71, their dot falls under 0.999 and the middle point is kept; divided, the "
            + "dot is 1 and it is removed.",
            [
                "SkyWindingNormaliseTests.StockKeepsAColinearPointBetweenShortEdges",
                "SkyWindingNormaliseTests.CorrectRemovesAColinearPointBetweenShortEdges",
                "SkyWindingNormaliseTests.StockSkyTestNormalisesWithTheEstimate",
                "SkyWindingNormaliseTests.CorrectSkyTestNormalisesWithADivide",
                "VradCpuIndependenceTests.TheCorrectLeafAmbientIsPinnedOnEveryCpu",
            ],
            "Decided once, when the walk's geometry is built, and read by both the leaf-ambient "
            + "walk and the generic surface tracer, so the windings and the test on them always "
            + "follow the same side."),

        StockQuirk.PlaneFromPointsNormalise => new(
            quirk,
            "Brush side planes normalised with the rsqrt estimate",
            "PlaneFromPoints normalises each brush side's cross product with the rsqrtss "
            + "estimate plus a Newton step, so every slanted side's normal, and every plane later "
            + "merged into it, depends on the CPU.",
            "PlaneFromPoints divides exactly, so the plane table and the tree built on it are the "
            + "same on every CPU.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.MapFileLoader.LoadSideAsync",
            ],
            QuirkObservation.Measured,
            "Of the 122 slanted load-time planes this port numbers the same as stock's 2fort BSP, "
            + "the exact divide gives stock's normal for 72; 24 are no exact formula's answer, and "
            + "every one is the Newton step from an estimate inside rsqrtss's error bound.",
            [
                "PlaneFromPointsNormaliseTests.StocksSlantedPlanesAreTheEstimatesNewtonStep",
                "PlaneFromPointsNormaliseTests.SomeOfStocksPlanesAreNoExactFormulasAnswer",
                "PlaneFromPointsNormaliseTests.StockModeReproducesStocksPlanes",
                "PlaneFromPointsNormaliseTests.StockLoadTakesTheEstimatedNormal",
                "PlaneFromPointsNormaliseTests.CorrectLoadTakesTheExactNormal",
                "PlaneFromPointsNormaliseTests.TheSlantedPlanesMoveWhenOnlyThisQuirkIsCorrected",
            ],
            "One ulp here does not stay one ulp: the split heuristic's epsilon-brush test reads "
            + "the sign of a vertex's residual against the plane, so on 2fort it moves which "
            + "plane a node splits on and the cluster count with it (SplitEpsilonBrushOnPlane "
            + "narrows that under Correct). On a CPU whose estimate is not the reference's, the "
            + "Stock side still differs from stock."),

        StockQuirk.SplitEpsilonBrushOnPlane => new(
            quirk,
            "Split heuristic reads an on-plane vertex by its rounding",
            "TestBrushToPlanenum charges a candidate plane 1000 points for a brush whose furthest "
            + "vertex is more than 0 and under 1 unit across it, so a vertex lying on the plane is "
            + "charged or not by the sign of its rounding residual.",
            "A vertex within 0.1 of the plane, where SplitBrush already stops calling it a "
            + "crossing, is on the plane, so only a brush crossing by 0.1 to 1 unit is charged and "
            + "last-bit noise in the windings no longer moves the split.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Tree.BrushBspTree.TestBrushToPlaneNumber",
            ],
            QuirkObservation.Demonstrated,
            "On 2fort a vertex residual of +6.1e-5 cost one wedge plane the penalty and moved the "
            + "split; flipping PlaneFromPointsNormalise, BaseWindingNormalise or EdgeBevelNormalise "
            + "alone moved Correct's cluster count from 2492 to 2476, 2500 or 2495. With this quirk "
            + "corrected, Correct gives 2473 and the three flips 2475, 2476 and 2473, and the "
            + "EdgeBevelNormalise flip leaves the tree identical.",
            [
                "SplitEpsilonBrushOnPlaneTests.StockCountsAnEdgeAFewUlpsInFrontAndNotOneAFewUlpsBehind",
                "SplitEpsilonBrushOnPlaneTests.StockCountsAnEdgeAFewUlpsBehindTheMirroredPlaneAndNotOneInFront",
                "SplitEpsilonBrushOnPlaneTests.CorrectCountsNeitherSideOfTheResidualOnEitherHalfOfTheTest",
                "SplitEpsilonBrushOnPlaneTests.AnEdgeInsideTheBandCountsOnlyUnderStock",
                "SplitEpsilonBrushOnPlaneTests.UnderStockTheResidualsSignChangesTheSplitter",
                "SplitEpsilonBrushOnPlaneTests.UnderCorrectTheSplitterDoesNotDependOnTheResidualsSign",
            ],
            "The numbers above are with SplitBrushSliverSides on the stock side. Most of the spread "
            + "left came from brush splitting, not scoring: a zero-epsilon clip keeps or drops a "
            + "sliver side on its last bits, so a fragment has one side more or fewer; "
            + "SplitBrushSliverSides takes that out under Correct."),

        StockQuirk.SplitBrushSliverSides => new(
            quirk,
            "Brush split hands out sliver sides by rounding",
            "SplitBrush divides each side between the two halves with an epsilon of zero, so a side "
            + "that only touches the splitting plane gives the other half a sliver side a few "
            + "thousandths wide, or not, by the sign of its edge's rounding residual.",
            "A side reaching less than 0.1 across the plane, the band SplitBrush already uses for a "
            + "whole brush, goes whole to the half it is on, so neither half gains a side by noise.",
            CompileTools.Vbsp,
            [
                "SourceSharp.MapTools.Bsp.Csg.BrushGeometry.SplitBrush",
            ],
            QuirkObservation.Demonstrated,
            "@@OBSERVED@@",
            [
                "SplitBrushSliverSidesTests.StockPutsTheSliverOnWhicheverHalfTheEdgeRoundedInto",
                "SplitBrushSliverSidesTests.CorrectGivesNeitherHalfASliverWhicheverWayTheEdgeRounds",
                "SplitBrushSliverSidesTests.AnEdgeInsideTheBandIsCutOnlyUnderStock",
                "SplitBrushSliverSidesTests.BothPoliciesCutASideThatCrossesByAQuarterUnit",
                "SplitBrushSliverSidesTests.UnderStockTheSliverMakesAHalfFaceAPlaneItDoesNotReach",
                "SplitBrushSliverSidesTests.UnderCorrectNeitherHalfFacesAPlaneItDoesNotReach",
            ],
            "@@NOTE@@"),

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
            $"-compliance correct|stock[,+Quirk|-Quirk...] (default correct). {mine.Count} of {all.Count} stock quirks affect {Name(tool)}:\n");
        text.Append("+Quirk takes the Stock side for that quirk alone, -Quirk the Correct side.\n");
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
            text.Append(CultureInfo.InvariantCulture, $"  {q.Quirk} [{Name(q.Tools)}]: {q.Title}\n");
            text.Append(CultureInfo.InvariantCulture, $"      Stock: {q.Stock}\n");
            text.Append(CultureInfo.InvariantCulture, $"      Correct: {q.Correct}\n");
            text.Append(CultureInfo.InvariantCulture, $"      {ObservationLabel(q.Observation)}: {q.Observed}\n");

            if (q.Note is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $"      Note: {q.Note}\n");
            }
        }
    }

    /// <summary>The listing's label for an observation kind.</summary>
    /// <param name="observation">The kind.</param>
    /// <returns>The label.</returns>
    public static string ObservationLabel(QuirkObservation observation) => observation switch
    {
        QuirkObservation.Measured => "Measured against stock",
        QuirkObservation.Demonstrated => "Demonstrated by facts",
        QuirkObservation.ReadFromReference => "Read from the reference",
        _ => throw new ArgumentOutOfRangeException(nameof(observation), observation, null),
    };

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
