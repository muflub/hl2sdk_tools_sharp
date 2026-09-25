using SourceSharp.MapTools.Bsp.Collision;

namespace SourceSharp.MapTools.Phys;

/// <summary>
/// What the per-model cooked-collision cache replays for one brush model
/// (plan_maptools.md 10a: the common edit loop is "move a light / tweak an
/// entity / nudge a prop, recompile" — every model whose cooking inputs are
/// unchanged is reused, byte for byte).
/// </summary>
/// <remarks>
/// <para>
/// The payload is exactly what <c>PhysCollisionEmitter.EmitAsync</c> produces
/// per model — the solid blobs and the finished keydata text — plus, for the
/// world model, the three things the world cook also writes back
/// (<c>LeafWaterDataIds</c>, <c>WorldPropList</c>, the <c>PHYSDISP</c> bytes),
/// which are the cook's only effects outside the model's own record.
/// </para>
/// <para>
/// The text is stored finished — including the world-only
/// <c>virtualterrain</c>/<c>materialtable</c> sections, NUL-terminated as
/// <c>CollisionTextBuffer.Terminate</c> leaves it — so a hit bypasses text
/// assembly as well as cooking, and is the same bytes as a fresh cook by
/// construction.
/// </para>
/// </remarks>
/// <param name="ModelNumber">The model index it was cooked as — the codec's self-check against the keyed cross-read.</param>
/// <param name="Solids">The solid blobs, in entry order (what <c>PhysCollideModel.Solids</c> holds).</param>
/// <param name="Text">The model's finished keydata bytes (empty when no entries survived the cook).</param>
/// <param name="LeafWaterDataIds">World only: the per-leaf water ids the world cook assigned; null for a brush model.</param>
/// <param name="WorldPropList">World only: the material table the world cook filled; null for a brush model.</param>
/// <param name="PhysDisp">World only: the virtual-mesh blob, when the world cook built one.</param>
public sealed record CachedCollisionModel(
    int ModelNumber,
    IReadOnlyList<byte[]> Solids,
    byte[] Text,
    short[]? LeafWaterDataIds,
    int[]? WorldPropList,
    byte[]? PhysDisp)
{
    /// <summary>Total cooked bytes, for the report.</summary>
    public long Bytes => Text.LongLength + Solids.Sum(s => (long)s.Length);
}

/// <summary>
/// The per-model cooked-collision cache seam, hung on
/// <c>PhysCollisionEmitter.EmitAsync</c> (plan_maptools.md 10a).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately tiny: the emitter asks once per model with its input and index
/// and either cooks (and offers the finished product back through
/// <see cref="CookedAsync"/>) or replays. The implementation owns key policy
/// (<see cref="Compile.Cache.CollisionModelKey"/>), the store, the poisoning
/// checks and the report counters; the emitter knows only
/// "ask, else cook, else offer".
/// </para>
/// <para>
/// A hit must return exactly the bytes the cook would have produced — the
/// store is content-addressed, the key covers every input the cook reads and
/// the tool/cooker identity, and every blob is re-hashed against its key on
/// replay, so a wrong hit surfaces as a miss, never as wrong output.
/// </para>
/// <para>
/// Thread-safety: the parallel cook arm may ask for several models at once;
/// implementations must be safe for concurrent callers.
/// </para>
/// </remarks>
public interface ICollisionModelCache
{
    /// <summary>Looks up one model's cooked product; null on miss.</summary>
    /// <param name="input">The emitter input (the fold's source; not retained).</param>
    /// <param name="modelIndex">The model being cooked.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<CachedCollisionModel?> TryGetAsync(
        PhysCollisionInput input,
        int modelIndex,
        CancellationToken cancellationToken);

    /// <summary>Offers one model's finished product for storage; may drop it (size policy, read-only posture).</summary>
    /// <param name="input">The same input the cook ran under.</param>
    /// <param name="modelIndex">The model that was cooked.</param>
    /// <param name="model">The finished product.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask CookedAsync(
        PhysCollisionInput input,
        int modelIndex,
        CachedCollisionModel model,
        CancellationToken cancellationToken);
}
