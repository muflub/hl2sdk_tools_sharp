namespace SourceSharp.Tests.MapTools.Rad.Final;

/// <summary>
/// The lighting gates' tolerances, set from the first measurement and frozen
/// (plan §2 I2): per map, how many LIGHTING records may differ from stock.
/// </summary>
/// <remarks>
/// <para>
/// A map not listed must be byte-identical. Every listed number is the count
/// measured at p4f commit 6163bb294 (all four tables, one run);
/// a regression makes the count go UP and fails, an improvement passes and
/// should be followed by lowering the number here.
/// </para>
/// <para>
/// The causes, measured (p4f-findings.md): with supersampling, stock reads two
/// uninitialised stack buffers -- <c>pSampleIntensity</c> for luxels with no
/// sample (<c>lightmap.cpp:2881</c>, modelled by
/// <c>StockQuirk.SupersampleGradientReadsUninitialised</c>) and
/// <c>PointsInWinding</c>'s <c>invalidMask</c> (<c>lightmap.cpp:2644</c>;
/// <c>movaps xmm11,[rsp]</c> at vrad_dll.dll+0x17c51, never written) -- so
/// which edge samples are supersampled, and which partial-sample subsamples
/// count, depend on stack garbage. Without supersampling (b0nx) the only
/// residue is single visibility rays grazing brush edges.
/// </para>
/// </remarks>
internal static class FrozenTolerance
{
    private static readonly Dictionary<string, int> Ldr = new(StringComparer.Ordinal)
    {
        ["l1_areaportal"] = 4,
        ["l1_default_cubemap"] = 2,
        ["l1_detail_props"] = 22,
        ["l1_env_cubemap"] = 2,
        ["l1_hint_skip"] = 2,
        ["l1_missing_prop_model"] = 189,
        ["l1_open_arena"] = 6,
        ["l1_overlay"] = 2,
        ["l1_sealed_room"] = 2,
        ["l1_static_prop"] = 258,
        ["l1_static_prop_solid_types"] = 1087,
        ["l1_three_doors_almost_in_a_line"] = 10,
        ["l1_three_doors_in_a_line"] = 16,
        ["l1_two_rooms_and_a_door"] = 4,
        ["l1_water_and_fog_leaves"] = 4,
        ["l2_areaportal_between_pools"] = 4,
        ["l2_props_overlays_detail_cubemap"] = 153,
        ["l3_arena_144_pillars"] = 34,
        ["l3_corridor_64_rooms"] = 12,
        ["p3f_grid_mixed"] = 53,
        ["p3f_grid_same"] = 1,
        ["p3f_half_edge_mixed"] = 44,
        ["p3f_lm8_clamp"] = 13,
        ["p3f_p4_random"] = 56,
        ["p3f_rotated"] = 1,
        ["p3f_strip"] = 22,
        ["p3f_tags_flags"] = 1,
        ["p3f_wall"] = 1,
        ["ss_sandbox"] = 4104,
    };

    private static readonly Dictionary<string, int> LdrNoExtra = new(StringComparer.Ordinal)
    {
        ["l1_detail_props"] = 22,
        ["l1_missing_prop_model"] = 76,
        ["l1_static_prop"] = 208,
        ["l1_static_prop_solid_types"] = 825,
        ["l2_props_overlays_detail_cubemap"] = 108,
        ["l3_arena_144_pillars"] = 16,
        ["ss_sandbox"] = 87,
    };

