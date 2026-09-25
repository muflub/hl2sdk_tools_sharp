namespace SourceSharp.MapTools.Options;

/// <summary>
/// One of the six output-format presets Tools++ switches between, as a
/// frozen overlay: a null field means the preset does not touch it, so
/// presets compose as last-write-wins per field exactly like ++'s callback
/// bodies, which each store only their own fields and leave the rest of the
/// globals as they found them.
/// </summary>
/// <remarks>
/// <para>
/// The table is the decompilation report
/// (<c>~/re/toolsplusplus/toolsplusplus-findings.md</c> §1.1) as
/// re-derived from the binary during this port: the callback bodies at
/// <c>0x1400429c0…0x140042aff</c> and the auto-detect switch at
/// <c>0x140041c9a…0x140041dbe</c> of <c>vbspplusplus.exe</c>
/// (<c>objdump -M intel</c>, this session). Where the report and the dump
/// disagree the dump wins, and the findings file names each correction:
/// the <c>-singleplayer</c> callback is NOT a default-reset no-op (it stores
/// matsys-compat and the static-props token), the plain token <c>"10"</c>
/// is the Insurgency flavour while <c>"10_TF2"</c> is the TF2 one, and
/// appid 4465480 falls in the csgo bucket, not the singleplayer one.
/// </para>
/// <para>
/// The token strings in <see cref="StaticPropsToken"/> are ++'s
/// <c>-staticpropformat</c> token table exactly. ++ maps a token to a
/// stored index 0..9 (<c>"6"→0, "7"→1, "8"→2, "9"→3, "10_TF2"→4, "10"→5,
/// "11"→6, "12"→7, "13"→8, "14"→9</c>; the jump table at
/// <c>0x1400e696c</c>, <c>"10_TF2"</c> compared by <c>strcmp</c> first);
/// the written GAMELUMP_STATIC_PROPS version of a digit token is the digit
/// and <c>"10_TF2"</c> writes version 10 with the TF2 flavour. That mapping
/// is the seam into the BSP writer (<c>Compile.BspFormatWriter.ToWriteFormat</c>,
/// wired into the save call sites by the format-plumbing lane); this port does
/// not invent it. The default preset leaves the token null, which means the
/// writer default — today's pinned version 10 — so the no-preset path stays
/// byte-identical.
/// </para>
/// </remarks>
public sealed record MapFormatPreset
{
    /// <summary>
    /// Builds a preset overlay.
    /// </summary>
    /// <param name="name">The preset's command-line name.</param>
    /// <param name="bspVersion">
    /// The GAMELUMP_MAPVERS version, or null to leave the writer default.
    /// </param>
    /// <param name="worldLightVersion">The lightgroups version, or null.</param>
    /// <param name="staticPropsToken">
    /// The <c>-staticpropformat</c> token, or null to leave the writer default.
    /// </param>
    /// <param name="matsysCompat">Whether the matsys-compat lump set is on, or null.</param>
    /// <param name="simpleLadders">Whether simple ladders are on, or null.</param>
    /// <param name="noDisp4VirtualMesh">Whether the disp 4 virtual mesh is off, or null.</param>
    /// <param name="noIneligibleVertexLitProps">
    /// Whether ineligible vertex-lit props are skipped, or null.
    /// </param>
    /// <param name="csgoClipContents">Whether the csgo clip contents are on, or null.</param>
    /// <param name="dispInfoLimit">The <c>-dispinfolimit</c> value, or null.</param>
    /// <param name="l4d2LumpDirLayout">Whether the L4D2 lump-dir layout is on, or null.</param>
    public MapFormatPreset(
        string name,
        int? bspVersion = null,
        int? worldLightVersion = null,
        string? staticPropsToken = null,
        bool? matsysCompat = null,
        bool? simpleLadders = null,
        bool? noDisp4VirtualMesh = null,
        bool? noIneligibleVertexLitProps = null,
        bool? csgoClipContents = null,
        int? dispInfoLimit = null,
        bool? l4d2LumpDirLayout = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        BspVersion = bspVersion;
        WorldLightVersion = worldLightVersion;
        StaticPropsToken = staticPropsToken;
        MatsysCompat = matsysCompat;
        SimpleLadders = simpleLadders;
        NoDisp4VirtualMesh = noDisp4VirtualMesh;
        NoIneligibleVertexLitProps = noIneligibleVertexLitProps;
        CsgoClipContents = csgoClipContents;
        DispInfoLimit = dispInfoLimit;
        L4d2LumpDirLayout = l4d2LumpDirLayout;
    }

