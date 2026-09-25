using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// One cordon volume: <c>Cordon_t</c>, <c>utils/vbsp/manifest.h:19</c>.
/// </summary>
/// <param name="Name">The cordon's name.</param>
/// <param name="Active">Whether this cordon culls.</param>
/// <param name="Boxes">Its volumes.</param>
public sealed record MapCordon(string Name, bool Active, IReadOnlyList<(Vec3 Mins, Vec3 Maxs)> Boxes);

/// <summary>
/// <c>CManifest</c>, <c>utils/vbsp/manifest.cpp</c>: a <c>.vmm</c> and the
/// per-user <c>.vmm_prefs</c> beside it.
/// </summary>
/// <remarks>
/// <para>
/// A manifest is a list of sub-VMFs that compile as one map. vbsp does NOT
/// merge them itself: <c>LoadSubMaps</c> fabricates one synthetic worldspawn
/// and one <c>func_instance</c> per sub-map, all at the origin with
/// <c>fixup_style 2</c>, and the ordinary instance pipeline does the rest
/// (<c>manifest.cpp:291</c>). So a manifest compile is an instance compile
/// with the entities written by the compiler instead of by Hammer.
/// </para>
/// <para>
/// <b>Cordon walls are not generated here, and stock does not generate them
/// either.</b> Hammer writes finished cordon-wall <c>solid</c> chunks into the
/// prefs file; vbsp parses them with the ordinary brush loader and reparents
/// them to worldspawn at the end of <see cref="CordonWorld"/>. There is no
/// cordon material name anywhere in the tree — searching for one is how that
/// was established.
/// </para>
/// <para>
/// The prefs file is named after the logged-in user
/// (<c>&lt;manifest&gt;\&lt;username&gt;.vmm_prefs</c>,
/// <c>manifest.cpp:367</c>), which makes a manifest compile depend on WHO runs
/// it. <see cref="LoadAsync"/> takes the user name explicitly rather than
/// reading the environment, so a compile is reproducible and a test can pin
/// it.
/// </para>
/// </remarks>
public sealed class MapManifest
{
    /// <summary>The chunk holding the sub-map list.</summary>
    public const string MapsChunk = "Maps";

    /// <summary>The chunk holding one sub-map.</summary>
    public const string VmfChunk = "VMF";

    /// <summary>The prefs chunk holding cordons and cordon brushes.</summary>
    public const string CordoningChunk = "cordoning";

    /// <summary>The prefs extension, per user.</summary>
    public const string PrefsExtension = ".vmm_prefs";

    private readonly List<MapCordon> _cordons = [];

    private MapManifest(MapFile map, string instanceDirectory)
    {
        Map = map;
        InstanceDirectory = instanceDirectory;
    }

    /// <summary>The map the manifest's sub-maps are merged into.</summary>
    public MapFile Map { get; }

    /// <summary>
    /// The directory the sub-VMFs live in: the manifest's path with its
    /// extension stripped, plus a separator (<c>manifest.cpp:423-424</c>).
    /// </summary>
    public string InstanceDirectory { get; }

    /// <summary>The sub-map file names, in manifest order.</summary>
    public IReadOnlyList<string> SubMaps { get; private set; } = [];

    /// <summary>Whether cordoning is switched on at all.</summary>
    public bool IsCordoning { get; private set; }

    /// <summary>The cordon volumes.</summary>
    public IReadOnlyList<MapCordon> Cordons => _cordons;

    /// <summary>
    /// The anonymous entity the cordon wall brushes were loaded into, or null.
    /// </summary>
    /// <remarks>
    /// It has no classname and no keys at all — it exists only to hold brushes
    /// until <see cref="CordonWorld"/> hands them to worldspawn
    /// (<c>manifest.cpp:231-240</c>).
    /// </remarks>
    public MapEntity? CordoningEntity { get; private set; }

