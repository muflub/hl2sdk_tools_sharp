//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Options;

/// <summary>
/// Whether a compile reproduces the stock tools' defects or does the right
/// thing instead.
/// </summary>
/// <remarks>
/// <para>
/// The port keeps finding places where vbsp, vvis and vrad are demonstrably
/// wrong rather than merely different: a loop that steps the wrong link, a
/// comparison masked so one branch is unreachable, a float constant that
/// promotes to double and inverts a test. Reproducing them is what makes
/// byte-exact comparison against stock possible, and shipping them is what
/// makes the port useless as a better tool.
/// </para>
/// <para>
/// So it is a switch rather than a decision. <see cref="Correct"/> is the
/// default because a new compile should be right; <see cref="Stock"/> exists so
/// that every parity gate in the suite has something to compare against, and so
/// that a map which depends on a stock quirk can still be built the old way.
/// </para>
/// <para>
/// <b>The two are not "slow and correct" versus "fast and wrong".</b> Every
/// quirk here is a behaviour difference, not a cost difference. Where a quirk
/// genuinely costs something, that is recorded on the individual
/// <see cref="StockQuirk"/> member.
/// </para>
/// </remarks>
public enum CompliancePolicy
{
    /// <summary>
    /// Do the right thing, even where stock does not. The default.
    /// </summary>
    /// <remarks>
    /// Output may differ from stock's, and where it does, the difference is a
    /// stock defect this port declines to reproduce. Every such site is a named
    /// <see cref="StockQuirk"/>, so "what did we change?" has an enumerable
    /// answer rather than a prose one.
    /// </remarks>
    Correct = 0,

    /// <summary>
    /// Reproduce the stock tools' defects exactly, bug for bug.
    /// </summary>
    /// <remarks>
    /// What every byte-exact comparison against stock output must select. Also
    /// the honest choice for rebuilding an existing map whose content was
    /// authored against stock's behaviour.
    /// </remarks>
    Stock = 1,
}

/// <summary>
/// One place where the stock tools do something wrong, and this port can choose
/// not to.
/// </summary>
/// <remarks>
/// <para>
/// A quirk earns a member here only when stock's behaviour is <b>defective</b>,
/// not merely arbitrary. An arbitrary-but-consistent choice -- a tie-break, an
/// iteration order, a magic constant that is simply what it is -- is not a
/// quirk and must be reproduced unconditionally, because "correct" has no
/// meaning for it. The test is whether a sentence of the form "this is wrong
/// because X" can be written with X naming a concrete consequence.
/// </para>
/// <para>
/// Each member's remarks carry the reference site, what stock does, what
/// <see cref="CompliancePolicy.Correct"/> does instead, and how the difference
/// was observed. A member with no observation is a suspicion, not a quirk.
/// </para>
/// </remarks>
public enum StockQuirk
{
    /// <summary>
    /// <c>BaseWindingForPlane</c> normalises with <c>rsqrtss</c> plus one
    /// Newton-Raphson step instead of dividing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference's <c>BaseWindingForPlane</c> calls
    /// <c>VectorNormalize</c>, which on Windows and Linux alike is the SSE
    /// reciprocal-square-root approximation refined once, not an exact divide.
    /// The winding it produces is then scaled by <c>MAX_COORD_INTEGER*4</c> and
    /// clipped with an epsilon of <b>exactly zero</b>, so the approximation's
    /// last bits decide whether a sliver survives the clip.
    /// </para>
    /// <para>
    /// Measured: stock's world-brush shadow casters come to 23,549
    /// triangles and this port's to 23,545 -- and that gap is the <b>net of 53
    /// lost and 49 gained</b>, not a deficit. The disagreeing triangles have
    /// median area zero.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> divides exactly, which is what
    /// <c>WindingArena</c> already does.
    /// <see cref="CompliancePolicy.Stock"/> routes through
    /// <c>Vec3.NormaliseLikeStock</c>.
    /// </para>
    /// <para>
    /// <b>This one switch moves three tools' output.</b>
    /// <c>BaseWindingForPlane</c> is shared by vbsp, vvis and vrad, so a gate
    /// that pins it for one tool has pinned it for all of them. It is also the
    /// only quirk here whose <see cref="CompliancePolicy.Stock"/> side costs
    /// cross-machine determinism: <c>rsqrtss</c>'s result is architecturally
    /// permitted to differ between CPU models, so a stock-compliant compile is
    /// reproducible on one machine and not guaranteed across two.
    /// </para>
    /// </remarks>
    BaseWindingNormalise,

    /// <summary>
    /// <c>AddBrushBevels</c> normalises its edge and its candidate bevel normal
    /// with <c>rsqrtss</c> too, and that decides a stored PLANE TYPE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The edge and candidate normal both go through <c>VectorNormalize</c>, so
    /// both are the estimate rather than a divide — the same instruction
    /// sequence as <see cref="BaseWindingNormalise"/> but a different function,
    /// and separately switchable because a gate needs to attribute a moved
    /// plane to one or the other.
    /// </para>
    /// <para>
    /// <b>Why this is a defect and not merely arbitrary.</b> A regular
    /// octahedron's slanted edge bevel has |x| and |z| mathematically EQUAL.
    /// Divide exactly and the two components come out equal and
    /// <c>PlaneTypeForNormal</c> returns <c>PLANE_ANYX</c>; run the estimate
    /// and they differ in the last bit, so the plane is stored as
    /// <c>PLANE_ANYZ</c> instead. A byte in the PLANES lump is decided by an
    /// approximation's rounding.
    /// </para>
    /// <para>
    /// Measured on <c>ThePlanesLumpMatchesStockElementForElement</c> over 30
    /// catalogue maps. This quirk and
    /// <see cref="BaseWindingNormalise"/> are BOTH needed for the lump to match
    /// and neither suffices alone: with only this one stock-side,
    /// <c>x0_octahedron</c> plane 28's distance is still 7e-4 out, because the
    /// winding POINT the distance is taken from comes from
    /// <c>BaseWindingForPlane</c>; with only that one stock-side, the distance
    /// lands but the normal is still one ulp out and the stored type still
    /// disagrees. With both, all 30 maps match.
    /// </para>
    /// </remarks>
    EdgeBevelNormalise,

    /// <summary>
    /// <c>ReportAreaportalLeak</c>'s second loop steps the wrong portal link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>continue</c> that skips <c>pStart</c>
    /// jumps over the line that assigns <c>s</c>, so the next step uses the
    /// value assigned above the loop -- the complement of the convention every
    /// other portal walk in the reference uses -- and the walk proceeds into the other
    /// node's portal list.
    /// </para>
    /// <para>
    /// Consequence: the areaportal leak report names the wrong portals. No
    /// catalogue map reaches it, so this is reproduced from the source rather
    /// than from an observed difference, and its
    /// <see cref="CompliancePolicy.Correct"/> side is untested against stock by
    /// construction -- stock cannot be made to print the right answer.
    /// </para>
    /// </remarks>
    AreaportalLeakWalk,

    /// <summary>
    /// <c>WindingIsTiny</c>'s edge threshold promotes to double, so a float
    /// <c>0.2f</c> counts as a long edge.
    /// </summary>
    /// <remarks>
    /// <c>EDGE_LENGTH</c> is spelled <c>0.2</c> with no
    /// <c>f</c>, so the comparison happens in double and <c>0.2f</c>
    /// (0.20000000298...) compares as <b>greater</b> than the threshold.
    /// Comparing in float, as the surrounding code's types imply, flips the
    /// result for edges of exactly that length.
    /// </remarks>
    WindingIsTinyEdgePromotion,

