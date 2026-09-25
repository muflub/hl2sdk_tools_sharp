namespace SourceSharp.MapTools.Options;

/// <summary>
/// The grid vbsp splits the world model on, in block coordinates.
/// </summary>
/// <param name="MinX">Stock's <c>block_xl</c>.</param>
/// <param name="MinY">Stock's <c>block_yl</c>.</param>
/// <param name="MaxX">Stock's <c>block_xh</c>.</param>
/// <param name="MaxY">Stock's <c>block_yh</c>.</param>
/// <remarks>
/// <para>
/// A block is <c>BLOCKS_SIZE</c> = 1024 world units square
/// (<c>vbsp.cpp:73</c>), and the grid spans <c>COORD_EXTENT/1024</c> = 32
/// blocks each way (<c>worldsize.h:19,28</c>), so the full range is -16..15 in
/// both axes -- exactly stock's <c>BLOCKS_MIN</c>..<c>BLOCKS_MAX</c> at
/// <c>vbsp.cpp:77,78</c> and the initialiser at <c>vbsp.cpp:80</c>.
/// </para>
/// <para>
/// Narrowing the range compiles only part of the map, which is a debugging aid
/// for a level that will not build: stock's <c>-block</c> and <c>-blocks</c>.
/// A record struct rather than four loose ints so that "the whole grid" is one
/// value a caller can compare against, and so a half-set pair is not
/// representable.
/// </para>
/// </remarks>
public readonly record struct BspBlockGrid(int MinX, int MinY, int MaxX, int MaxY)
{
    /// <summary>
    /// The whole world: -16..15 on both axes, as <c>vbsp.cpp:80</c> initialises
    /// it.
    /// </summary>
    public static BspBlockGrid Full { get; } = new(-16, -16, 15, 15);

    /// <summary>One single block, as stock's <c>-block x y</c> selects.</summary>
    /// <param name="x">The block's X coordinate.</param>
    /// <param name="y">The block's Y coordinate.</param>
    /// <returns>A grid covering only that block.</returns>
    public static BspBlockGrid Single(int x, int y) => new(x, y, x, y);
}