    /// <summary>
    /// Reads a <c>.vmm</c> and the prefs beside it:
    /// <c>CManifest::LoadVMFManifest</c>, <c>utils/vbsp/manifest.cpp:421</c>.
    /// </summary>
    /// <param name="context">The compile.</param>
    /// <param name="files">Where the manifest and prefs are read from.</param>
    /// <param name="path">The <c>.vmm</c>.</param>
    /// <param name="userName">
    /// Whose <c>.vmm_prefs</c> to read. Stock uses the logged-in user and falls
    /// back to <c>default</c>; null here means read no prefs at all, which is
    /// what a machine with no prefs file gets.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="ArgumentNullException">Any required argument is null.</exception>
    /// <remarks>
    /// The order is the sub-map entities first and the cordoning entity last
    /// (<c>manifest.cpp:441-445</c>), so the cordon walls are entity
    /// <c>1 + submaps</c> and their brushes start at brush 0 — the map is still
    /// brush-empty when the prefs are read.
    /// </remarks>
    public static async Task<MapManifest> LoadAsync(
        VbspContext context,
        IFileSystem files,
        VPath path,
        string? userName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(files);
        string withoutExtension = StripExtension(path.Value);

        MapFile map = new(context.Windings);
        context.Maps.Add(map);
        context.MainMap ??= map;
        context.LoadingMap = map;

        MapManifest manifest = new(map, withoutExtension + "/");

        await using (Stream stream = await files.OpenReadAsync(path, cancellationToken)
            .ConfigureAwait(false))
        {
            VmfDocument document = await VmfDocument.ReadAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            manifest.ReadMaps(document);
        }

        manifest.CreateSubMapEntities();

        if (userName is not null)
        {
            VPath prefs = VPath.Create($"{withoutExtension}/{userName}{PrefsExtension}");

            if (await files.ExistsAsync(prefs, cancellationToken).ConfigureAwait(false))
            {
                await using Stream stream = await files.OpenReadAsync(prefs, cancellationToken)
                    .ConfigureAwait(false);
                VmfDocument document = await VmfDocument.ReadAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
                await manifest.ReadPrefsAsync(context, document, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return manifest;
    }

    /// <summary>
    /// Culls the map to the active cordons and folds the cordon walls into
    /// worldspawn: <c>CManifest::CordonWorld</c>,
    /// <c>utils/vbsp/manifest.cpp:474</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two different tests, and mixing them up would cull the wrong things.
    /// Worldspawn brushes are kept when their BOX intersects a cordon box —
    /// strictly, so a brush merely touching a cordon face is culled. Every
    /// other entity is kept when its ORIGIN POINT is inside a box, inclusively
    /// — so a brush entity whose origin is outside the cordon is dropped even
    /// when all its geometry is inside.
    /// </para>
    /// <para>
    /// With cordoning on and no active cordon, EVERYTHING is culled: the
    /// inner loops never get a chance to clear the remove flag. That is stock's
    /// and is not guarded against.
    /// </para>
    /// <para>
    /// Stock shrinks only worldspawn's brush COUNT and never decrements
    /// <c>nummapbrushes</c> or adjusts other entities' first-brush indices, so
    /// it leaves orphan slots between the blocks that nothing references. This
    /// removes the brushes from the list instead and repairs the indices, which
    /// is the same set of surviving brushes in the same order.
    /// </para>
    /// </remarks>
    public void CordonWorld()
    {
        if (!IsCordoning)
        {
            return;
        }

        MapEntity world = Map.Entities[0];

        for (int n = world.BrushCount - 1; n >= 0; n--)
        {
            MapBrush brush = Map.Brushes[world.FirstBrush + n];

            if (AnyCordonIntersects(brush.Mins, brush.Maxs))
            {
                continue;
            }

            Map.Brushes.RemoveAt(world.FirstBrush + n);
            world.BrushCount--;

            for (int e = 1; e < Map.Entities.Count; e++)
            {
                if (Map.Entities[e].FirstBrush > world.FirstBrush + n)
                {
                    Map.Entities[e].FirstBrush--;
                }
            }
        }

        for (int i = 1; i < Map.Entities.Count; i++)
        {
            MapEntity entity = Map.Entities[i];

            if (ReferenceEquals(entity, CordoningEntity))
            {
                continue;
            }

            if (entity.BrushCount == 0 && entity.Pairs.Count == 0)
            {
                continue;
            }

            if (AnyCordonContains(entity.Origin))
            {
                continue;
            }

            entity.Clear();
        }

        if (CordoningEntity is not null)
        {
            Map.MoveBrushesToWorldGeneral(CordoningEntity);
            CordoningEntity.Clear();
        }
    }

    private static string StripExtension(string path)
    {
        int dot = path.LastIndexOf('.');
        int slash = path.LastIndexOfAny(['/', '\\']);
        return dot > slash ? path[..dot] : path;
    }

    private void ReadMaps(VmfDocument document)
    {
        List<string> subMaps = [];

        foreach (VmfChunk maps in document.GetChunks(MapsChunk))
        {
            foreach (VmfChunk vmf in maps.GetChunks(VmfChunk))
            {
                // "File" is the only key with any effect. "Name", "IsPrimary"
                // and "IsProtected" are parsed and thrown away -- their
                // assignments are commented out in manifest.cpp:40-58.
                subMaps.Add(vmf.GetValue("File") ?? string.Empty);
            }
        }

        SubMaps = subMaps;
    }

    private void CreateSubMapEntities()
    {
        MapEntity world = new();
        world.SetKeyValue("classname", "worldspawn");
        Map.Entities.Add(world);

        foreach (string file in SubMaps)
        {
            MapEntity instance = new();

            // manifest.cpp:318-343, in call order -- and SetKeyValue prepends,
            // so the pair list comes out reversed from this.
            instance.SetKeyValue("angles", "0 0 0");
            instance.SetKeyValue("fixup_style", MapInstanceMerger.NameFixupNone);
            instance.SetKeyValue("classname", FuncInstance.ClassName);
            instance.SetKeyValue("file", file);

            Map.Entities.Add(instance);
        }
    }

    private async Task ReadPrefsAsync(
        VbspContext context,
        VmfDocument document,
        CancellationToken cancellationToken)
    {
        foreach (VmfChunk cordoning in document.GetChunks(CordoningChunk))
        {
            MapEntity entity = new() { FirstBrush = Map.BrushCount };
            Map.Entities.Add(entity);
            CordoningEntity = entity;

            foreach (VmfNode node in cordoning.Children)
            {
                if (node is not VmfChunk child)
                {
                    continue;
                }

                if (string.Equals(child.Name, "cordons", StringComparison.OrdinalIgnoreCase))
                {
                    ReadCordons(child);
                }
                else if (string.Equals(
                    child.Name, MapFileLoader.SolidChunk, StringComparison.OrdinalIgnoreCase))
                {
                    await MapFileLoader
                        .LoadSolidAsync(context, Map, entity, child, 0, 0, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    private void ReadCordons(VmfChunk cordons)
    {
        // "active" here is the MASTER switch; each cordon has its own.
        // ReadKeyValueBool is atoi() > 0 (chunkfile.cpp:636), so the string
        // "true" reads as FALSE -- Hammer writes "1".
        if (cordons.GetValue("active") is { } active)
        {
            IsCordoning = VmfValue.ParseBool(active);
        }

        foreach (VmfChunk cordon in cordons.GetChunks("cordon"))
        {
            List<(Vec3 Mins, Vec3 Maxs)> boxes = [];

            foreach (VmfChunk box in cordon.GetChunks("box"))
            {
                // ReadKeyValuePoint: "(x y z)", PARENTHESISED -- not the
                // bracketed form the manifest.cpp doc comment shows.
                _ = VmfValue.TryParsePoint(box.GetValue("mins"), out Vec3 mins);
                _ = VmfValue.TryParsePoint(box.GetValue("maxs"), out Vec3 maxs);
                boxes.Add((mins, maxs));
            }

            _cordons.Add(new MapCordon(
                cordon.GetValue("name") ?? string.Empty,
                VmfValue.ParseBool(cordon.GetValue("active")),
                boxes));
        }
    }

    private bool AnyCordonIntersects(Vec3 mins, Vec3 maxs)
    {
        foreach (MapCordon cordon in _cordons)
        {
            if (!cordon.Active)
            {
                continue;
            }

            foreach ((Vec3 boxMins, Vec3 boxMaxs) in cordon.Boxes)
            {
                // BoundBox::IsIntersectingBox (boundbox.cpp:141): STRICT, so
                // two boxes that merely touch do not intersect.
                if (boxMins.X >= maxs.X || boxMaxs.X <= mins.X ||
                    boxMins.Y >= maxs.Y || boxMaxs.Y <= mins.Y ||
                    boxMins.Z >= maxs.Z || boxMaxs.Z <= mins.Z)
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private bool AnyCordonContains(Vec3 point)
    {
        foreach (MapCordon cordon in _cordons)
        {
            if (!cordon.Active)
            {
                continue;
            }

            foreach ((Vec3 boxMins, Vec3 boxMaxs) in cordon.Boxes)
            {
                // BoundBox::ContainsPoint (boundbox.cpp:122): INCLUSIVE on
                // both faces, unlike the box test above.
                if (point.X < boxMins.X || point.X > boxMaxs.X ||
                    point.Y < boxMins.Y || point.Y > boxMaxs.Y ||
                    point.Z < boxMins.Z || point.Z > boxMaxs.Z)
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }
}