    /// <summary>
    /// The <c>.lin</c> leak file records the entity origin, not the nudged
    /// origin the flood actually started from.
    /// </summary>
    /// <remarks>
    /// The reference's leak writer re-reads the <c>origin</c> key when writing, so the
    /// trace it draws begins at a point the flood never used: the flood starts
    /// at origin + 1 unit in z. The line is drawn from the wrong end by one
    /// unit. <see cref="CompliancePolicy.Correct"/> writes the point the flood
    /// used.
    /// </remarks>
    LeakFileUnnudgedOrigin,

    /// <summary>
    /// <c>AddQuad</c> tags its second triangle with <c>id + 1</c>.
    /// </summary>
    /// <remarks>
    /// Stock's static-prop AABB path emits two triangles per quad and gives the
    /// second one the <b>next</b> prop's id, so half of every prop's box is
    /// attributed to its neighbour. Consequence: a shadow traced against the
    /// last prop's second triangles carries an id one past the end of the prop
    /// list. <see cref="CompliancePolicy.Correct"/> gives both triangles the
    /// same id.
    /// </remarks>
    AddQuadSecondTriangleId,

    /// <summary>
    /// <c>ComputeAmbientForLeaf</c> computes a leaf's y and z sample counts and
    /// then overwrites both with the x one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The heuristic wants about one
    /// candidate sample per player-sized volume, so it divides the leaf's
    /// extents by 32, 32 and 64 -- and then clamps with
    /// </para>
    /// <code>
    /// xSize = max(xSize,1);
    /// ySize = max(xSize,1);   // ySize was meant here
    /// zSize = max(xSize,1);   // zSize was meant here
    /// </code>
    /// <para>
    /// so <c>ySize</c> and <c>zSize</c> are computed and thrown away, and the
    /// candidate count is <c>xSize</c> CUBED.
    /// </para>
    /// <para>
    /// Consequence, concrete: a leaf's sample count depends only on its extent
    /// along x. A corridor 32 units wide in x and 2,048 long in y draws ONE
    /// candidate sample where the heuristic asks for 64, and a tall thin shaft
    /// draws one; conversely a leaf wide in x and thin in the other two draws
    /// <c>xSize^3</c> samples for a volume that wanted <c>xSize</c>. The error
    /// runs in both directions, up to the 128 cap either way, and it is the
    /// candidate count -- so it decides how much of the stage's 51.6 % of vrad's
    /// wall clock a given leaf costs, as well as how good its cubes are.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> clamps each axis to its own value,
    /// which is what the three lines plainly mean.
    /// </para>
    /// <para>
    /// Observed by <c>LeafAmbientSampleCountTests</c>, which counts the
    /// candidate samples a stock-compiled map's leaves draw under both
    /// spellings: 2,286 leaves, 9,164 candidates the stock way and 30,899 the
    /// correct way, with 706 leaves disagreeing. Only the stock spelling
    /// reproduces that map's <c>LUMP_LEAF_AMBIENT_LIGHTING</c> byte for byte.
    /// </para>
    /// </remarks>
    LeafAmbientSampleCountAxes,

    /// <summary>
    /// <c>InvRSquared</c> and <c>VectorNormalize</c> use SSE reciprocal
    /// ESTIMATES, so the leaf-ambient surface-light term is machine-dependent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference's <c>InvRSquared</c> computes <c>1/max(1,|v|^2)</c> with
    /// <c>_mm_rcp_ss</c> -- a 12-bit approximation, over an input nudged by
    /// <c>1e-10f</c>, and with NO Newton-Raphson step -- and
    /// <c>VectorNormalize</c> normalises with <c>rsqrtss</c> plus one refinement.
    /// Both sit on the path <c>AddEmitSurfaceLights</c>
    /// takes, and <c>InvRSquared</c> is
    /// also how <c>IsLeafAmbientSurfaceLight</c>'s 512-unit threshold is
    /// computed -- which decides which lights are baked into the cubes at all,
    /// and that decision is written back into <c>LUMP_WORLDLIGHTS</c>.
    /// </para>
    /// <para>
    /// Consequence, concrete, and the same one <see cref="BaseWindingNormalise"/>
    /// carries: <c>rcpss</c> and <c>rsqrtss</c> results are architecturally
    /// permitted to differ between CPU models, so a stock-compliant compile is
    /// reproducible on one machine and not guaranteed across two. At the
    /// classification threshold it is coarser than a last bit: the estimate is
    /// good to about one part in 4,096, so a surface light within that of the
    /// cutoff can be flagged either way.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> divides and normalises exactly.
    /// </para>
    /// <para>
    /// Observed by <c>LeafAmbientSurfaceLightTests</c>, which evaluates the
    /// 512-unit threshold both ways and shows the two reciprocals differ --
    /// 3.81470e-06 exact against the estimate's own value -- on the same input.
    /// </para>
    /// </remarks>
    AmbientCubeReciprocalEstimate,

    /// <summary>
    /// <c>AddSampleToList</c>'s tie-break compares against a variable that is
    /// never assigned, so it can never fire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference's <c>AddSampleToList</c> declares
    /// <c>float nearestNeighborTotal = 0;</c> and nothing ever writes to it, so
    /// the second half of
    /// </para>
    /// <code>
    /// if ( closestDist &lt; nearestNeighborDist ||
    ///      (closestDist == nearestNeighborDist &amp;&amp; totalDC &lt; nearestNeighborTotal) )
    /// </code>
    /// <para>
    /// reads <c>totalDC &lt; 0</c>, and <c>totalDC</c> is a sum of absolute
    /// colour differences and cannot be negative. The companion assignment
    /// inside the body -- <c>nearestNeighborDist = closestDist;</c> with no
    /// <c>nearestNeighborTotal = totalDC;</c> beside it -- is what shows the
    /// pair was meant to be updated together.
    /// </para>
    /// <para>
    /// Consequence, concrete: when two candidate samples tie exactly on
    /// nearest-neighbour distance -- routine in the uniformly lit leaves that
    /// dominate a map, where every cube is identical, <c>maxDC</c> falls under
    /// 1e-4 and is forced to zero, and the score collapses to 0.1 times a
    /// distance -- the sample with LESS colour variation is the one meant to be
    /// evicted, and instead the lower index always is. Which samples survive is
    /// then decided by draw order rather than by content.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> assigns
    /// <c>nearestNeighborTotal</c> alongside <c>nearestNeighborDist</c>, so the
    /// tie-break does what it says.
    /// </para>
    /// <para>
    /// Observed by <c>AmbientSampleListTests</c>, which builds seventeen
    /// samples that tie on distance and differ in colour and shows the two
    /// policies evict different ones.
    /// </para>
    /// </remarks>
    AmbientSampleTieBreakNeverFires,

