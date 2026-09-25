using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// A static prop model's collision geometry, in MODEL space: the triangles
/// <c>ICollisionQuery</c> reads back out of a <c>CPhysCollide</c>.
/// </summary>
/// <param name="Vertices">The positions, in the model's own frame.</param>
/// <param name="Indices">
/// Three entries per triangle, indexing <paramref name="Vertices"/>, in the
/// order <c>ICollisionQuery::GetTriangleVerts</c> would hand them over:
/// convex by convex, triangle by triangle within a convex.
/// </param>
/// <param name="ConvexCount">
/// How many convex pieces the triangles came from. Recorded rather than
/// computed because <c>vradstaticprops.cpp:1847</c> loops convexes and then
/// triangles, and a caster set that flattened the two loops could not be
/// compared against a dump that did not.
/// </param>
/// <remarks>
/// MODEL SPACE, not world space. The caller applies
/// <c>VMatrix::SetupMatrixOrgAngles( prop.m_Origin, prop.m_Angles )</c> per
/// prop (<c>vradstaticprops.cpp:1845</c>), so one load serves every prop that
/// shares a model -- which is the whole reason stock keeps the collide in the
/// model dictionary rather than per prop.
/// </remarks>
public sealed record PropCollisionMesh(
    IReadOnlyList<Vec3> Vertices,
    IReadOnlyList<int> Indices,
    int ConvexCount)
{
    /// <summary>How many triangles the mesh holds.</summary>
    public int TriangleCount => Indices.Count / 3;
}

/// <summary>
/// Where a static prop model's collision triangles come from.
/// </summary>
/// <remarks>
/// <para>
/// THE ONE CORRECT IMPLEMENTATION TODAY IS A BINDING TO <c>vphysics.so</c>,
/// and this interface exists so that the rest of the static prop shadow path
/// is complete managed code with exactly one hole in it rather than a hole in
/// the middle of every function.
/// </para>
/// <para>
/// Stock's <c>CVradStaticPropMgr::CreateCollisionModel</c>
/// (<c>vradstaticprops.cpp:942</c>) gets a <c>CPhysCollide*</c> down two
/// different roads and BOTH of them go through vphysics:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// With a usable <c>.phy</c> (<c>vradstaticprops.cpp:965</c>):
/// <c>IPhysicsCollision::VCollideLoad</c> over the file's solids, then
/// <c>m_loadedModel.solids[0]</c>. The solid payload is an IVP compact ledge
/// tree in Havok's private format; nothing in this tree decodes it, stock does
/// not either, and <see cref="SourceSharp.MapFormats.Assets.PhyFile"/> frames
/// it without claiming to understand it. THERE IS NO MANAGED TRIANGLE COUNT
/// FOR A <c>.phy</c> and inventing one would be a second, divergent
/// implementation of a format this project does not own.
/// </description>
/// </item>
/// <item>
/// <description>
/// With no <c>.phy</c> (<c>vradstaticprops.cpp:978</c>):
/// <c>ComputeConvexHull( studiohdr_t* )</c>
/// (<c>vradstaticprops.cpp:433</c>), which makes ONE CONVEX HULL PER RENDER
/// MESH out of that mesh's vertices via
/// <c>IPhysicsCollision::ConvexFromVerts</c> and glues them together with
/// <c>ConvertConvexToCollide</c>. That is also vphysics, and it is also not
/// something managed code can answer.
/// </description>
/// </item>
/// </list>
/// <para>
/// THAT PER-MESH RENDER HULL IS NOT THE SAME TRIANGLES AS THE RENDER MESH.
/// It is the CONVEX HULL of each mesh's vertex cloud -- a concave mesh loses
/// its concavity, and a mesh whose vertices are shared with another mesh still
/// gets its own separate hull. It is also not the same triangles as the
/// <c>.phy</c>: stock uses the hull only when the <c>.phy</c> is absent, never
/// as a stand-in for one. So an implementation that answered the render
/// triangles here would produce a caster set with a plausible count and the
/// wrong geometry, which is the exact shape of wrongness this port is built to
/// refuse.
/// </para>
/// <para>
/// See <see cref="NullPropCollisionSource"/> for the honest placeholder:
/// answering null routes every prop to stock's AABB branch
/// (<c>vradstaticprops.cpp:1861</c>), which is wrong in a way that is visible
/// in the caster count rather than wrong in a way that looks right.
/// </para>
/// </remarks>
public interface IPropCollisionSource
{
    /// <summary>Loads a model's collision triangles in model space.</summary>
    /// <param name="modelPath">
    /// The model's content path, as the <c>sprp</c> dictionary spells it --
    /// <c>models/props_c17/oildrum001.mdl</c>. The implementation derives the
    /// <c>.phy</c> path from it the way
    /// <c>LoadStudioCollisionModel</c> does (<c>vradstaticprops.cpp:508</c>:
    /// <c>Q_SetExtension</c> to <c>.phy</c>).
    /// </param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>
    /// The triangles, or null when the model has no usable collision at all --
    /// which routes the caller to the AABB fallback.
    /// </returns>
    ValueTask<PropCollisionMesh?> LoadAsync(
        VPath modelPath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// An <see cref="IPropCollisionSource"/> that has no collision for anything.
/// </summary>
/// <remarks>
/// <para>
/// Not a mock and not a test double: it is the honest answer for a build with
/// no vphysics binding, and it is what lets the default (non
/// <c>-StaticPropPolys</c>) path be exercised end to end today. Every prop
/// falls to stock's AABB branch (<c>vradstaticprops.cpp:1861</c>) and
/// contributes the twelve triangles of its model's hull box.
/// </para>
/// <para>
/// THE RESULTING CASTER SET IS NOT STOCK'S. Stock reaches that branch only
/// when the model failed to LOAD -- see
/// <see cref="StaticPropModel.HullMin"/> for why the box is then degenerate --
/// so a compile run against this source has static prop shadows that are boxes
/// rather than shapes. It is visibly, countably different (12 triangles per
/// prop against stock's 17,304 total on the golden map), which is the point:
/// a placeholder that produced a believable number would hide the missing
/// binding.
/// </para>
/// </remarks>
public sealed class NullPropCollisionSource : IPropCollisionSource
{
    /// <summary>The single instance; it has no state.</summary>
    public static NullPropCollisionSource Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<PropCollisionMesh?> LoadAsync(
        VPath modelPath,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<PropCollisionMesh?>(null);
}