/// <summary>
/// What vbsp was asked to do. Defaults are stock's defaults.
/// </summary>
/// <remarks>
/// <para>
/// A typed immutable record rather than an <c>argv</c>, so a host states its
/// intent instead of assembling a command line and a stage cannot read an
/// option nobody passed. The stock-spelling parser is a separate, public
/// function -- <see cref="StockArgs.ParseVbsp"/> -- so a host that genuinely
/// has a Hammer command line can still use one.
/// </para>
/// <para>
/// Every default below is the value of stock's corresponding global at the top
/// of <c>src/utils/vbsp/vbsp.cpp</c> (lines 30-80), and each is cited on the
/// option it belongs to. The <c>qboolean</c> globals with no initialiser
/// (<c>noprune</c>, <c>nodetail</c>, <c>onlyents</c>, ...) are file-scope, so C
/// zero-initialises them: false.
/// </para>
/// <para>
/// <b>There is deliberately no thread count here.</b> Stock parses
/// <c>-threads</c> at <c>vbsp.cpp:935</c> and then throws the answer away at
/// <c>vbsp.cpp:1302</c> -- <c>numthreads = 1; // multiple threads aren't
/// helping...</c> -- so stock vbsp is 100% serial however it was invoked, and
/// its own usage text at <c>vbsp.cpp:1221</c> ("defaults to the # of
/// processors on your machine") is simply wrong. Do not add one here on the
/// strength of that usage text. Parallelism in this port is
/// <see cref="SourceSharp.MapTools.Parallel.CompileParallelism"/>, which every stage takes
/// uniformly, and Phase 3p is where vbsp actually becomes parallel.
/// </para>
/// <para>
/// Options stock parses but this port drops, with reasons from
/// plan_maptools.md 9. The stock-spelling parser still ACCEPTS each of these
/// and reports it as a diagnostic rather than failing, so a pasted Hammer
/// command line keeps working:
/// <list type="bullet">
/// <item><description>
/// <c>-xbox</c> (<c>vbsp.cpp:1136</c>) and every byte-swap path. Its only
/// effect is to set <c>g_NodrawTriggers</c> and <c>g_DisableWaterLighting</c>;
/// the first has its own flag, <see cref="NoDrawTriggers"/>, and the second has
/// no other way in and so is dropped with it.
/// </description></item>
/// <item><description>
/// <c>-glview</c> (<c>vbsp.cpp:940</c>), <c>-dumpcollide</c>
/// (<c>vbsp.cpp:1065</c>) and <c>-dumpstaticprop</c> (<c>vbsp.cpp:1070</c>):
/// debug dumps of intermediate state to files beside the map. The port reports
/// intermediate state as data through <c>Diagnostics</c>, which a host can
/// render however it likes.
/// </description></item>
/// <item><description>
/// <c>-tmpout</c> (<c>vbsp.cpp:1080</c>), which hardcodes <c>/tmp</c> as the
/// output base. Where bytes go is the host's, through
/// <see cref="SourceSharp.MapTools.Io.IFileSystem"/>.
/// </description></item>
/// <item><description>
/// <c>-low</c> (<c>vbsp.cpp:1113</c>): a process priority, which belongs to
/// whoever owns the process and not to a library.
/// </description></item>
/// <item><description>
/// <c>-FullMinidumps</c> (<c>vbsp.cpp:1158</c>), <c>-novconfig</c>
/// (<c>vbsp.cpp:1121</c>), <c>-allowdebug</c>/<c>-steam</c>
/// (<c>vbsp.cpp:1124</c>): launcher plumbing. The last three are already no-ops
/// inside stock's own parse loop.
/// </description></item>
/// <item><description>
/// Everything VMPI. vbsp has no <c>-mpi</c> of its own in this tree, unlike
/// vvis and vrad.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// Two stock options are absent because stock does not have them either: the
/// <c>-maxlightmapdim</c> branch at <c>vbsp.cpp:1041</c> and the
/// <c>-defaultluxelsize</c> branch at <c>vbsp.cpp:1085</c> are both inside
/// <c>#if 0</c> and cannot be reached in any build.
/// </para>
/// <para>
/// Four more are advertised in the usage text and parsed nowhere, so stock
/// itself rejects them and prints the usage: <c>-virtualdispphysics</c>,
/// <c>-x360</c>, <c>-nox360</c> (<c>vbsp.cpp:1255,1257,1258</c>) and
/// <c>-dumpstaticprops</c> -- plural -- at <c>vbsp.cpp:1246</c>, where the
/// parser at <c>vbsp.cpp:1070</c> only matches the singular. They are absent
/// here and the parser treats them as unknown, which is what stock does.
/// </para>
/// </remarks>
public sealed record VbspOptions
{
    /// <summary>Emit the per-stage commentary stock's <c>-v</c> emits.</summary>
    /// <remarks>
    /// Stock's <c>-v</c> or <c>-verbose</c>, <c>vbsp.cpp:944</c>. Default false.
    /// </remarks>
    public bool Verbose { get; init; }

    /// <summary>
    /// Extend <see cref="Verbose"/> commentary to brush submodels.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-verboseentities</c>, <c>vbsp.cpp:1030</c>, default false at
    /// <c>vbsp.cpp:47</c>. Stock's usage text at <c>vbsp.cpp:1223</c> says it
    /// backwards -- "If -v is on, this disables verbose output for submodels"
    /// -- but the only read of the global is <c>vbsp.cpp:877</c>, and it does
    /// the opposite: when the flag is NOT set, vbsp assigns
    /// <c>verbose = false</c> after the first model. So the flag ENABLES
    /// submodel commentary, and its absence silences <see cref="Verbose"/> for
    /// the whole rest of the run rather than for submodels alone. The code is
    /// what is modelled.
    /// </remarks>
    public bool VerboseEntities { get; init; }

    /// <summary>Do not join coincident face vertices together.</summary>
    /// <remarks>Stock's <c>-noweld</c>, <c>vbsp.cpp:949</c>, false at <c>vbsp.cpp:41</c>.</remarks>
    public bool NoWeld { get; init; }