    /// <summary>
    /// A displacement vertex's stored direction is normalised with
    /// <c>rsqrtss</c>, so the LUMP_DISP_VERTS lump is machine-dependent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DispMapToCoreDispInfo</c> and the
    /// identical three lines in <c>EmitInitialDispInfos</c>. Both
    /// take <c>v = fieldVector * dist + offset</c>, record
    /// <c>VectorLength(v)</c> -- an exact <c>sqrt</c> -- and then store
    /// <c>VectorNormalize(v)</c>, which is the SSE reciprocal-square-root
    /// estimate refined once. The estimate's result lands straight in the BSP as
    /// <c>CDispVert::m_vVector</c>; unlike
    /// <see cref="BaseWindingNormalise"/> and
    /// <see cref="EdgeBevelNormalise"/>, nothing downstream re-derives it.
    /// </para>
    /// <para>
    /// <b>Why this is a defect and not merely arbitrary.</b> <c>rsqrtss</c>'s
    /// result is architecturally permitted to differ between CPU models, so the
    /// same VMF compiled on two machines can produce two different
    /// LUMP_DISP_VERTS lumps -- a compile that is not reproducible, for a
    /// quantity where an exact divide is available and costs nothing. The
    /// stored vector is also not a unit vector: the engine and vrad both
    /// reconstruct a vertex as <c>m_vVector * m_flDist</c>, so every
    /// displacement vertex in every Source map sits up to <c>dist * 1.5e-7</c>
    /// off where the author put it.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> divides exactly.
    /// <see cref="CompliancePolicy.Stock"/> routes through
    /// <c>Vec3.NormaliseLikeStock</c>, which is what a byte-exact comparison
    /// against a stock-compiled BSP needs.
    /// </para>
    /// <para>
    /// Observed on the 16-map displacement catalogue this lane built:
    /// <c>TheDispVertsLumpMatchesStockElementForElement</c> passes under
    /// <see cref="ComplianceOptions.Stock"/> and fails under
    /// <see cref="ComplianceOptions.Correct"/>, with the disagreement confined
    /// to the direction components and the distances bit-identical either way.
    /// </para>
    /// </remarks>
    DispVertNormalise,

    /// <summary>
    /// A displacement whose luxel layout runs across its texinfo's lightmap
    /// axes has them swapped into a new texinfo
    /// — but only on the MAP face, after the BSP face was emitted,
    /// so the swap never reaches LUMP_FACES and
    /// <c>CompactTexinfos</c> deletes the copy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a defect.</b> The face's lightmap vectors are what vrad
    /// uses to map world positions into the face's luxel grid for displacement
    /// faces (the reference fills <c>worldToLuxelSpace</c> from it, and its radial pass and detail-prop lighting read it), and
    /// the grid itself is laid out by the displacement's own luxel
    /// coordinates. Unswapped, the two disagree by a transpose: on
    /// <c>p3f_swap</c> the texinfo projects to 9 x 33 luxels while the face
    /// stores 33 x 9. The swap exists precisely to make them agree, and its
    /// comment names the lighting bug it was written for.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> repoints the base face at the
    /// swapped copy (one copy per original texinfo, numbered in face order),
    /// so it survives compaction. <see cref="CompliancePolicy.Stock"/> leaves
    /// the face and the table as stock does.
    /// </para>
    /// </remarks>
    DispLightmapSwapDropped,

    /// <summary>
    /// A displacement's contribution to <c>world_mins</c>/<c>world_maxs</c>
    /// is its FLAT base quad puffed by 0.1 (<c>ComputeDispInfoBounds</c>,
    /// reusing the neighbour finder's
    /// <c>GetDispBox</c>), so its height never counts.
    /// </summary>
    /// <remarks>
    /// The reference's <c>AddDispsToBounds</c> comment is "Adds the displacement surfaces
    /// in the world to the bounds": a terrain raised
    /// above every brush leaves the world box short of it.
    /// <see cref="CompliancePolicy.Correct"/> uses the box of the displaced
    /// vertices (the quad-tree root box); <see cref="CompliancePolicy.Stock"/>
    /// the puffed base quad.
    /// </remarks>
    DispWorldBoundsBaseQuad,

    /// <summary>
    /// A displacement vertex normal is the MEAN of its fan's unit triangle
    /// normals and is never renormalised (<c>CalcNormalFromEdges</c>), so it is shorter than one wherever the fan
    /// is not coplanar.
    /// </summary>
    /// <remarks>
    /// vrad blends these normals bilinearly and normalises only the blend,
    /// and its displacement lighting sums them
    /// before normalising — in both, a short normal is a smaller WEIGHT, so a
    /// crease vertex pulls less than its neighbours for no geometric reason.
    /// Every consumer treats the value as a direction.
    /// <see cref="CompliancePolicy.Correct"/> normalises the mean;
    /// <see cref="CompliancePolicy.Stock"/> keeps it short. vbsp writes no
    /// normal, so only vrad's output moves.
    /// </remarks>
    DispVertexNormalMeanUnnormalised,

    /// <summary>
    /// <c>IsLowerLeaf</c>'s near-equal branch is dead: both of its outcomes
    /// return true, so two water surface leaves at the same height sort by
    /// the order they were found in -- reversed -- instead of by distance.
    /// </summary>
    /// <remarks>
    /// The branch tests
    /// <c>newleaf.surfaceDist &lt; currentleaf.surfaceDist</c>, returns true if
    /// so, and then falls through to <c>return true</c> anyway. The ordering
    /// decides which water volume is emitted first, so the order of the
    /// <c>fluid</c> blocks in the world's keydata and of LEAFWATERDATA.
    /// <see cref="CompliancePolicy.Correct"/> sorts near-equal leaves lower
    /// distance first, as the branch intends.
    /// </remarks>
    WaterLeafSortTie,

    /// <summary>
    /// A water volume's fluid always gets the surface property
    /// <c>water</c>: the "material override" reads a texinfo id that is
    /// hard-coded to -1.
    /// </summary>
    /// <remarks>
    /// The reference seeds <c>int waterSurfaceTexInfoID = -1;</c> and then
    /// <c>if ( waterSurfaceTexInfoID &gt;= 0 )</c>. <see cref="CompliancePolicy.Correct"/>
    /// takes the surface property of the volume's water surface material,
    /// falling back to <c>water</c> when it has none.
    /// </remarks>
    FluidSurfacePropIgnored,

    /// <summary>
    /// A brush entity's shell mass counts the placeholder areas: 1 for the
    /// sentinel entry, and 2 for a faceless model's first side.
    /// </summary>
    /// <remarks>
    /// The reference's shell-mass pass seeds <c>proplist</c> with those areas so that
    /// SOME property wins the vote, then adds them to
    /// <c>totalArea</c>, which is the surface area in the shell formula
    /// (<c>mass = totalArea * thickness * density</c>). A model with faces is
    /// one square inch heavier than its faces; a faceless one weighs three
    /// square inches of shell. <see cref="CompliancePolicy.Correct"/> sums only
    /// the faces, and weighs a faceless shell model by its volume instead.
    /// </remarks>
    ShellMassSentinelArea,

    /// <summary>
    /// A brush crossing a water surface is added whole to the water volume's
    /// collision, so the fluid extends above its own surface.
    /// </summary>
    /// <remarks>
    /// The reference's own "BUGBUG ... Right now map makers
    /// must cut such brushes. It could be automatically cut by adding the
    /// surface plane to the list for each brush before calling
    /// ConvexFromPlanes()". <see cref="CompliancePolicy.Correct"/> does exactly
    /// that, for the brushes that actually reach above the surface.
    /// </remarks>
    WaterBrushNotClippedAtSurface,