    /// <summary>The preset's command-line name (also its diagnostics label).</summary>
    public string Name { get; }

    /// <summary>GAMELUMP_MAPVERS version (++ <c>bspver</c>).</summary>
    public int? BspVersion { get; }

    /// <summary>GAMELUMP_LIGHTGROUPS version (++ <c>lightver</c>).</summary>
    public int? WorldLightVersion { get; }

    /// <summary>
    /// The written static-props token (++ <c>propper</c>), from
    /// <see cref="FormatOptions.StaticPropsTokens"/>; null keeps the writer default.
    /// </summary>
    public string? StaticPropsToken { get; }

    /// <summary>Overlay flag; null means this preset does not touch it.</summary>
    public bool? MatsysCompat { get; }

    /// <summary>Overlay flag; null means this preset does not touch it.</summary>
    public bool? SimpleLadders { get; }

    /// <summary>Overlay flag; null means this preset does not touch it.</summary>
    public bool? NoDisp4VirtualMesh { get; }

    /// <summary>Overlay flag; null means this preset does not touch it.</summary>
    public bool? NoIneligibleVertexLitProps { get; }

    /// <summary>Overlay flag; null means this preset does not touch it.</summary>
    public bool? CsgoClipContents { get; }

    /// <summary>Overlay value; null means this preset does not touch it.</summary>
    public int? DispInfoLimit { get; }

    /// <summary>Overlay flag; null means this preset does not touch it.</summary>
    public bool? L4d2LumpDirLayout { get; }

    /// <summary>
    /// The <c>-singleplayer</c> preset: the 2007 singleplayer branch. The
    /// dump's callback (<c>0x1400429c0</c>) stores exactly two globals —
    /// matsys-compat and the static-props token <c>"6"</c> (stored index 0)
    /// — and leaves bsp and light versions at the writer defaults (20 and 0),
    /// which matches the report matrix's "19/20 path, flag resets to 20"
    /// cell; the report's "resets defaults / writes none" claim is wrong
    /// against the dump (the stores are there at <c>0x1400429c0</c>).
    /// </summary>
    public static MapFormatPreset Singleplayer { get; } =
        new("singleplayer", null, null, "6", true);

    /// <summary>
    /// The <c>-portal2</c> preset: bsp version 21, lightgroups on, the
    /// version-9 (L4D2-era) static-props token, matsys-compat and the disp
    /// virtual-mesh suppression on (callback <c>0x140042a10</c>).
    /// </summary>
    public static MapFormatPreset Portal2 { get; } =
        new("portal2", 21, 1, "9", true, null, true);

    /// <summary>
    /// The <c>-l4d2</c> preset: bsp version 21, lightgroups on, the
    /// version-9 static-props token, matsys-compat, simple ladders, the disp
    /// virtual-mesh suppression, and the tail store of the L4D2 lump-dir
    /// layout (callback <c>0x140042a40</c>, tail <c>jmp 0x140051b50</c> with
    /// <c>cl = 1</c>).
    /// </summary>
    public static MapFormatPreset L4d2 { get; } =
        new("l4d2", 21, 1, "9", true, true, true, null, null, null, true);

    /// <summary>
    /// The <c>-asw</c> preset: bsp version 21, lightgroups on, and the
    /// version-7 (L4D-era) static-props token, with matsys-compat and the
    /// disp virtual-mesh suppression (callback <c>0x1400429e0</c>; the
    /// report's table cell missed the lightgroups store, the dump has it).
    /// </summary>
    public static MapFormatPreset Asw { get; } = new("asw", 21, 1, "7", true, null, true);

    /// <summary>
    /// The <c>-insurgency</c> preset: bsp version 21, lightgroups on, the
    /// plain version-10 static-props token (the Insurgency flavour, stored
    /// index 5 — NOT the TF2 flavour), matsys-compat, the disp virtual-mesh
    /// suppression, and ineligible vertex-lit props skipped (callback
    /// <c>0x140042a80</c>). CLI-only: no appid selects it (222880, the
    /// Insurgency game, selects csgo).
    /// </summary>
    public static MapFormatPreset Insurgency { get; } =
        new("insurgency", 21, 1, "10", true, null, true, true);