    /// <summary>Do not chop out the parts of brushes that intersect other brushes.</summary>
    /// <remarks>Stock's <c>-nocsg</c>, <c>vbsp.cpp:954</c>, false at <c>vbsp.cpp:40</c>.</remarks>
    public bool NoCsg { get; init; }

    /// <summary>Emit unique face edges instead of sharing them between faces.</summary>
    /// <remarks>Stock's <c>-noshare</c>, <c>vbsp.cpp:959</c>, false at <c>vbsp.cpp:42</c>.</remarks>
    public bool NoShare { get; init; }

    /// <summary>Do not fix up t-junctions.</summary>
    /// <remarks>Stock's <c>-notjunc</c>, <c>vbsp.cpp:964</c>, false at <c>vbsp.cpp:44</c>.</remarks>
    public bool NoTJunc { get; init; }

    /// <summary>Discard water brushes entirely.</summary>
    /// <remarks>Stock's <c>-nowater</c>, <c>vbsp.cpp:969</c>, false at <c>vbsp.cpp:39</c>.</remarks>
    public bool NoWater { get; init; }

    /// <summary>
    /// Keep the outer shell of the map instead of removing the faces no player
    /// can ever see.
    /// </summary>
    /// <remarks>Stock's <c>-noopt</c>, <c>vbsp.cpp:974</c>, false at <c>vbsp.cpp:45</c>.</remarks>
    public bool NoOpt { get; init; }

    /// <summary>Do not prune neighbouring solid nodes out of the tree.</summary>
    /// <remarks>Stock's <c>-noprune</c>, <c>vbsp.cpp:979</c>, false at <c>vbsp.cpp:31</c>.</remarks>
    public bool NoPrune { get; init; }

    /// <summary>Do not merge coplanar chopped faces on nodes.</summary>
    /// <remarks>Stock's <c>-nomerge</c>, <c>vbsp.cpp:984</c>, false at <c>vbsp.cpp:37</c>.</remarks>
    public bool NoMerge { get; init; }

    /// <summary>Do not merge coplanar chopped faces on water surfaces.</summary>
    /// <remarks>Stock's <c>-nomergewater</c>, <c>vbsp.cpp:989</c>, false at <c>vbsp.cpp:38</c>.</remarks>
    public bool NoMergeWater { get; init; }

    /// <summary>Do not subdivide faces for lightmapping.</summary>
    /// <remarks>Stock's <c>-nosubdiv</c>, <c>vbsp.cpp:994</c>, false at <c>vbsp.cpp:43</c>.</remarks>
    public bool NoSubdiv { get; init; }

    /// <summary>Throw away all detail geometry, keeping only what affects visibility.</summary>
    /// <remarks>Stock's <c>-nodetail</c>, <c>vbsp.cpp:999</c>, false at <c>vbsp.cpp:33</c>.</remarks>
    public bool NoDetail { get; init; }

    /// <summary>
    /// Treat every detail brush as world geometry, so detail affects visibility.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-fulldetail</c>, <c>vbsp.cpp:1004</c>, false at
    /// <c>vbsp.cpp:34</c>. The opposite extreme to <see cref="NoDetail"/>;
    /// setting both is meaningless and the parser says so.
    /// </remarks>
    public bool FullDetail { get; init; }

    /// <summary>
    /// Re-import only the entity lump, leaving the existing BSP geometry alone.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-onlyents</c>, <c>vbsp.cpp:1009</c>, false at
    /// <c>vbsp.cpp:35</c>. The fastest possible iteration on entity work, and
    /// the reason it exists: it does NOT reimport brush models.
    /// </remarks>
    public bool OnlyEnts { get; init; }

    /// <summary>Update only the static props and detail props.</summary>
    /// <remarks>Stock's <c>-onlyprops</c>, <c>vbsp.cpp:1014</c>, false at <c>vbsp.cpp:36</c>.</remarks>
    public bool OnlyProps { get; init; }