    /// <summary>
    /// A node's area is written to LUMP_NODES before anything has set it, so
    /// every <c>dnode_t::area</c> in a stock BSP is 0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>EmitDrawNode_r</c> copies <c>node-&gt;area</c> into the lump;
    /// <c>SetNodeAreaIndices_R</c>, whose comment
    /// says it "sets node_t::area for non-leaf nodes (this allows an
    /// optimization in the renderer)", runs at the END of
    /// <c>EmitAreaPortals</c>, which <c>WriteBSP</c>
    /// calls after the walk. The values land in the
    /// tree and never reach the file. Measured: all 47/27/67 nodes of
    /// l1_areaportal, l1_sealed_room and l2_areaportal_between_pools read 0 in
    /// stock's own output.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> writes the node areas
    /// <c>SetNodeAreaIndices_R</c> computes (a node's area when both children
    /// agree, else -1) by patching them in after the walk.
    /// </para>
    /// </remarks>
    NodeAreaWrittenBeforeSet,

    /// <summary>
    /// <c>WriteFogVolumeIDs</c> loops over a face count that is still zero, so
    /// no face ever gets a fog volume and every water top keeps its own
    /// texinfo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>for (i = pModel-&gt;firstface; i &lt; pModel-&gt;firstface +
    /// pModel-&gt;numfaces; i++)</c> runs from
    /// <c>EmitWaterVolumesForBSP</c> inside <c>WriteBSP</c>;
    /// <c>numfaces</c> is set by <c>EndModel</c>
    /// after <c>WriteBSP</c> returns, and until
    /// then is the zero of the cleared <c>dmodels</c> global. The loop body --
    /// which would ALSO read <c>leafWaterDataID</c> before
    /// <c>EmitPhysCollision</c> assigns it, and <c>dplanes</c> before
    /// <c>EmitPlanes</c> writes it -- never runs. Measured: every face of
    /// l1_water_volume, l1_water_and_fog_leaves and
    /// l2_areaportal_between_pools has <c>surfaceFogVolumeID</c> -1 in stock's
    /// output.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> runs the loop over the model's
    /// faces with each water leaf's real fog volume and the map's own planes.
    /// </para>
    /// </remarks>
    FogVolumeLoopOverNoFaces,

    /// <summary>
    /// <c>SolveInverseQuadratic</c> divides by the determinant with a
    /// reciprocal and three multiplies, not three divides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference's <c>SolveInverseQuadratic</c> writes three divisions by
    /// <c>det</c>. The reference build's x64 <c>vrad.exe</c> is built <c>/fp:fast</c>,
    /// and the numbers it writes are those of <c>num * (1.0f / det)</c>: on
    /// the texlight fixture, a <c>_fifty_percent_distance</c> 96 /
    /// <c>_zero_percent_distance</c> 256 light's linear and quadratic terms are
    /// 0.00250157341 and 0.000185510085 in stock's LUMP_WORLDLIGHTS, the
    /// reciprocal form gives exactly those, and the three divides give
    /// 0.00250157318 and 0.000185510071 (a C probe of both forms, gcc SSE,
    /// no excess precision). It is a compiler artefact rather than a source
    /// bug, and it moves the worldlight bytes and every lit luxel of such a
    /// light.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> divides as the source says.
    /// <see cref="CompliancePolicy.Stock"/> multiplies by the reciprocal.
    /// </para>
    /// </remarks>
    InverseQuadraticReciprocal,

    /// <summary>
    /// A light's direction takes the reference build's cosine of a right angle:
    /// <c>sin((double)M_PI)</c>, not <c>cos((double)M_PI / 2)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SetupLightNormalFromProps</c>
    /// casts <c>cos(angle/180*M_PI)</c> to float. For a yaw of 90 or a pitch
    /// of -90 -- a spotlight pointing down, the commonest light_spot there is --
    /// the correctly rounded answer is 6.123234e-17 and stock's LUMP_WORLDLIGHTS
    /// holds 1.2246469e-16 (the texlight fixture, lights 4 and 5). That is the
    /// C runtime's cosine, not anything in the source.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> uses the correctly rounded cosine.
    /// <see cref="CompliancePolicy.Stock"/> returns the observed value at
    /// exactly +/-pi/2 and the correctly rounded one everywhere else.
    /// </para>
    /// </remarks>
    CrtCosineAtRightAngle,

    /// <summary>
    /// vrad's own <c>VectorNormalize</c> calls use the <c>rsqrtss</c> estimate
    /// plus one Newton-Raphson step, not a divide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same instruction sequence as <see cref="BaseWindingNormalise"/>
    /// at different vrad sites: <c>PairEdges</c>'s
    /// corner normals, a light's target direction,
    /// the phong normal (and its four-wide twin) and the bump basis. Those
    /// normals decide which patches the edge rule re-chops, which way a spot
    /// points in LUMP_WORLDLIGHTS, and every dot product the lighting takes.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> divides exactly.
    /// <see cref="CompliancePolicy.Stock"/> routes through
    /// <c>Vec3.NormaliseLikeStock</c>. Machine-dependent on the Stock side, as
    /// every <c>rsqrtss</c> quirk is.
    /// </para>
    /// </remarks>
    VradVectorNormalise,

    /// <summary>
    /// The direct-light gather takes reciprocals and reciprocal square roots
    /// with the <c>rcpps</c>/<c>rsqrtps</c> estimates plus one Newton step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ReciprocalSIMD</c> and <c>ReciprocalSqrtSIMD</c>
    /// are what <c>GatherSampleStandardLightSSE</c>,
    /// <c>GatherSampleAmbientSkySSE</c>'s
    /// normalisation, the four-wide phong normal
    /// and the skybox recursion's direction
    /// divide with. The estimates are architecturally
    /// allowed to differ between CPU models, so stock's lightmaps depend on the
    /// machine that compiled them.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> divides and takes square roots
    /// exactly. <see cref="CompliancePolicy.Stock"/> issues the same estimate
    /// instructions and Newton steps, which reproduces stock on the same CPU
    /// family and nowhere else.
    /// </para>
    /// </remarks>
    GatherReciprocalEstimate,

    /// <summary>
    /// A spotlight's <c>_exponent</c> is applied in fixed point with two
    /// fractional bits: 1.3 behaves as 1.25.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>PowSIMD</c> is <c>Pow_FixedPoint_Exponent_SIMD(x,
    /// (int)(4.0*exponent))</c>; stock's own comment says
    /// fractions other than quarters are dropped. The spot fringe of
    /// the gather pass is its caller.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> takes the real power.
    /// <see cref="CompliancePolicy.Stock"/> reproduces the fixed-point one, which
    /// is deterministic (square roots and multiplies) except for a negative
    /// exponent's final <c>rcpps</c>.
    /// </para>
    /// </remarks>
    SpotExponentQuarterSteps,

    /// <summary>
    /// The 3D-skybox recursion of a sky ray is decided by the FIRST of four
    /// sample points for all four.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TestLine_DoesHitSky</c> looks up the leaf of <c>start.Vec(0)</c>
    /// and, if that area has no sky camera, recurses
    /// into every skybox for all four lanes. A group straddling a skybox
    /// area's boundary therefore treats its other three samples as being where
    /// the first is.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> decides per sample.
    /// <see cref="CompliancePolicy.Stock"/> decides by lane zero.
    /// </para>
    /// </remarks>
    SkyboxRecursionFromLaneZero,