    // The same maps and the same counts as LDR, record for record: the HDR
    // pass differs from stock exactly where the LDR pass does, which is what a
    // range-independent cause predicts.
    private static readonly Dictionary<string, int> Hdr = new(StringComparer.Ordinal)
    {
        ["l1_areaportal"] = 4,
        ["l1_default_cubemap"] = 2,
        ["l1_detail_props"] = 22,
        ["l1_env_cubemap"] = 2,
        ["l1_hint_skip"] = 2,
        ["l1_missing_prop_model"] = 189,
        ["l1_open_arena"] = 6,
        ["l1_overlay"] = 2,
        ["l1_sealed_room"] = 2,
        ["l1_static_prop"] = 258,
        ["l1_static_prop_solid_types"] = 1087,
        ["l1_three_doors_almost_in_a_line"] = 10,
        ["l1_three_doors_in_a_line"] = 16,
        ["l1_two_rooms_and_a_door"] = 4,
        ["l1_water_and_fog_leaves"] = 4,
        ["l2_areaportal_between_pools"] = 4,
        ["l2_props_overlays_detail_cubemap"] = 153,
        ["l3_arena_144_pillars"] = 34,
        ["l3_corridor_64_rooms"] = 12,
        ["ss_sandbox"] = 4104,
    };

    /// <summary>LDR records allowed to differ from stock <c>-bounce 0</c>.</summary>
    public static int LdrDifferingRecords(string map) => Ldr.GetValueOrDefault(map);

    /// <summary>LDR records allowed to differ from stock <c>-bounce 0 -noextra</c>.</summary>
    public static int LdrNoExtraDifferingRecords(string map) => LdrNoExtra.GetValueOrDefault(map);

    private static readonly Dictionary<string, int> Full = new(StringComparer.Ordinal)
    {
        ["l1_areaportal"] = 11,
        ["l1_corridor_farz"] = 3,
        ["l1_corridor_radius_override"] = 3,
        ["l1_default_cubemap"] = 1,
        ["l1_detail_props"] = 222,
        ["l1_env_cubemap"] = 1,
        ["l1_func_detail"] = 174,
        ["l1_hint_skip"] = 1,
        ["l1_long_corridor"] = 17,
        ["l1_missing_prop_model"] = 3246,
        ["l1_open_arena"] = 777,
        ["l1_overlay"] = 3,
        ["l1_sealed_room"] = 3,
        ["l1_static_prop"] = 4148,
        ["l1_static_prop_solid_types"] = 8090,
        ["l1_three_doors_almost_in_a_line"] = 33,
        ["l1_three_doors_in_a_line"] = 48,
        ["l1_tool_textures"] = 2,
        ["l1_two_rooms_and_a_door"] = 21,
        ["l1_vvis_fast"] = 1,
        ["l1_water_and_fog_leaves"] = 15,
        ["l2_areaportal_between_pools"] = 11,
        ["l2_detail_and_hint_in_a_corridor"] = 118,
        ["l2_props_overlays_detail_cubemap"] = 4445,
        ["l3_arena_144_pillars"] = 19788,
        ["l3_corridor_64_rooms"] = 116,
        ["p3f_corner_only"] = 31,
        ["p3f_grid_mixed"] = 1701,
        ["p3f_grid_same"] = 24,
        ["p3f_half_edge"] = 30,
        ["p3f_half_edge_mixed"] = 506,
        ["p3f_lm8_clamp"] = 963,
        ["p3f_offsets"] = 12,
        ["p3f_p2_blend"] = 37,
        ["p3f_p2_flat"] = 115,
        ["p3f_p3_bump"] = 23,
        ["p3f_p4_random"] = 754,
        ["p3f_rotated"] = 23,
        ["p3f_slope"] = 120,
        ["p3f_smooth_mintess"] = 16,
        ["p3f_strip"] = 639,
        ["p3f_swap"] = 32,
        ["p3f_tags_flags"] = 22,
        ["p3f_wall"] = 14,
        ["ss_sandbox"] = 5236,
    };

    /// <summary>LDR records allowed to differ from stock's default (bounced) compile.</summary>
    public static int FullDifferingRecords(string map) => Full.GetValueOrDefault(map);

    /// <summary>HDR records allowed to differ from stock <c>-bounce 0 -both</c>.</summary>
    public static int HdrDifferingRecords(string map) => Hdr.GetValueOrDefault(map);
}