    /// <summary>
    /// Warn about any brush whose output volume is smaller than this, in cubic
    /// world units.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-micro</c>, <c>vbsp.cpp:1019</c>. Default 1.0, from
    /// <c>vec_t microvolume = 1.0;</c> at <c>vbsp.cpp:30</c>, which the usage
    /// text at <c>vbsp.cpp:1235-1236</c> agrees with.
    /// </remarks>
    public float MicroVolume { get; init; } = 1.0f;

    /// <summary>Stop the compile as soon as a leak is found.</summary>
    /// <remarks>
    /// Stock's <c>-leaktest</c>, <c>vbsp.cpp:1025</c>, false at
    /// <c>vbsp.cpp:46</c>. The leak trail is written either way -- see
    /// <see cref="SourceSharp.MapTools.Diagnostics.LeakReport"/>, which carries it as data instead
    /// of only as a <c>.lin</c> file.
    /// </remarks>
    public bool LeakTest { get; init; }

    /// <summary>Snap axis-aligned brush planes to integer coordinates.</summary>
    /// <remarks>
    /// Stock's <c>-snapaxial</c>, <c>vbsp.cpp:1035</c>, false at
    /// <c>vbsp.cpp:53</c>.
    /// </remarks>
    public bool SnapAxialPlanes { get; init; }

    /// <summary>Which part of the world grid to compile.</summary>
    /// <remarks>
    /// Stock's <c>-block x y</c> (<c>vbsp.cpp:1048</c>) and
    /// <c>-blocks xl yl xh yh</c> (<c>vbsp.cpp:1055</c>), which set the same
    /// four globals. Default is the whole grid, <see cref="BspBlockGrid.Full"/>.
    /// </remarks>
    public BspBlockGrid Blocks { get; init; } = BspBlockGrid.Full;

    /// <summary>Run visibility calculations inside 3D skybox leaves.</summary>
    /// <remarks>
    /// Stock's <c>-forceskyvis</c>, <c>vbsp.cpp:1075</c>. Default false --
    /// <c>vbsp.cpp:51</c> says so in a comment: "skybox vis is off by default".
    /// </remarks>
    public bool ForceSkyVis { get; init; }

    /// <summary>Scale every lightmap on the map by this factor.</summary>
    /// <remarks>
    /// Stock's <c>-luxelscale</c>, <c>vbsp.cpp:1091</c>, default 1.0 at
    /// <c>vbsp.cpp:61</c>. Note that stock applies a DXLevel override to this
    /// AFTER parsing (<c>vbsp.cpp:1292</c>: a scale still at exactly 1.0
    /// becomes 4.0 when <see cref="DxLevel"/> is 70); the parser here does not,
    /// because a parser's job is to say what was asked for. The stage applies
    /// it, and so the value seen here is always the one on the command line.
    /// </remarks>
    public float LuxelScale { get; init; } = 1.0f;

    /// <summary>A floor under every luxel scale on the map.</summary>
    /// <remarks>
    /// Stock's <c>-minluxelscale</c>, <c>vbsp.cpp:1096</c>, default 1.0 at
    /// <c>vbsp.cpp:62</c>, and stock clamps a value below 1 up to 1 as it
    /// parses. This is stock vbsp's ONLY case-sensitive flag: line 1096 uses
    /// plain <c>strcmp</c> where every one of its neighbours in that loop
    /// uses <c>Q_stricmp</c>, so stock rejects <c>-minLuxelScale</c> and accepts
    /// <c>-minluxelscale</c>. That is a typo in stock, not a contract; the
    /// parser here matches it case-insensitively like everything else.
    /// </remarks>
    public float MinLuxelScale { get; init; } = 1.0f;

