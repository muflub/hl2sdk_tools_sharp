using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Phys;

/// <summary>
/// An opaque <c>CPhysConvex*</c> owned by the cooker.
/// </summary>
/// <param name="Value">The native pointer; zero is the null convex.</param>
/// <remarks>
/// Valid only inside the <see cref="ICollisionCooker.RunAsync{T}"/> callback that
/// produced it, on the cooker thread. A convex handed to
/// <see cref="ICollisionSession.ConvertConvexToCollide"/> is consumed by it
/// (<c>vphysics_interface.h:210</c>: "this deletes the convex elements").
/// </remarks>
public readonly record struct ConvexHandle(nint Value)
{
    /// <summary>Whether this is the null convex a failed build returns.</summary>
    public bool IsNull => Value == 0;
}

/// <summary>An opaque <c>CPhysCollide*</c> owned by the cooker.</summary>
/// <param name="Value">The native pointer; zero is the null collide.</param>
public readonly record struct CollideHandle(nint Value)
{
    /// <summary>Whether this is the null collide a failed build returns.</summary>
    public bool IsNull => Value == 0;
}

/// <summary>An opaque <c>CPhysPolysoup*</c> owned by the cooker.</summary>
/// <param name="Value">The native pointer.</param>
public readonly record struct PolysoupHandle(nint Value)
{
    /// <summary>Whether this is the null soup.</summary>
    public bool IsNull => Value == 0;
}

/// <summary>
/// One bounding plane in the layout <c>ConvexFromPlanes</c> reads: four floats,
/// normal then distance (<c>ivp.cpp:396</c>, <c>listplane_t</c>).
/// </summary>
/// <param name="Normal">The OUTWARD normal (<c>physics_collide.cpp:712</c>).</param>
/// <param name="Dist">The plane distance.</param>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct CollisionPlane(Vec3 Normal, float Dist);

/// <summary>
/// <c>convertconvexparams_t</c>, <c>vphysics_interface.h:1077</c>.
/// </summary>
/// <param name="BuildOuterConvexHull">Build a hull around all the convexes.</param>
/// <param name="BuildDragAxisAreas">Compute the drag areas.</param>
/// <param name="BuildOptimizedTraceTables">Build trace tables.</param>
/// <param name="DragAreaEpsilon">The drag-area ray spacing.</param>
public readonly record struct ConvertConvexParams(
    bool BuildOuterConvexHull,
    bool BuildDragAxisAreas,
    bool BuildOptimizedTraceTables,
    float DragAreaEpsilon)
{
    /// <summary>
    /// <c>convertconvexparams_t::Defaults</c>, <c>vphysics_interface.h:1085</c>:
    /// everything off and an epsilon of 0.25.
    /// </summary>
    public static ConvertConvexParams Defaults => new(false, false, false, 0.25f);
}

/// <summary>
/// The fields of <c>trace_t</c> a collide-versus-collide trace answers.
/// </summary>
/// <param name="StartPosition">Where the sweep started.</param>
/// <param name="EndPosition">Where it stopped.</param>
/// <param name="PlaneNormal">The hit plane's normal.</param>
/// <param name="Fraction">How far along the sweep it got, 0..1.</param>
/// <param name="AllSolid">Whether the whole sweep was inside.</param>
/// <param name="StartSolid">Whether it started inside: what <c>staticprop.cpp:350</c> reads.</param>
public readonly record struct CollisionTrace(
    Vec3 StartPosition,
    Vec3 EndPosition,
    Vec3 PlaneNormal,
    float Fraction,
    bool AllSolid,
    bool StartSolid);

/// <summary>
/// A <c>vcollide_t</c> (<c>public/vcollide.h:13</c>) loaded by
/// <c>VCollideLoad</c>: its solids, as handles valid until
/// <c>VCollideUnload</c>, and its keydata.
/// </summary>
/// <param name="Native">The native <c>vcollide_t</c> block, owned by the session.</param>
/// <param name="Solids">The solids, in buffer order.</param>
/// <param name="KeyData">The keydata bytes vphysics copied (<c>bufferSize - position</c>, NUL included).</param>
public sealed record LoadedVCollide(nint Native, IReadOnlyList<CollideHandle> Solids, byte[] KeyData);

/// <summary>
/// A triangle mesh handed to <c>CreateVirtualMesh</c>: what
/// <c>CDispMeshEvent</c> (<c>disp_ivp.cpp:199</c>) serves back to vphysics.
/// </summary>
/// <param name="Vertices">The vertex positions.</param>
/// <param name="Indices">Three indices per triangle, in the order they are served.</param>
public sealed record VirtualMeshSource(Vec3[] Vertices, ushort[] Indices)
{
    /// <summary>
    /// <c>MAX_VIRTUAL_TRIANGLES</c>, <c>vphysics/virtualmesh.h:13</c>: the
    /// index array vphysics hands the event is this many triangles long.
    /// </summary>
    public const int MaxVirtualTriangles = 1024;
}