    /// <summary>
    /// <c>CanLeafTraceToSky</c> casts the last of the 162 sky directions three
    /// times.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The directions go four at a time with each index clamped to 161,
    /// so the last group is 160, 161, 161,
    /// 161. The answer is "any ray reached sky", which the duplicates cannot
    /// change -- only the ray count moves, by two per probed leaf.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> casts each direction once.
    /// <see cref="CompliancePolicy.Stock"/> casts the duplicates.
    /// </para>
    /// </remarks>
    SkyProbeTailDoubleCount,

    /// <summary>
    /// A second <c>light_environment</c>, whose light is discarded, still sets
    /// the sun's angular extent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ParseLightEnvironment</c> reads <c>SunSpreadAngle</c>
    /// before it checks whether a sun already exists,
    /// so the LAST light_environment's spread applies to the
    /// FIRST one's sun.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> takes the spread only from the
    /// light_environment that becomes the sun.
    /// <see cref="CompliancePolicy.Stock"/> lets any of them overwrite it.
    /// </para>
    /// </remarks>
    SecondSunSpreadAngleWins,

    /// <summary>
    /// The monotonic falloff fit tests the curve's slope at x = 1, not at its
    /// start point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SolveInverseQuadraticMonotonic</c>'s comment says it enforces "the
    /// sign of the derivative at the start point" and the code tests
    /// <c>2.0*a+b</c>, the derivative at x = 1.
    /// The only caller starts at x = 0, where the derivative is <c>b</c>, so
    /// the blend toward a straight line stops at a different step and a
    /// <c>_fifty_percent_distance</c> light's attenuation terms can move.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> tests <c>2*a*x1 + b</c>.
    /// <see cref="CompliancePolicy.Stock"/> tests <c>2*a + b</c>.
    /// </para>
    /// </remarks>
    MonotonicDerivativeAtOne,

    /// <summary>
    /// <c>Cubemap_FindClosestCubemap</c> measures each sample's distance with
    /// <c>NormalizeInPlace</c>, the <c>rsqrtss</c> estimate, rather than a
    /// length.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The nearest-sample loop measures <c>flDist =
    /// vecDelta.NormalizeInPlace()</c> returns <c>sqrlen * invlen</c> with
    /// <c>invlen</c> the refined estimate, and the nearest sample in front of
    /// the face wins on that value. Two samples at nearly the same distance can
    /// therefore be ordered differently on two CPUs, which moves a side to a
    /// different <c>_x_y_z</c> patch — a different material name in
    /// TEXDATA_STRING_DATA and a different VMT in the pak. The reference's fallback loop
    /// uses <c>Length()</c> and is exact either way.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> uses the exact length and an
    /// exact divide for the direction; <see cref="CompliancePolicy.Stock"/>
    /// routes through <c>Vec3.NormaliseLikeStock</c>.
    /// </para>
    /// </remarks>
    CubemapDistanceNormalise,

    /// <summary>
    /// A conforming detail prop's basis is built with <c>VectorNormalize</c>,
    /// the <c>rsqrtss</c> estimate, so its stored angles depend on the CPU.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The surface normal, then
    /// the two cross products that make the tangent basis, are each normalised
    /// with the estimate refined once; <c>MatrixToAngles</c> turns that basis
    /// into the pitch, yaw and roll written to LUMP_GAME_LUMP's <c>dprp</c>.
    /// The last bits of every non-upright detail prop's angles are therefore
    /// an approximation's, and may differ between CPU models for the same map.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> divides exactly;
    /// <see cref="CompliancePolicy.Stock"/> routes through
    /// <c>Vec3.NormaliseLikeStock</c>.
    /// </para>
    /// </remarks>
    DetailOrientationNormalise,

    /// <summary>
    /// vbsp's "does this material have <c>$envmap</c>" test reads a
    /// <c>patch</c> VMT raw, so a patched specular material never gets its
    /// per-cubemap patch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The envmap test's loader calls <c>LoadFromFile</c>, no
    /// <c>ExpandPatchFile</c>. The keys a patch inserts live under its
    /// <c>insert</c> section, and the root is <c>"patch"</c>, so
    /// <c>DoesMaterialOrDependentsUseEnvmap</c> answers false and the side keeps
    /// the unpatched material: the engine then has no <c>env_cubemap</c> to
    /// resolve for it. MEASURED on <c>l2_cubemap_on_water_and_patch</c>: stock
    /// writes no <c>p3g/patchedmetal_0_0_160</c> patch for its four walls.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> expands the patch (vbsp's own
    /// <c>ExpandPatchFile</c>) before asking, and walks the expanded material
    /// for a REPLACE patch, whose <c>include</c> is then the patch itself —
    /// a chain the engine's reader resolves.
    /// </para>
    /// </remarks>
    CubemapIgnoresPatchMaterials,

    /// <summary>
    /// vbsp's <c>ExpandPatchFile</c> drops a patch's <c>replace</c> section
    /// when it also has an <c>insert</c>.
    /// </summary>
    /// <remarks>
    /// After the insert is
    /// applied the tree is reassigned to the INCLUDED material, and the
    /// replace lookup that follows searches that, not the patch. Consequence:
    /// the water <c>$bottommaterial</c> read and the
    /// WorldVertexTransition fixup see a material the engine never draws.
    /// <see cref="CompliancePolicy.Correct"/> takes both sections from the patch.
    /// </remarks>
    PatchExpandInsertDropsReplace,

    /// <summary>
    /// vbsp's <c>ExpandPatchFile</c> never resolves a patch with neither
    /// <c>insert</c> nor <c>replace</c>.
    /// </summary>
    /// <remarks>
    /// The tree is reassigned only
    /// inside the two branches, so an empty patch stays <c>"patch"</c>, the
    /// loop re-reads the include ten times and warns "Infinite recursion in
    /// patch file?". The engine's reader resolves it to its include.
    /// <see cref="CompliancePolicy.Correct"/> does the same.
    /// </remarks>
    PatchExpandEmptyPatchNeverResolves,

    /// <summary>
    /// <c>Cubemap_AddUnreferencedCubemaps</c>' "already added" test compares a
    /// texture name with file names and never matches.
    /// </summary>
    /// <remarks>
    /// The "already added" test compares <c>maps/m/c1_2_3</c> against
    /// <c>materials/maps/m/c1_2_3.vtf</c>. Every sample is appended again;
    /// the pak writer's <c>FileExistsInPak</c> hides it, so
    /// the pak is IDENTICAL under both policies — measured on all 11 3g
    /// entries. <see cref="CompliancePolicy.Correct"/> compares file names.
    /// </remarks>
    CubemapUnreferencedNeverMatches,

    /// <summary>
    /// An overlay whose face list exactly fills its 64 slots (256 for a water
    /// overlay) is a fatal error.
    /// </summary>
    /// <remarks>
    /// The reference tests
    /// <c>nFaceCount &gt;= OVERLAY_BSP_FACE_COUNT</c> against arrays of exactly
    /// that size, so a list that fits is refused. <see cref="CompliancePolicy.Correct"/>
    /// refuses only a list that does not fit.
    /// </remarks>
    OverlayFaceLimitOffByOne,

    /// <summary>
    /// <c>-replacematerials</c> is not applied to an <c>info_overlay</c>'s
    /// material.
    /// </summary>
    /// <remarks>
    /// The reference reads the <c>material</c> key raw,
    /// while brush sides and water overlays
    /// go through <c>ReplaceMaterialName</c>, so a
    /// replacement table swaps a map's materials everywhere except on its
    /// decals-by-overlay. <see cref="CompliancePolicy.Correct"/> replaces it too.
    /// </remarks>
    OverlayMaterialNotReplaced,

