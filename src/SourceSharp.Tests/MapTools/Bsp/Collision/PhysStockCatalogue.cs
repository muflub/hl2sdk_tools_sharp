//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;
using SourceSharp.Tests.MapTools.Io;

namespace SourceSharp.Tests.MapTools.Bsp.Collision;

/// <summary>
/// The stock-compiled maps the collision gate compares against, and the
/// stage-isolated input built from each one.
/// </summary>
/// <remarks>
/// <para>
/// <c>PHYS_STOCK_DIR</c> names a directory of <c>&lt;n&gt;.bsp</c> written by
/// stock x64 <c>vbsp.exe</c>; <c>.stockvis</c>/<c>.stockrad</c> copies are
/// ignored. Recipe: the catalogue build, <c>make-catmaps</c>.
/// </para>
/// <para>
/// STAGE ISOLATION: the input is stock's OWN finished lumps -- BRUSHES.
/// BRUSHSIDES, PLANES, NODES, LEAFS, LEAFBRUSHES, MODELS, FACES, TEXINFO.
/// so the planes the cooker sees are bit-identical to the ones stock's cooker
/// saw, and any byte difference is the cooker's, not the port's. Three inputs
/// are not in any lump and are reconstructed, each named where it is used:
/// the surface-property index per texdata (from the texdata names through the
/// content the map's <c>&lt;n&gt;.gameinfo</c> sidecar names, see
/// <see cref="StockProvenance"/>, and the map's own pak), the water volumes (from
/// <c>leafWaterDataID</c> and LEAFWATERDATA), and brush-side visibility (not
/// recoverable: every side is taken as visible).
/// </para>
/// </remarks>
internal static class PhysStockCatalogue
{
    /// <summary>The environment variable naming the directory.</summary>
    public const string DirectoryVariable = "PHYS_STOCK_DIR";

