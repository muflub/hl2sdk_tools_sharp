using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// One worker's scratch for <see cref="VisBaseFlow"/>: the <c>portalfront</c>
/// vector and the flood's cluster stack.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>portalfront</c> is scratch, not state, and stock's layout hides
/// that.</b> Stock hangs it off <c>portal_t</c> beside <c>portalflood</c> and
/// <c>portalvis</c>, which reads as three
/// equally durable per-portal facts. It is not: a portal's <c>portalfront</c>
/// is written at the head of its own <c>BasePortalVis</c>, read by the
/// <c>SimpleFlood</c> two lines later, and never looked at again by anything --
/// not by the portal flow, not by <c>ClusterMerge</c>, not by another portal's
/// pass. So exactly one is needed per WORKER.
/// </para>
/// <para>
/// On 2fort that is 51 KB in place of 20 MB, and on a map at
/// <c>MAX_PORTALS</c> it is 262 KB in place of 536 MB. plan_maptools.md 5's 2b
/// list asks for "drop portalfront after SimpleFlood"; never allocating it
/// per portal is the same saving taken at the other end, and it also means the
/// vector a worker reuses stays in its cache instead of being a fresh 1,600
/// bytes of cold memory for every one of the map's portals.
/// </para>
/// <para>
/// The stack is here for the same reason: it is the explicit form of stock's
/// <c>SimpleFlood</c> recursion, it is emptied at the head of every flood, and
/// one per worker is enough.
/// </para>
/// </remarks>
internal sealed class VisFloodScratch
{
    private readonly ulong[] _front;

    /// <summary>Allocates one worker's scratch.</summary>
    /// <param name="portalCount">The map's memory-portal count.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="portalCount"/> is negative.
    /// </exception>
    internal VisFloodScratch(int portalCount) => _front = BitVector.Allocate(portalCount);

    /// <summary>
    /// <c>portalfront</c> for whichever portal this worker is on.
    /// </summary>
    /// <remarks>
    /// Cleared by <see cref="VisBaseFlow.Run"/> at the head of every item, not
    /// here: a caller that forgot would otherwise inherit the previous
    /// portal's bits and flood through walls, and the clear belongs next to the
    /// code that depends on it.
    /// </remarks>
    internal Span<ulong> Front => _front;

    /// <summary>The flood's cluster stack, reused between items.</summary>
    internal Stack<int> Clusters { get; } = new();
}