    /// <summary>
    /// Any studio model version is accepted: the version is overwritten with
    /// 48 before it is checked.
    /// </summary>
    /// <remarks>
    /// The reference's model loader calls
    /// <c>Studio_ConvertStudioHdrToNewVersion</c>, which ends by slamming the
    /// version to <c>STUDIO_VERSION</c>,
    /// so the <c>version != STUDIO_VERSION</c> refusal that follows is dead
    /// code. Versions 44-47 are compatible (the conversion's own comment, and
    /// HL2's props are 44); a model of any OTHER version has a different
    /// header and is read as garbage. <see cref="CompliancePolicy.Correct"/>
    /// accepts 44-48 and refuses the rest as unreadable.
    /// </remarks>
    StudioVersionSlam,

    /// <summary>
    /// <c>ComputeIndirectLightingAtPoint</c> reuses one surface enumerator for
    /// every sample direction, so each ray only accepts hits nearer than the
    /// previous ray's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference constructs <c>CLightSurface surfEnum</c>
    /// once, and <c>FindIntersection</c> resets only the
    /// displacement counter, never <c>m_HitFrac</c>. A leaf face or
    /// displacement further along the new ray than the LAST ray's hit fraction
    /// is rejected, so a static prop vertex's indirect
    /// light depends on the order of the 40 sample directions and misses
    /// surfaces that the fresh enumerator would find. Node faces are unaffected.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> starts every ray from a fresh
    /// enumerator, as <c>CalcRayAmbientLighting</c> already does.
    /// </para>
    /// </remarks>
    IndirectSurfaceEnumeratorReused,

    /// <summary>
    /// A static prop vertex that sits in solid is relit without the prop's
    /// <c>IGNORE_NORMALS</c> and self-shadowing flags.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CVradStaticPropMgr::ComputeLighting</c> lights a good vertex with
    /// <c>skip_prop</c> and <c>nFlags</c> and its
    /// indirect term with the prop's <c>STATIC_PROP_IGNORE_NORMALS</c>.
    /// The bad-vertex relight calls the same
    /// two functions with their defaults, so on an <c>IGNORE_NORMALS</c> prop the
    /// recovered vertices are lit by their normals and every other vertex is not,
    /// and a <c>NO_SELF_SHADOWING</c> prop shadows its own recovered vertices.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> relights a bad vertex with the
    /// prop's flags.
    /// </para>
    /// </remarks>
    StaticPropBadVertexDropsPropFlags,

    /// <summary>
    /// A polygon form factor whose edge sine rounds past 1 is thrown away
    /// whole, and the transfer with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FormFactorPolyToDiff</c> takes each edge's sine as the length of the
    /// cross product of two unit vectors and guards <c>asin</c> with
    /// <c>if (flSinAlpha &lt; -1.0f || flSinAlpha &gt; 1.0f) return 0.0f;</c>
    /// The guard is as written in the reference. Rounding -- and on stock's path the
    /// <c>rsqrtss</c> estimate, whose returned "length" is
    /// <c>sqrlen * invlen</c> -- can put a right-angled edge's sine an ulp
    /// above 1. The return discards every other edge's contribution too, and
    /// <c>MakeTransfer</c> drops a form factor at or below zero,
    /// so a near patch that should receive the most
    /// light from its neighbour receives none.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> clamps the sine to 1 and keeps
    /// summing. <see cref="CompliancePolicy.Stock"/> returns 0.
    /// </para>
    /// </remarks>
    FormFactorSineAboveOne,

    /// <summary>
    /// The transfer rays are normalised with the <c>rcpps</c> estimate plus a
    /// Newton step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FlushStreamEntry</c> scales each ray of the transfer stream by
    /// <c>ReciprocalSaturateSIMD(length)</c> before
    /// tracing it to its length. The estimate differs between CPU vendors, so
    /// which patch pairs graze an edge -- and so which transfers exist -- is
    /// machine-dependent. Measured on the corpus: an exact divide moves up to
    /// 73 of 195,455 transfers on <c>l1_func_detail</c> against stock's log.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> divides.
    /// <see cref="CompliancePolicy.Stock"/> multiplies by the estimate.
    /// Machine-dependent on the Stock side, as every <c>rcpps</c> quirk is.
    /// </para>
    /// </remarks>
    TransferRayReciprocalEstimate,

    /// <summary>
    /// The patch-to-patch plane test takes a smoothed patch's phong normal
    /// against its flat plane's distance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TestPatchToPatch</c> keeps a source only when
    /// <c>DotProduct( patch2-&gt;origin, patch-&gt;normal ) &gt; patch-&gt;planeDist + PLANE_TEST_EPSILON</c>
    /// The test is as written in the reference. <c>patch-&gt;normal</c> is the PHONG normal of
    /// a child patch on a smoothed face while
    /// <c>planeDist</c> is the flat plane's distance, so the "plane" tested is
    /// neither the face's nor one through the patch: it is tilted about the
    /// world origin, and whether a source passes depends on how far both lie
    /// from (0, 0, 0). A curved surface far from the origin gains or loses
    /// transfers for no geometric reason.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> tests against the patch's own
    /// plane normal, which is the same test wherever the face is not smoothed.
    /// <see cref="CompliancePolicy.Stock"/> uses the phong normal.
    /// </para>
    /// </remarks>
    VisPlaneTestPhongNormal,

    /// <summary>
    /// A <c>-fast</c> displacement samples each luxel half a luxel off, and its
    /// last row and column off the surface entirely, so they come out black.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>BuildDispSamplesAndLuxels_DoFast</c>
    /// adds the full path's half-step to coordinates on the LUXEL grid
    /// (<c>step = 1 / (w - 1)</c>). The last column's coordinate is then
    /// <c>1 + step / 2</c>, <c>DispUVToSurfPoint</c>/<c>DispUVToSurfNormal</c>
    /// return without writing, the sample
    /// keeps the <c>calloc</c>'d zero position and normal, and a zero normal
    /// receives no light. Measured on the 18 p3f-t displacement maps: every
    /// displacement's last row and column is exactly zero in stock <c>-fast</c>
    /// output and in none of the full-path compiles.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> samples each luxel at its own
    /// position (<c>u = s / (w - 1)</c>), as <c>BuildDispLuxels</c> places it
    /// and as the flat <c>-fast</c> path samples.
    /// <see cref="CompliancePolicy.Stock"/> keeps the half step.
    /// </para>
    /// </remarks>
    DispFastSamplesPastEdge,

    /// <summary>
    /// A <c>-fast</c> displacement's samples have zero area, so its patches
    /// receive no direct light and it reflects nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>BuildDispSamplesAndLuxels_DoFast</c>
    /// <c>calloc</c>s the samples and never sets <c>area</c>;
    /// <c>AddSampleToPatch</c> accumulates
    /// <c>area</c> and <c>area * light</c>, so a displacement patch's sample
    /// area stays zero and its direct light is never averaged in. The flat
    /// <c>-fast</c> path sets <c>worldAreaPerLuxel</c>.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> gives each sample the texinfo's
    /// world area per luxel, as the flat fast path does.
    /// <see cref="CompliancePolicy.Stock"/> leaves it zero.
    /// </para>
    /// </remarks>
    DispFastSampleAreaZero,

