//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapGen.Catalog;

/// <summary>
/// How complex an entry is, from the catalogue's level table.
///
/// <para>
/// The grade is the whole point of the catalogue. A real map exercises every
/// feature at once, so when it comes out wrong it says nothing about WHICH
/// feature broke; an L1 entry is a sealed room plus exactly one feature, so a
/// failure names the feature by name.
/// </para>
/// </summary>
public enum MapLevel
{
    /// <summary>
    /// Micro: a handful of brushes, no shell, oracle hand-computed.
    ///
    /// <para>
    /// §2a describes L0 as `MapFixtures` fed to the compiler as an in-memory
    /// `MapFile` model. That model arrives with vbsp in Phase 3; until then an
    /// L0 entry is the same handful of brushes emitted as VMF text, and the
    /// declaration below does not change when the in-memory path lands.
    /// </para>
    /// </summary>
    L0,

    /// <summary>
    /// One feature: a sealed room plus exactly one thing under test.
    ///
    /// <para>
    /// "Exactly one" is enforced — see the matrix facts. An L1 entry that
    /// declares two features is not an L1 entry, because a failure in it no
    /// longer names one feature.
    /// </para>
    /// </summary>
    L1,

    /// <summary>Interaction: features known to interfere, in deliberate pairs and triples.</summary>
    L2,

    /// <summary>
    /// Scale: the same generators with a size knob — N rooms, N brushes, up to
    /// and past the MAX_MAP_* limits.
    /// </summary>
    L3,

    /// <summary>
    /// Real maps: the corpus (`ss_sandbox.vmf`, the shipped content maps).
    ///
    /// <para>
    /// Declared for completeness and deliberately EMPTY in this catalogue: an
    /// L4 map is a file on disk, not a generator, so it has no entry here. The
    /// corpus is reached through its own path.
    /// </para>
    /// </summary>
    L4,
}