    /// <summary>
    /// The DirectX feature level the map is being built for, or 0 for "not
    /// specified".
    /// </summary>
    /// <remarks>
    /// Stock's <c>-dxlevel</c>, <c>vbsp.cpp:1103</c>, default 0 at
    /// <c>vbsp.cpp:65</c> -- whose own comment reads "default dxlevel if you
    /// don't specify it on the command-line". Stock uses it for exactly two
    /// post-parse decisions, at <c>vbsp.cpp:1288</c> and <c>vbsp.cpp:1292</c>:
    /// below 80 it forces <see cref="BumpAll"/> off, and at exactly 70 it
    /// raises a default <see cref="LuxelScale"/> to 4.0. Both belong to the
    /// stage, not to the parser.
    /// </remarks>
    public int DxLevel { get; init; }

    /// <summary>Force every surface to be bump mapped.</summary>
    /// <remarks>
    /// Stock's <c>-bumpall</c>, <c>vbsp.cpp:1109</c>, false at
    /// <c>vbsp.cpp:63</c>. Silently ignored by stock when
    /// <see cref="DxLevel"/> is set below 80.
    /// </remarks>
    public bool BumpAll { get; init; }

    /// <summary>
    /// Generate lightmaps for every surface, including ones that do not need
    /// them.
    /// </summary>
    /// <remarks>Stock's <c>-lightifmissing</c>, <c>vbsp.cpp:1117</c>, false at <c>vbsp.cpp:52</c>.</remarks>
    public bool LightIfMissing { get; init; }

    /// <summary>
    /// Leave the BSP's embedded zip (pakfile) lump as it is and regenerate
    /// everything else.
    /// </summary>
    /// <remarks>Stock's <c>-keepstalezip</c>, <c>vbsp.cpp:1132</c>, false at <c>vbsp.cpp:54</c>.</remarks>
    public bool KeepStaleZip { get; init; }

    /// <summary>
    /// Allow the cracks that appear where detail geometry meets world geometry
    /// rather than closing them.
    /// </summary>
    /// <remarks>Stock's <c>-allowdetailcracks</c>, <c>vbsp.cpp:1142</c>, false at <c>vbsp.cpp:57</c>.</remarks>
    public bool AllowDetailCracks { get; init; }

    /// <summary>
    /// Do not precompute displacement collision models; let the physics engine
    /// build them from the displacement at load time.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-novirtualmesh</c>, <c>vbsp.cpp:1146</c>, false at
    /// <c>vbsp.cpp:58</c>. Its name reads backwards against the usage text,
    /// which advertises a <c>-virtualdispphysics</c> that no build parses: the
    /// flag that exists TURNS OFF the virtual mesh, i.e. it asks for the
    /// precomputed models. False, the default, means virtual meshes are used.
    /// </remarks>
    public bool NoVirtualMesh { get; init; }

    /// <summary>
    /// Substitute materials according to the mod's <c>materialsub.txt</c>.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-replacematerials</c>, <c>vbsp.cpp:1150</c>, false at
    /// <c>materialsub.cpp:14</c>. Stock reads the table from
    /// <c>content\maps\materialsub.txt</c> and turns the flag back off at
    /// <c>materialsub.cpp:49</c> when the file is missing.
    /// </remarks>
    public bool ReplaceMaterials { get; init; }

    /// <summary>Compile trigger brushes with the nodraw material.</summary>
    /// <remarks>
    /// Stock's <c>-nodrawtriggers</c>, <c>vbsp.cpp:1154</c>, false at
    /// <c>vbsp.cpp:55</c>. Stock's <c>-xbox</c> forces it on; that path is
    /// dropped, this flag is not.
    /// </remarks>
    public bool NoDrawTriggers { get; init; }

    /// <summary>
    /// A directory whose entire contents are embedded in the compiled map's
    /// pakfile lump, or null to embed nothing.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-embed &lt;directory&gt;</c>, <c>vbsp.cpp:1162</c>, empty at
    /// <c>vbsp.cpp:69</c>. Stock also adds it as a GAME and MOD search path,
    /// which this port does not do from here: mounting content is the host's
    /// job through <see cref="SourceSharp.MapTools.Io.IContentFileSystem"/>. Stock refuses the
    /// combination of <c>-embed</c> with <see cref="OnlyEnts"/> or
    /// <see cref="OnlyProps"/> by calling <c>CmdLib_Exit(1)</c> at
    /// <c>vbsp.cpp:1277</c>; the parser here reports that as an error
    /// diagnostic and returns.
    /// </remarks>
    public string? EmbedDirectory { get; init; }

