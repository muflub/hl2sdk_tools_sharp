using System.Collections.Frozen;
using System.Reflection;

namespace SourceSharp.MapGen.Catalog;

/// <summary>
/// The vocabulary, read back: every <see cref="MapFeature"/> with the tool and
/// the phase its <see cref="MapFeatureAttribute"/> declares.
/// </summary>
public static class MapFeatures
{
    /// <summary>
    /// Every feature in the vocabulary, in declaration order.
    ///
    /// <para>
    /// Declaration order, not sorted: the enum is grouped by tool the way
    /// §2a's table is, and a coverage report read in that order is read against
    /// the plan.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MapFeature> All { get; } =
        [.. Enum.GetValues<MapFeature>()];

    /// <summary>
    /// The attribute on each feature.
    ///
    /// <para>
    /// Frozen, and built once in a field initialiser: the catalogue's rule is no
    /// mutable static state, so this is a read-only table and not a cache that
    /// fills in as it is asked.
    /// </para>
    /// </summary>
    private static readonly FrozenDictionary<MapFeature, MapFeatureAttribute> Table =
        Enum.GetValues<MapFeature>()
            .ToFrozenDictionary(
                f => f,
                f => typeof(MapFeature).GetField(f.ToString(), BindingFlags.Public | BindingFlags.Static)
                         ?.GetCustomAttribute<MapFeatureAttribute>()
                     ?? throw new InvalidOperationException(
                         $"MapFeature.{f} has no [MapFeature] attribute, so the matrix fact "
                         + "cannot say which phase owns it"));

    /// <summary>What the feature is and who owns it.</summary>
    /// <param name="feature">A member of the vocabulary.</param>
    public static MapFeatureAttribute Describe(MapFeature feature) => Table[feature];

    /// <summary>The compiler a failure in this feature lands in.</summary>
    /// <param name="feature">A member of the vocabulary.</param>
    public static CompileTool ToolOf(MapFeature feature) => Table[feature].Tool;

    /// <summary>The plan phase that ports the stage this feature belongs to.</summary>
    /// <param name="feature">A member of the vocabulary.</param>
    public static int PhaseOf(MapFeature feature) => Table[feature].Phase;

    /// <summary>
    /// The earliest phase with a managed compiler for any of these features, and
    /// so the phase the catalogue's L1 coverage is required to be complete for.
    ///
    /// <para>
    /// Phase 2 is vvis. Phase 1 — the one this lane runs in — ports formats and
    /// geometry and has no compiler at all, so every feature here belongs to
    /// phase 2 or later by construction.
    /// </para>
    /// </summary>
    public const int FirstCompilerPhase = 2;

    /// <summary>Every feature the named phase owns.</summary>
    /// <param name="phase">A plan_maptools.md phase number.</param>
    public static IReadOnlyList<MapFeature> OfPhase(int phase)
        => [.. All.Where(f => PhaseOf(f) == phase)];

    /// <summary>Every feature the named tool owns.</summary>
    /// <param name="tool">A stock map compiler.</param>
    public static IReadOnlyList<MapFeature> OfTool(CompileTool tool)
        => [.. All.Where(f => ToolOf(f) == tool)];
}
