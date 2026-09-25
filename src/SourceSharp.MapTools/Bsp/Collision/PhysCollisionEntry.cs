using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp.Collision;

/// <summary>
/// One solid of one model's collision: <c>CPhysCollisionEntry</c> and its
/// four kinds (<c>ivp.cpp:195-372</c>), holding the cooked bytes rather than
/// a live <c>CPhysCollide*</c>.
/// </summary>
/// <param name="Blob">What <c>CollideWrite</c> produced, from its <c>VPHY</c> header.</param>
/// <remarks>
/// The bytes are taken on the cooker thread the moment a collide is built,
/// so everything after -- the text, the framing, the lump -- is plain managed
/// data with no native handle to outlive.
/// </remarks>
public abstract record PhysCollisionEntry(byte[] Blob)
{
    /// <summary><c>WriteToTextBuffer</c>: this solid's block of keydata.</summary>
    /// <param name="text">Where it goes.</param>
    /// <param name="collideIndex">The solid's index within its model.</param>
    public abstract void WriteText(CollisionTextBuffer text, int collideIndex);
}

/// <summary>
/// <c>CPhysCollisionEntrySolid</c>, <c>ivp.cpp:220</c>: a brush entity's
/// movable solid.
/// </summary>
/// <param name="Blob">The cooked bytes.</param>
/// <param name="Material">The surface property name, or null.</param>
/// <param name="Mass">kg.</param>
/// <param name="Volume"><c>CollideVolume</c>, cubic inches.</param>
public sealed record PhysSolidEntry(byte[] Blob, string? Material, float Mass, float Volume) : PhysCollisionEntry(Blob)
{
    /// <inheritdoc />
    public override void WriteText(CollisionTextBuffer text, int collideIndex)
    {
        ArgumentNullException.ThrowIfNull(text);
        text.WriteText("solid {\n");
        text.WriteIntKey("index", collideIndex);
        text.WriteFloatKey("mass", Mass);
        if (Material is not null)
        {
            text.WriteStringKey("surfaceprop", Material);
        }

        if (Volume != 0f)
        {
            text.WriteFloatKey("volume", Volume);
        }

        text.WriteText("}\n");
    }
}

/// <summary>
/// <c>CPhysCollisionEntryStaticSolid</c>, <c>ivp.cpp:265</c>: the world's
/// brushes of one contents class.
/// </summary>
/// <param name="Blob">The cooked bytes.</param>
/// <param name="Contents">The contents mask the brushes were selected by.</param>
public sealed record PhysStaticSolidEntry(byte[] Blob, int Contents) : PhysCollisionEntry(Blob)
{
    /// <inheritdoc />
    public override void WriteText(CollisionTextBuffer text, int collideIndex)
    {
        ArgumentNullException.ThrowIfNull(text);
        text.WriteText("staticsolid {\n");
        text.WriteIntKey("index", collideIndex);
        text.WriteIntKey("contents", Contents);
        text.WriteText("}\n");
    }
}

/// <summary>
/// <c>CPhysCollisionEntryStaticMesh</c>, <c>ivp.cpp:298</c>: displacement
/// terrain as a polysoup (the <c>-novirtualmesh</c> road). It writes no
/// contents key: a mesh is always solid.
/// </summary>
/// <param name="Blob">The cooked bytes.</param>
public sealed record PhysStaticMeshEntry(byte[] Blob) : PhysCollisionEntry(Blob)
{
    /// <inheritdoc />
    public override void WriteText(CollisionTextBuffer text, int collideIndex)
    {
        ArgumentNullException.ThrowIfNull(text);
        text.WriteText("staticsolid {\n");
        text.WriteIntKey("index", collideIndex);
        text.WriteText("}\n");
    }
}

/// <summary>
/// <c>CPhysCollisionEntryFluid</c>, <c>ivp.cpp:318</c>: one connected water volume.
/// </summary>
/// <param name="Blob">The cooked bytes.</param>
/// <param name="SurfaceProp">The water's surface property (stock: always <c>water</c>).</param>
/// <param name="Damping">Stock: 0.01.</param>
/// <param name="SurfaceNormal">The surface plane's normal.</param>
/// <param name="SurfaceDist">The surface plane's distance.</param>
/// <param name="Contents">The water leaf's contents.</param>
public sealed record PhysFluidEntry(
    byte[] Blob, string SurfaceProp, float Damping, Vec3 SurfaceNormal, float SurfaceDist, int Contents)
    : PhysCollisionEntry(Blob)
{
    /// <inheritdoc />
    public override void WriteText(CollisionTextBuffer text, int collideIndex)
    {
        ArgumentNullException.ThrowIfNull(text);
        text.WriteText("fluid {\n");
        text.WriteIntKey("index", collideIndex);
        text.WriteStringKey("surfaceprop", SurfaceProp);
        text.WriteFloatKey("damping", Damping);
        text.WriteIntKey("contents", Contents);
        text.WriteFloatArrayKey("surfaceplane", [SurfaceNormal.X, SurfaceNormal.Y, SurfaceNormal.Z, SurfaceDist]);
        text.WriteFloatArrayKey("currentvelocity", [0f, 0f, 0f]);
        text.WriteText("}\n");
    }
}
