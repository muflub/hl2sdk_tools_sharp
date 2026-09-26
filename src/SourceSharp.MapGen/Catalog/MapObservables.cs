//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapGen.Catalog;

/// <summary>
/// What an entry is expected to produce, declared as DATA.
///
/// <para>
/// Almost none of this can be checked today, and that is deliberate rather than
/// a shortcoming. §2a's rule is map first: a feature's catalogue entry, with its
/// expected observables, lands BEFORE the feature is implemented, so the
/// implementation is written against a failing test rather than graded
/// afterwards by whatever it happened to produce. A declaration that only
/// appears once there is something to compare it with is a declaration copied
/// from the output.
/// </para>
///
/// <para>
/// The two groups are separated on purpose. The counts at the top are
/// properties of the GENERATED VMF and are asserted now — they catch a generator
/// that quietly stops emitting what its entry claims. Everything below is a
/// property of a COMPILED BSP, and each field names the phase that can first
/// read it.
/// </para>
/// </summary>
public sealed record MapObservables
{
    /// <summary>Nothing declared: an entry that has not been thought about yet.</summary>
    public static MapObservables None { get; } = new();

    // ------------------------------------------------------------------
    // Properties of the .vmf — checkable in Phase 1, by this lane's facts.
    // ------------------------------------------------------------------

    /// <summary>World solids in the generated map.</summary>
    public CountRange? WorldBrushes { get; init; }

    /// <summary>Brush entities — entities carrying at least one solid.</summary>
    public CountRange? BrushEntities { get; init; }

    /// <summary>Point entities — entities carrying none.</summary>
    public CountRange? PointEntities { get; init; }

    /// <summary>
    /// Materials the entry must reference at least once.
    ///
    /// <para>
    /// The tool-texture entry is the reason this exists: "it uses playerclip"
    /// is the whole content of that entry, and without this the fact that would
    /// notice it being dropped is a fact about brush counts, which would not.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Materials { get; init; } = [];

    /// <summary>Classnames the entry must contain at least one of.</summary>
    public IReadOnlyList<string> Classnames { get; init; } = [];

    // ------------------------------------------------------------------
    // Properties of the .bsp — Phase 3 (vbsp) unless said otherwise.
    // ------------------------------------------------------------------

    /// <summary>Whether vbsp must report a leak and write a `.lin`. Phase 3.</summary>
    public bool? Leaks { get; init; }

    /// <summary>
    /// Entries in LUMP_AREAS. Phase 3.
    ///
    /// <para>
    /// THE LUMP'S COUNT, not the number of areas a person would say the map
    /// has, and the two differ by one: stock vbsp writes area 0 as a
    /// placeholder, so an ordinary sealed room comes out at 2 and a map with
    /// one sealing areaportal at 3. Measured, not reasoned — every entry below
    /// was compiled with stock vbsp and the lump read back. Declaring the
    /// human count would make every one of these expectations off by one
    /// against the instrument that has to check them.
    /// </para>
    /// </summary>
    public CountRange? Areas { get; init; }

    /// <summary>How many models — worldspawn plus one per brush entity. Phase 3.</summary>
    public CountRange? Models { get; init; }

    /// <summary>Whether any leaf must carry water data. Phase 3.</summary>
    public bool? HasLeafWaterData { get; init; }

    /// <summary>Solids in LUMP_PHYSCOLLIDE. Phase 3, §7.</summary>
    public CountRange? CollisionSolids { get; init; }

    // ------------------------------------------------------------------
    // Properties of the .prt and the visibility lump — Phase 2 (vvis).
    // ------------------------------------------------------------------

    /// <summary>Portals in the `.prt` file. Phase 2 — the first thing vvis reads.</summary>
    public CountRange? Portals { get; init; }

    /// <summary>Visibility clusters. Phase 2.</summary>
    public CountRange? Clusters { get; init; }

    /// <summary>The named places this entry reasons about. Phase 2.</summary>
    public IReadOnlyList<VisProbe> Probes { get; init; } = [];

    /// <summary>Probe pairs whose clusters MUST see each other. Phase 2.</summary>
    public IReadOnlyList<ProbePair> MustSee { get; init; } = [];

    /// <summary>
    /// Probe pairs whose clusters must NOT see each other. Phase 2.
    ///
    /// <para>
    /// The half that actually catches a broken vvis. A PVS that says everything
    /// sees everything is wrong and passes every must-see expectation there is;
    /// only a must-NOT-see can fail it.
    /// </para>
    /// </summary>
    public IReadOnlyList<ProbePair> MustNotSee { get; init; } = [];

    // ------------------------------------------------------------------
    // Properties of the lighting lumps — Phase 4 (vrad).
    // ------------------------------------------------------------------

    /// <summary>Light entities vrad must gather. Phase 4.</summary>
    public CountRange? Lights { get; init; }

    /// <summary>Every probe named by this entry, by name.</summary>
    /// <param name="name">A probe name.</param>
    public VisProbe Probe(string name)
    {
        foreach (VisProbe probe in Probes)
        {
            if (probe.Name == name)
                return probe;
        }

        throw new ArgumentException($"no probe named '{name}'", nameof(name));
    }
}