    /// <summary>
    /// The <c>-csgo</c> preset: the 2013 branch — bsp version 21, lightgroups
    /// on, the version-11 static-props token, matsys-compat, the disp
    /// virtual-mesh suppression, ineligible vertex-lit props skipped, the
    /// csgo clip contents, and 32,768 disp infos (callback
    /// <c>0x140042ac0</c>, tail <c>mov ecx,0x8000; jmp 0x140055ec0</c> — the
    /// literal is the ground truth).
    /// </summary>
    public static MapFormatPreset Csgo { get; } = new(
        "csgo", 21, 1, "11", true, null, true, true, true, 32768);

    /// <summary>Every named preset, in ++'s registration order.</summary>
    public static IReadOnlyList<MapFormatPreset> All { get; } =
        [Singleplayer, Portal2, L4d2, Asw, Insurgency, Csgo];

    /// <summary>
    /// Looks up a preset by the flag spelling, with or without the leading
    /// dash, case-insensitive like ++'s table walk.
    /// </summary>
    /// <param name="name">The flag or preset name.</param>
    /// <param name="preset">The preset, when the name is one.</param>
    /// <returns>Whether the name named a preset.</returns>
    public static bool TryByName(string name, out MapFormatPreset? preset)
    {
        ArgumentNullException.ThrowIfNull(name);
        string key = name.Length > 0 && name[0] == '-' ? name[1..] : name;
        foreach (MapFormatPreset candidate in All)
        {
            if (string.Equals(candidate.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                preset = candidate;
                return true;
            }
        }

        preset = null;
        return false;
    }

    /// <summary>
    /// This preset's fields as an overlay record: non-null only where the
    /// preset speaks, so <see cref="FormatOverrides.ApplyOver"/> writes in
    /// exactly the fields the preset's callback stores.
    /// </summary>
    /// <returns>The overlay form of this preset.</returns>
    public FormatOverrides ToOverrides() => new(
        BspVersion,
        WorldLightVersion,
        StaticPropsToken,
        MatsysCompat,
        SimpleLadders,
        NoDisp4VirtualMesh,
        NoIneligibleVertexLitProps,
        CsgoClipContents,
        DispInfoLimit,
        L4d2LumpDirLayout);

    /// <summary>
    /// The auto-detect table: an appid to its preset. The compared constants
    /// are disassembly ground truth (the switch at
    /// <c>0x140041c9a…0x140041dbe</c>: the targets load one of the five
    /// preset names into <c>r12</c> and jump to the apply block, and every
    /// other appid jumps to <c>0x140041dbe</c>, which applies nothing and
    /// prints nothing); the titles are this port's guesses at what each
    /// appid is, so they stay comments, not data. Three placements the
    /// report got wrong and the dump corrects (findings file, fact list):
    /// 550 is the l4d2 preset (its own compare + register), 4465480 is csgo,
    /// and 619 is only a range bound, not a bucket member — it selects
    /// nothing. No TF2 appid bucket exists at all: a TF2 gameinfo lands in
    /// the default (no preset applied) bucket.
    /// </summary>
    public static IReadOnlyDictionary<int, MapFormatPreset> AutoDetectTable { get; } =
        new Dictionary<int, MapFormatPreset>
        {
            [220] = Singleplayer, // Half-Life 2 Deathmatch
            [400] = Singleplayer, // Half-Life 2: Deathmatch
            [17520] = Singleplayer, // title guess
            [243730] = Singleplayer, // title guess
            [714070] = Singleplayer, // title guess
            [1583720] = Singleplayer, // title guess
            [550] = L4d2, // Left 4 Dead 2
            [620] = Portal2, // Portal 2
            [630] = Asw, // Alien Swarm
            [563560] = Asw, // Alien Swarm SDK
            [730] = Csgo, // Counter-Strike: Global Offensive
            [222880] = Csgo, // Insurgency (the game; not the -insurgency preset)
            [362890] = Csgo, // title guess
            [447820] = Csgo, // title guess
            [4465480] = Csgo, // title guess
        };

    /// <summary>
    /// The preset a mounted game's <c>SteamAppId</c> selects, or null when no
    /// bucket matches — no match is the SDK-2013 default bucket (nothing
    /// applied, nothing printed), not an error.
    /// </summary>
    /// <param name="steamAppId">The mounted <c>gameinfo.txt</c>'s appid.</param>
    /// <param name="preset">The preset, when the appid is in the table.</param>
    /// <returns>Whether the appid selects a preset.</returns>
    public static bool TryFromSteamAppId(int steamAppId, out MapFormatPreset? preset) =>
        AutoDetectTable.TryGetValue(steamAppId, out preset);
}