    /// <summary>The directory, or null.</summary>
    public static string? Directory =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } set ? set : null;

    /// <summary>The maps, by name; a single "none" when the directory is absent.</summary>
    public static IReadOnlyList<string> Names
    {
        get
        {
            if (Directory is not { } dir || !System.IO.Directory.Exists(dir))
            {
                return ["none"];
            }

            return
            [
                .. System.IO.Directory.GetFiles(dir, "*.bsp")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => n is not null && !n.Contains('.', StringComparison.Ordinal))
                    .Select(n => n!)
                    .Order(StringComparer.Ordinal),
            ];
        }
    }

    /// <summary>The stock BSP.</summary>
    /// <param name="name">The map.</param>
    /// <returns>It, loaded.</returns>
    public static async Task<BspData> LoadAsync(string name)
    {
        await using FileStream stream = File.OpenRead(Path.Combine(Directory!, name + ".bsp"));
        return await BspFile.LoadAsync(stream);
    }

    /// <summary>The stage-isolated emitter input for one stock BSP.</summary>
    /// <param name="name">The map.</param>
    /// <param name="bsp">Its stock BSP.</param>
    /// <returns>The input.</returns>
    public static async Task<PhysCollisionInput> InputAsync(string name, BspData bsp) =>
        (await InputAndProp2Async(Directory!, name, bsp)).Input;

    /// <summary>The input, and each texdata's <c>$surfaceprop2</c> index (-1 for none).</summary>
    /// <param name="referenceDirectory">
    /// The directory the stock BSP came from, whose <c>&lt;name&gt;.gameinfo</c>
    /// sidecar names the content it was compiled against (<see cref="StockProvenance"/>).
    /// </param>
    /// <param name="name">The map.</param>
    /// <param name="bsp">Its stock BSP.</param>
    /// <returns>Both.</returns>
    public static async Task<(PhysCollisionInput Input, IReadOnlyList<int> SurfaceProp2)> InputAndProp2Async(
        string referenceDirectory, string name, BspData bsp)
    {
        PhysicalFileSystem host = PhysicalFileSystem.AtHostRoot();
        ReadOnlyFileSystem guarded = new(host);

        GameContentMounter.Result mounted = await StockProvenance.MountAsync(
            guarded, host.ToVirtualPath(referenceDirectory), name);

        await using ContentFileSystem game = mounted.Content;
        await using ArchiveContentMount pak = ArchiveContentMount.Mount(
            PakArchive.Open(bsp[BspLump.PakFile].Data, name + ".bsp/pak"));
        await using ContentFileSystem all = new([pak, .. game.Mounts], ownsMounts: false);

        SurfacePropertyTable props = await SurfacePropertyTable.LoadAsync(all);

        List<int> surfaceProperties = [];
        List<int> prop2 = [];
        foreach (string material in TexDataNames(bsp))
        {
            MaterialFacts facts = await MaterialFactsReader.ReadAsync(material, all);
            surfaceProperties.Add(props.ResolveMaterial(facts.SurfaceProp));
            prop2.Add(props.ResolveMaterial(facts.SurfaceProp2));
        }

        // LEAFS version 0 (the ambient cube inline, 56 bytes) on older maps.
        DLeaf[] leafs = bsp[BspLump.Leafs].Version == 0
            ?
            [
                .. BspStructView.As<DLeafVersion0>(bsp[BspLump.Leafs]).ToArray().Select(l => new DLeaf
                {
                    Contents = l.Contents, Cluster = l.Cluster, AreaFlags = l.AreaFlags, Mins = l.Mins, Maxs = l.Maxs,
                    FirstLeafFace = l.FirstLeafFace, NumLeafFaces = l.NumLeafFaces,
                    FirstLeafBrush = l.FirstLeafBrush, NumLeafBrushes = l.NumLeafBrushes, LeafWaterDataId = l.LeafWaterDataId,
                }),
            ]
            : [.. BspStructView.As<DLeaf>(bsp[BspLump.Leafs])];
        DLeafWaterData[] waterData = [.. BspStructView.As<DLeafWaterData>(bsp[BspLump.LeafWaterData])];

        return (new PhysCollisionInput
        {
            Planes = [.. BspStructView.As<DPlane>(bsp[BspLump.Planes])],
            Brushes = [.. BspStructView.As<DBrush>(bsp[BspLump.Brushes])],
            BrushSides = [.. BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides])],
            Nodes = [.. BspStructView.As<DNode>(bsp[BspLump.Nodes])],
            Leafs = leafs,
            LeafBrushes = [.. BspStructView.As<ushort>(bsp[BspLump.LeafBrushes])],
            Models = [.. BspStructView.As<DModel>(bsp[BspLump.Models])],
            Faces = [.. BspStructView.As<DFace>(bsp[BspLump.Faces])],
            TexInfos = [.. BspStructView.As<TexInfo>(bsp[BspLump.TexInfo])],
            SurfaceProperties = surfaceProperties,
            SurfaceProps = props,
            WaterModels = WaterModelsFrom(leafs, waterData),
            Compliance = ComplianceOptions.Stock,
        }, prop2);
    }

    /// <summary>
    /// The water volumes, reconstructed: one per <c>leafWaterDataID</c> in use,
    /// in index order (the order <c>FindOrCreateLeafWaterData</c> handed them
    /// out, which is the order stock's sorted list visited them), with the
    /// surface plane taken as up at <c>surfaceZ</c>.
    /// </summary>
    /// <param name="leafs">LUMP_LEAFS.</param>
    /// <param name="waterData">LUMP_LEAFWATERDATA.</param>
    /// <returns>The models.</returns>
    public static IReadOnlyList<WaterModel> WaterModelsFrom(IReadOnlyList<DLeaf> leafs, IReadOnlyList<DLeafWaterData> waterData)
    {
        List<(int Last, WaterModel Model)> models = [];
        for (int fog = 0; fog < waterData.Count; fog++)
        {
            List<int> members = [];
            for (int i = 0; i < leafs.Count; i++)
            {
                if (leafs[i].LeafWaterDataId == fog)
                {
                    members.Add(i);
                }
            }

            // One fog volume can hold several disconnected pools
            // (FindOrCreateLeafWaterData dedups equal surfaces), and stock
            // makes one fluid per CONNECTED group. Leaves whose boxes touch are
            // taken as connected: the portals that decided it are not in the BSP.
            foreach (List<int> pool in Components(leafs, members))
            {
                // A volume with no surface keeps BuildWaterLeaf's MAX_COORD_INTEGER;
                // its fluid plane then comes from CollideGetExtent, as in stock.
                bool hasSurface = waterData[fog].SurfaceZ != CollisionContents.MaxCoordInteger;
                models.Add((pool.Max(), new WaterModel(0, leafs[pool[0]].Contents, hasSurface, new(0, 0, 1), waterData[fog].SurfaceZ, fog, pool, waterData[fog].SurfaceTexInfoId)));
            }
        }

        // InsertSortWaterLeaf puts an equal leaf BEFORE the ones already in the
        // list(IsLowerLeaf), so the last leaf in tree order leads.
        return [.. models.OrderBy(m => m.Model.FogVolumeIndex).ThenByDescending(m => m.Last).Select(m => m.Model)];
    }

    private static List<List<int>> Components(IReadOnlyList<DLeaf> leafs, List<int> members)
    {
        List<List<int>> groups = [];
        HashSet<int> seen = [];
        foreach (int start in members)
        {
            if (!seen.Add(start))
            {
                continue;
            }

            List<int> group = [start];
            for (int k = 0; k < group.Count; k++)
            {
                foreach (int other in members)
                {
                    if (!seen.Contains(other) && Touch(leafs[group[k]], leafs[other]))
                    {
                        seen.Add(other);
                        group.Add(other);
                    }
                }
            }

            group.Sort();
            groups.Add(group);
        }

        return groups;
    }

    private static bool Touch(DLeaf a, DLeaf b)
    {
        for (int i = 0; i < 3; i++)
        {
            if (a.Mins[i] > b.Maxs[i] || b.Mins[i] > a.Maxs[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The texdata names, in lump order.</summary>
    /// <param name="bsp">The BSP.</param>
    /// <returns>The names.</returns>
    public static IReadOnlyList<string> TexDataNames(BspData bsp)
    {
        ReadOnlySpan<DTexData> texData = BspStructView.As<DTexData>(bsp[BspLump.TexData]);
        ReadOnlySpan<int> table = BspStructView.As<int>(bsp[BspLump.TexDataStringTable]);
        ReadOnlySpan<byte> strings = bsp[BspLump.TexDataStringData].Data.Span;

        List<string> names = [];
        foreach (DTexData entry in texData)
        {
            int start = table[entry.NameStringTableId];
            int end = strings[start..].IndexOf((byte)0);
            names.Add(System.Text.Encoding.Latin1.GetString(strings.Slice(start, end)));
        }

        return names;
    }
}
