using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Disp;

/// <summary>
/// What the displacement neighbour rules need of a displacement:
/// <c>CDispUtilsHelper</c>, <c>public/disp_common.h:26</c>.
/// </summary>
/// <remarks>
/// <para>
/// Stock's abstract base exists so that vbsp's <c>CCoreDispInfo</c>, the
/// engine's <c>CDispInfo</c> and Hammer's <c>CMapDisp</c> can all be walked by
/// one copy of the traversal code. This port has one implementation today and
/// keeps the interface anyway, because the traversal is the part that is hard
/// to get right and the part a test wants to drive with a hand-built
/// displacement that no map file contains — a power-4 abutting a power-2 on
/// half an edge, say.
/// </para>
/// <para>
/// The neighbour accessors return <c>ref</c>, which is not an aesthetic choice:
/// <c>AddNeighbor</c> (<c>disp_common.cpp:850</c>) writes into BOTH
/// displacements' sub-neighbour slots through pointers it took earlier, and a
/// by-value accessor would silently make that a no-op on a struct.
/// </para>
/// </remarks>
public interface IDispUtils
{
    /// <summary>The precalculated tables for this displacement's power.</summary>
    PowerInfo PowerInfo { get; }

    /// <summary>The neighbours along one edge, by reference.</summary>
    /// <param name="index">A <see cref="DispEdge"/>.</param>
    /// <returns>That edge's neighbour record.</returns>
    ref DispNeighbor EdgeNeighbor(int index);

    /// <summary>The neighbours at one corner, by reference.</summary>
    /// <param name="index">A <see cref="DispCorner"/>.</param>
    /// <returns>That corner's neighbour record.</returns>
    ref DispCornerNeighbors CornerNeighbors(int index);

    /// <summary>
    /// Another displacement in the same list, by its LUMP_DISPINFO index.
    /// </summary>
    /// <param name="index">
    /// The index, or <see cref="DispSubNeighbor.NoNeighbor"/>.
    /// </param>
    /// <returns>That displacement, or null for the no-neighbour sentinel.</returns>
    /// <remarks>
    /// <c>CCoreDispInfo::GetDispUtilsByIndex</c> (<c>builddisp.cpp:887</c>)
    /// tests <c>index == 0xFFFF</c> and returns null. Every caller that could
    /// pass the sentinel checks <c>IsValid()</c> first, so the null return is
    /// defence rather than a path, and it is kept as one.
    /// </remarks>
    IDispUtils? ByIndex(int index);

    /// <summary>This displacement's power.</summary>
    int Power => PowerInfo.Power;

    /// <summary>Its side length in vertices.</summary>
    int SideLength => PowerInfo.SideLength;

    /// <summary>A grid index flattened to a vertex array position.</summary>
    /// <param name="index">The grid index.</param>
    /// <returns><c>y * sideLength + x</c>.</returns>
    int VertIndexToInt(VertIndex index) => PowerInfo.VertIndexToInt(index);
}

/// <summary>
/// The <c>SetInvalid</c> helpers the BSP neighbour structs carry in C++ but
/// that the managed lump structs, which are pure layout, do not.
/// </summary>
public static class DispNeighborExtensions
{
    /// <summary>
    /// Clears both sub-neighbours: <c>CDispNeighbor::SetInvalid</c>,
    /// <c>bspfile.h:589</c>.
    /// </summary>
    /// <param name="neighbor">The edge record to clear.</param>
    public static void SetInvalid(this ref DispNeighbor neighbor)
    {
        neighbor.SubNeighbors[0].Neighbor = DispSubNeighbor.NoNeighbor;
        neighbor.SubNeighbors[1].Neighbor = DispSubNeighbor.NoNeighbor;
    }

    /// <summary>
    /// Whether anything at all touches this edge:
    /// <c>CDispNeighbor::IsValid</c>, <c>bspfile.h:592</c>.
    /// </summary>
    /// <param name="neighbor">The edge record.</param>
    /// <returns>True if either sub-neighbour names a displacement.</returns>
    public static bool IsValid(this ref DispNeighbor neighbor) =>
        neighbor.SubNeighbors[0].IsValid() || neighbor.SubNeighbors[1].IsValid();

    /// <summary>
    /// Empties a corner's neighbour list:
    /// <c>CDispCornerNeighbors::SetInvalid</c>, <c>bspfile.h:607</c>.
    /// </summary>
    /// <param name="corner">The corner record to clear.</param>
    /// <remarks>
    /// Only the COUNT is cleared, exactly as stock does. The four index slots
    /// keep whatever they held, which is why a gate over the lump must compare
    /// the count first and only then the live prefix — stock's own writer emits
    /// the stale tail into the BSP.
    /// </remarks>
    public static void SetInvalid(this ref DispCornerNeighbors corner) =>
        corner.NumNeighbors = 0;
}
