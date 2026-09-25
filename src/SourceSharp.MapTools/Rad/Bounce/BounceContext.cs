using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rad.Bounce;

/// <summary>
/// The read-only state the transfer build and the bounce read: the parts of a
/// <see cref="RadWorld"/> after <see cref="RadWorld.LightFacesAsync"/>.
/// </summary>
/// <param name="Geometry">The map's faces, planes, leaves and texinfo.</param>
/// <param name="Neighbours">PairEdges' output, for the phong normals of bumped patches.</param>
/// <param name="Patches">
/// The patches, with their direct light in <c>TotalLight.Flat</c>. The bounce
/// WRITES their <c>TotalLight</c>, as stock's does.
/// </param>
/// <param name="Visibility">The PVS.</param>
/// <param name="Settings">The switches: bounce count, normalise fork, compliance.</param>
public sealed record BounceContext(
    LightGeometry Geometry,
    FaceNeighbours Neighbours,
    PatchSet Patches,
    LightVisibility Visibility,
    DirectLightingSettings Settings);