    /// <summary>
    /// Whether to reproduce the stock tools' defects or do the right thing.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="CompliancePolicy.Correct"/>, which is the one
    /// default on this record that is deliberately NOT stock's behaviour. See
    /// <see cref="ComplianceOptions"/>. Byte-exact comparisons against stock
    /// output must set <see cref="ComplianceOptions.Stock"/>.
    /// </remarks>
    public ComplianceOptions Compliance { get; init; } = ComplianceOptions.Correct;

    /// <summary>
    /// Which collision cooker builds the physics lumps, as
    /// <c>-cooker native|managed|none</c> (<c>vphysics</c> is an alias of <c>native</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a stock option: stock vbsp can only load <c>vphysics</c> (<c>vbsp.cpp:1309</c>).
    /// <see cref="CollisionCookerKind.Native"/> drives Valve's <c>vphysics.so</c> (chosen by
    /// <see cref="VPhysicsLibrary"/>); <see cref="CollisionCookerKind.Managed"/> needs no native
    /// library and follows <see cref="Compliance"/> (TF2's double arithmetic when correct, SDK
    /// 2013's float arithmetic when stock); <see cref="CollisionCookerKind.None"/> writes no
    /// collision lumps.
    /// </para>
    /// <para>
    /// <b>Managed by default (user ruling Q19, 2026-09-22).</b> Measured by lane p8a through
    /// the real driver: byte-identical to the native cooker on all 136 real-map gate compiles
    /// (stock compliance against SDK 2013's library, correct against TF2's), 2.6x faster per
    /// compile, deterministic at any thread count, and needs no SDK install. Native stays
    /// available as <c>-cooker native</c> and is the reference the gates compare against.
    /// </para>
    /// </remarks>
    public CollisionCookerKind Cooker { get; init; } = CollisionCookerKind.Managed;

    /// <summary>
    /// Which <c>vphysics.so</c> the native cooker loads, as <c>-vphysics &lt;game|path&gt;</c>
    /// (<c>ssmap phys list</c> names the games); null for the default (SDK Base 2013 MP's).
    /// </summary>
    /// <remarks>Ignored by the managed cooker, which loads no library.</remarks>
    public string? VPhysicsLibrary { get; init; }

    /// <summary>
    /// The output format the run writes with, as resolved by
    /// <see cref="FormatResolution.Resolve"/>. Default is the no-preset
    /// format — every field at what the writer does today — so a run that
    /// never touches a format flag, a preset, or a gameinfo auto-detect
    /// writes exactly the bytes it wrote before this field existed.
    /// </summary>
    /// <remarks>
    /// The parser does NOT write this field: <see cref="StockArgs.ParseVbsp"/>
    /// reports the raw CLI overlay on
    /// <see cref="StockArgsResult{VbspOptions}.Format"/>; the host resolves
    /// defaults → Tools key → appid preset → CLI and stores the result here
    /// before handing the options to the compile. That ordering is
    /// last-writer-wins and documented on <see cref="FormatResolution"/>.
    /// </remarks>
    public FormatOptions Format { get; init; } = FormatOptions.Default;

    /// <summary>Stock's defaults: a full compile of the whole grid.</summary>
    public static VbspOptions Default { get; } = new();

    /// <summary>
    /// Re-import the entity lump only, as <c>-onlyents</c>.
    /// </summary>
    public static VbspOptions OnlyEntsDefault { get; } = new() { OnlyEnts = true };
}

/// <summary>Which collision cooker a compile uses.</summary>
public enum CollisionCookerKind
{
    /// <summary>Valve's closed <c>vphysics.so</c>, driven through its vtable (one thread).</summary>
    Native = 0,

    /// <summary>The managed IVP reimplementation (any number of threads, no native library).</summary>
    Managed = 1,

    /// <summary>No cooker: no LUMP_PHYSCOLLIDE and no LUMP_PHYSDISP.</summary>
    None = 2,
}