    /// <summary>
    /// <c>SampleRadial</c>'s edge test lets a point one past the last luxel
    /// column or row through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference tests <c>u &gt; rad-&gt;w</c> and
    /// <c>v &gt; rad-&gt;h</c>, so <c>u == w</c> reads the first luxel of the
    /// next row and <c>v == h</c> reads the zeroed tail of a
    /// <c>SINGLEMAP</c>-sized array (black, or red under <c>-rederrors</c>).
    /// Only a luxel position that rounds onto the far edge reaches it, which a
    /// luxel on its own face's grid does not; a degenerate lightmap frame can.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> treats <c>u == w</c> and
    /// <c>v == h</c> as off the grid.
    /// <see cref="CompliancePolicy.Stock"/> reads the wrong luxel.
    /// </para>
    /// </remarks>
    SampleRadialEdgeOffByOne,

    /// <summary>
    /// The bounce filter treats every neighbour's patches as bumped whenever
    /// the face being filtered is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>BuildPatchRadial</c> computes <c>neighborNeedsBumpmap</c> from
    /// <c>facenum</c> instead of the neighbour and then
    /// passes <c>needsBumpmap</c> for both arguments anyway.
    /// A bumped face next to an unbumped one therefore takes the neighbour's
    /// patches' bump-direction light -- zero -- at full weight in its three
    /// bump maps, instead of the flat light times <c>1/sqrt(3)</c> that
    /// <c>AddBouncedToRadial</c>'s mixed branch exists for.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> asks the neighbour.
    /// <see cref="CompliancePolicy.Stock"/> asks the face itself.
    /// </para>
    /// </remarks>
    PatchRadialNeighbourBumpFromSelf,

    /// <summary>
    /// Under <c>-fast</c>, every light style of a face is written from style
    /// slot zero's light.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FinalLightFace</c>'s <c>-fast</c> branch reads
    /// <c>fl-&gt;light[0][iBump][j]</c> inside the loop over styles,
    /// so a switchable light's style comes out as a
    /// copy of the base lighting and the switch does nothing in a fast compile.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> reads the style being written.
    /// <see cref="CompliancePolicy.Stock"/> reads slot zero.
    /// </para>
    /// </remarks>
    FastFinalLightStyleZero,

    /// <summary>
    /// <c>-luxeldensity</c> recomputes the LDR faces' lightmap extents but not
    /// the HDR faces'.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>RadWorld_Start</c> caps the texinfo lightmap axes and then calls
    /// <c>UpdateAllFaceLightmapExtents</c>, which walks <c>dfaces</c>.
    /// An HDR pass lights <c>dfaces_hdr</c>, copied
    /// from <c>dfaces</c> earlier in <c>VRAD_LoadBSP</c>, so under <c>-hdr</c>
    /// every face keeps the extents of the OLD axes while its samples are
    /// placed with the new ones. <c>-both</c> hides it: its HDR pass reloads
    /// the map the LDR pass already rewrote.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> recomputes both face lumps.
    /// <see cref="CompliancePolicy.Stock"/> leaves the HDR faces stale.
    /// </para>
    /// </remarks>
    LuxelDensityLeavesHdrFacesStale,

    /// <summary>
    /// vrad converts a light's degrees to radians by multiplying by the float
    /// <c>1/180</c>, not by dividing by 180.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference's source says <c>angle/180*M_PI</c>; its x64 build,
    /// compiled <c>/fp:fast</c>, evaluates <c>angle * (1.0f/180)</c> -- a float
    /// reciprocal, one rounding off -- before the double multiply by pi.
    /// MEASURED on ss_sandbox's light_environment (yaw 300, pitch -40): stock
    /// wrote the sun normal (0.38302240, -0.66341382, ...), which the exact
    /// division misses by six and two float ulps, and the reciprocal form
    /// reproduces in both components. A 300-degree yaw becomes 300.000016.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> divides, as the source says.
    /// <see cref="CompliancePolicy.Stock"/> multiplies by the reciprocal.
    /// </para>
    /// </remarks>
    DegreesToRadiansByReciprocal,

    /// <summary>
    /// The supersampling gradient reads the intensity of luxels that have no
    /// sample from uninitialised stack.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>BuildSupersampleFaceLights</c> <c>stackalloc</c>s
    /// <c>pSampleIntensity</c> and writes only the
    /// luxels that have a sample; <c>ComputeLightmapGradients</c> then compares
    /// every edge sample of a non-rectangular face with its missing neighbours'
    /// garbage, and supersamples on the result. Undefined, so not reproducible
    /// bit for bit: the Stock side models the garbage as never matching, so
    /// such a sample is always supersampled.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> leaves luxels with no sample out
    /// of the gradient. <see cref="CompliancePolicy.Stock"/> supersamples every
    /// sample next to one.
    /// </para>
    /// </remarks>
    SupersampleGradientReadsUninitialised,

    /// <summary>
    /// The earlier reference build's physics library cooks collision in single
    /// precision and normalises with the <c>rsqrtss</c>/<c>rsqrtps</c>
    /// estimates, so the physics lump depends on the CPU and is rounded twice
    /// where the later reference build rounds once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Observed by comparing the two linux64 builds of the reference physics
    /// library: the same cook path, the same compiler and flags, and exactly
    /// one difference, the library's floating-point typedef -- <c>float</c>
    /// in the earlier build, <c>double</c> in the later one. The earlier
    /// build lets <c>-ffast-math</c> lower <c>1/sqrtf</c> to the estimate
    /// instructions (the point normalise and the four-lane hesse normalise),
    /// whose results are implementation-defined and may differ between CPU
    /// vendors.
    /// A cube's <c>mass_center</c> and <c>rotation_inertia</c> already differ
    /// between the builds (15 of 440 bytes), reproduced bit for bit from each
    /// build's arithmetic.
    /// </para>
    /// <para>
    /// <b>Why this is a defect.</b> The same map cooked on two machines can give
    /// two different physics lumps, for a quantity where exact IEEE arithmetic
    /// costs nothing; and the float build rounds intermediate sums that the
    /// library's floating-point typedef would have carried in full.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Stock"/> selects the managed cooker's
    /// <c>StockPrecision</c> (the earlier reference build's float arithmetic,
    /// estimate included, for byte parity with that build on the CPU the
    /// goldens were cut on).
    /// <see cref="CompliancePolicy.Correct"/> selects <c>CorrectPrecision</c>:
    /// the later reference build's double arithmetic in compiled order,
    /// identical on every CPU. Plan ruling Q18.
    /// </para>
    /// </remarks>
    CollisionCookerSinglePrecision,

