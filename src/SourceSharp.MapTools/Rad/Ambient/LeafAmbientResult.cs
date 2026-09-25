using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// What a leaf-ambient pass produces: the two lumps, and the numbers stock
/// prints while making them.
/// </summary>
/// <param name="Index">
/// One entry per leaf, for <c>LUMP_LEAF_AMBIENT_INDEX</c> or its HDR twin.
/// </param>
/// <param name="Lighting">
/// The samples, for <c>LUMP_LEAF_AMBIENT_LIGHTING</c> or its HDR twin.
/// </param>
/// <param name="LightsInAmbientCube">
/// How many world lights were folded into the cubes. Stock's first printed
/// number.
/// </param>
/// <param name="SurfaceLights">
/// How many <c>emit_surface</c> lights the map has. Stock's second.
/// </param>
/// <param name="BadLeaves">
/// How many NON-SOLID leaves ended with no samples at all. Stock prints one
/// "Bad leaf ambient for leaf N" line per leaf, which is a diagnostic rather
/// than an error; a count is the same information without the flood.
/// </param>
/// <remarks>
/// <para>
/// A RETURN VALUE, not a mutation. Stock accumulates into
/// <c>g_LeafAmbientSamples</c>, <c>g_pLeafAmbientIndex</c> and
/// <c>g_pLeafAmbientLighting</c> -- three globals whose identity depends on
/// which of LDR and HDR <c>SetHDRMode</c> last selected. Returning the pair
/// makes the pass a function of its scene and removes the mode's reach into the
/// output entirely: the caller writes it into whichever lump it asked for.
/// </para>
/// <para>
/// The two arrays are exactly what the lumps hold, in the lumps' order, so
/// writing them is a copy and a comparison against a stock BSP is a
/// <c>SequenceEqual</c>.
/// </para>
/// </remarks>
public sealed record LeafAmbientResult(
    DLeafAmbientIndex[] Index,
    DLeafAmbientLighting[] Lighting,
    int LightsInAmbientCube,
    int SurfaceLights,
    int BadLeaves);