    /// <summary>
    /// IVP's inertia integral divides 0 by 0 on a zero-length ledge edge, and the
    /// NaN is written into the compact surface's <c>rotation_inertia</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference physics library's per-triangle moment integrand for a
    /// compact ledge skips an edge only when
    /// <c>len * eps &gt; |d[axis]|</c>; a zero-length edge fails that test and
    /// reaches <c>d[b] / d[a] = 0/0</c>. The per-axis loop then takes the
    /// divide because its comparison is unordered on
    /// NaN, and every axis of <c>rotation_inertia</c> is NaN. Zero-length edges
    /// exist whenever two hull points collapse to one float: the later
    /// reference build de-duplicates the point soup in double and rounds the
    /// survivors to float only when it writes the ledge.
    /// </para>
    /// <para>
    /// Observed: 6 of dm_lockdown's 2,239 brushes come out of the later
    /// reference build's physics library with
    /// <c>rotation_inertia = (NaN, NaN, NaN)</c>; the
    /// managed cooker reproduces all six bit for bit under
    /// <see cref="CompliancePolicy.Stock"/> semantics and writes finite values
    /// under <see cref="CompliancePolicy.Correct"/>.
    /// </para>
    /// <para>
    /// <b>Why this is a defect.</b> A zero-length edge bounds no area and
    /// contributes nothing to the integral; the NaN is an artefact of the
    /// division, and it reaches the engine as a NaN inertia tensor.
    /// <see cref="CompliancePolicy.Correct"/> skips such edges;
    /// <see cref="CompliancePolicy.Stock"/> divides, as both builds do.
    /// </para>
    /// </remarks>
    CollisionInertiaZeroLengthEdge,

    /// <summary>
    /// <c>ConvertPolysoupToCollide</c>'s material fix-up walks past a ledge's last triangle when
    /// the first triangle's material is 0, and writes material 0 into the words that follow:
    /// the ledge's own points.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference's loop "copy the materials to the duplicated 2-D triangles",
    /// compiled into both reference builds, searches the
    /// ledge's triangles for a non-zero material with the same pointer it then copies with; when
    /// the first triangle's material is 0 the search leaves the pointer one past the last
    /// triangle, and the copy loop clears bits 24-30 of the next <c>n</c> words. For a
    /// displacement's two-sided triangle ledge those are the x coordinates of its first two
    /// points, whose exponents are wiped: the vertices collapse to x = 0 in the cooked space.
    /// </para>
    /// <para>
    /// Reached by vbsp's <c>-novirtualmesh</c> displacement road (also taken for
    /// power-4 displacements) whenever a displacement triangle's surface property
    /// is the first in the world's material table (index 0). Measured: the golden
    /// polysoups are byte-exact against both builds only with the overrun reproduced.
    /// </para>
    /// <para>
    /// <see cref="CompliancePolicy.Correct"/> copies the ledge's material to its own triangles
    /// only; <see cref="CompliancePolicy.Stock"/> reproduces the overrun.
    /// </para>
    /// </remarks>
    CollisionPolysoupMaterialOverrun,

    /// <summary>
    /// The KD tracer replaces a zero direction component with
    /// <c>FLT_EPSILON</c> before taking its reciprocal, so an axis-parallel ray
    /// that starts just inside a plane is cut short and passes through what
    /// lies beyond the cut.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Trace4Rays</c> calls
    /// <c>MakeReciprocalSaturate</c>, whose <c>ReciprocalSaturateSIMD</c>
    /// ORs <c>Four_Epsilons</c> =
    /// <c>FLT_EPSILON</c> into a zero component. The
    /// reciprocal is then about 8.4e6, and a ray whose origin lies a distance
    /// <c>d</c> inside the scene box's face, or short of a KD split plane, on
    /// that axis gets a far limit of <c>d * 8.4e6</c> -- 64 units for one ulp
    /// at 100 -- instead of never meeting the plane at all. Nothing past the
    /// limit is walked. A <c>+0</c> component cuts at the max face and a
    /// <c>-0</c> at the min face, so the same ray's answer depends on the
    /// sign of a zero.
    /// </para>
    /// <para>
    /// Observed: a shadow ray along +Y from one ulp inside x = 100 misses a
    /// wall at distance 1,000 in stock's own <c>Trace4Rays</c> (the reference build,
    /// unmodified) and hits it with <c>-0</c>. On 2fort's
    /// recorded transfer rays, 1 hit id and 2 hit distances in 4,193,652
    /// differ from the 1e-10 substitute this port used before, all past the
    /// segment's end.
    /// </para>
    /// <para>
    /// <b>Why this is a defect.</b> An occluder the ray passes through is
    /// missed: light leaks through a wall, and whether it does depends on a
    /// zero's sign bit. <see cref="CompliancePolicy.Correct"/> substitutes
    /// 2^-60 instead (a reciprocal of about 1.2e18, the largest the Newton
    /// step does not overflow), which cuts no ray a map can hold unless its
    /// coordinates are within about 1e-6 of zero.
    /// <see cref="CompliancePolicy.Stock"/> uses <c>FLT_EPSILON</c>.
    /// </para>
    /// </remarks>
    KdZeroDirectionReachCut,
}

/// <summary>
/// The compliance a compile runs under: one policy, plus per-quirk exceptions.
/// </summary>
/// <remarks>
/// <para>
/// The single <see cref="Policy"/> is what a command line sets and what almost
/// everything should use. <see cref="Except"/> exists for one job: a test that
/// isolates a single quirk, so that a gate can say "stock in every respect but
/// this one" and attribute a difference to exactly one cause. Using it to
/// assemble a bespoke mixture for a real compile would produce output matching
/// neither tool.
/// </para>
/// </remarks>
public sealed record ComplianceOptions
{
    /// <summary>The policy every quirk follows unless <see cref="Except"/> overrides it.</summary>
    public CompliancePolicy Policy { get; init; } = CompliancePolicy.Correct;

    /// <summary>
    /// Quirks whose behaviour is the opposite of <see cref="Policy"/>.
    /// </summary>
    /// <remarks>
    /// Empty in every compile a user asks for. A gate isolating one quirk sets
    /// exactly one member here.
    /// </remarks>
    public IReadOnlySet<StockQuirk> Except { get; init; } = new HashSet<StockQuirk>();

    /// <inheritdoc />
    /// <remarks>
    /// Written by hand because the compiler-generated version compares
    /// <see cref="Except"/> by reference: two compiles carrying the same quirk
    /// set in two different <see cref="HashSet{T}"/> instances — a CLI parse
    /// versus a gate's <see cref="Flipping"/> — must be equal, and with the
    /// generated members they are not. Equality is content-based (element
    /// order irrelevant); the hash only has to agree with it, as nothing keys
    /// a dictionary on a parse result.
    /// </remarks>
    public bool Equals(ComplianceOptions? other) =>
        other is not null
        && Policy == other.Policy
        && Except.SetEquals(other.Except);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Policy, Except.Count);

    /// <summary>Do the right thing everywhere. The default.</summary>
    public static ComplianceOptions Correct { get; } = new();

    /// <summary>Reproduce every stock defect. What byte-exact gates select.</summary>
    public static ComplianceOptions Stock { get; } =
        new() { Policy = CompliancePolicy.Stock };

    /// <summary>
    /// Whether <paramref name="quirk"/> should behave as stock does.
    /// </summary>
    /// <param name="quirk">The behaviour being decided.</param>
    /// <returns>
    /// True to reproduce stock's defect, false to do the right thing.
    /// </returns>
    public bool Emulates(StockQuirk quirk)
    {
        bool baseline = Policy == CompliancePolicy.Stock;
        return Except.Contains(quirk) ? !baseline : baseline;
    }

    /// <summary>
    /// This policy with one quirk forced to the other side, for a gate that
    /// isolates it.
    /// </summary>
    /// <param name="quirk">The quirk to flip.</param>
    /// <returns>A new instance; this one is unchanged.</returns>
    public ComplianceOptions Flipping(StockQuirk quirk)
    {
        HashSet<StockQuirk> flipped = new(Except);
        if (!flipped.Add(quirk))
        {
            flipped.Remove(quirk);
        }

        return this with { Except = flipped };
    }
}
