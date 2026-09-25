using System.Globalization;

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Bsp.Collision;

/// <summary>
/// Where the water pass creates the per-depth water texinfo:
/// <c>FindOrCreateWaterTexInfo</c> (<c>ivp.cpp:825</c>), which clones the
/// surface's texdata under <see cref="WaterVolumeBuilder.WaterTextureName"/>,
/// finds or creates the texinfo, and emits the patched <c>.vmt</c> with
/// <c>$waterdepth</c> into the pak. Those tables belong to the texture and
/// pak stages, so this pass calls out to them.
/// </summary>
public interface IWaterTexInfoSink
{
    /// <summary>The texinfo for the water surface at a depth.</summary>
    /// <param name="baseTexInfo">The water surface's texinfo.</param>
    /// <param name="depth">The water's depth (truncated to int by stock's name).</param>
    /// <returns>The depth texinfo's index.</returns>
    int FindOrCreateWaterTexInfo(int baseTexInfo, float depth);
}

/// <summary>
/// <c>EmitWaterVolumesForBSP</c> (<c>ivp.cpp:1106</c>): while a model's tree
/// and portals still exist, find its connected water volumes, give each a
/// <c>dleafwaterdata_t</c>, and record them for
/// <see cref="PhysCollisionEmitter"/>.
/// </summary>
/// <remarks>
/// <para>
/// CALL POSITION: stock calls it from <c>WriteBSP</c> (<c>writebsp.cpp:937</c>)
/// once per model, after the model's leaves have their disk ids and before
/// the tree is freed; then <c>WriteFogVolumeIDs</c> (<c>ivp.cpp:875</c>) sets
/// each of that model's faces' <c>surfaceFogVolumeID</c> -- which needs the
/// face-to-node map of the face stage and is left to it (the leaf data it
/// reads is <see cref="LeafWaterData"/>).
/// </para>
/// <para>
/// One builder per compile: the leaf-water-data table and the water-model
/// list accumulate across models exactly as stock's globals do.
/// </para>
/// </remarks>
public sealed class WaterVolumeBuilder
{
    private readonly IReadOnlyList<Plane> _planes;
    private readonly WindingArena _arena;
    private readonly Func<IBspNode, int> _diskId;
    private readonly Func<IBspNode, int, int> _firstWaterTexInfo;
    private readonly IWaterTexInfoSink? _sink;
    private readonly ComplianceOptions _compliance;

    /// <summary>Creates the builder.</summary>
    /// <param name="planes">The map's plane table (<c>g_MainMap-&gt;mapplanes</c>).</param>
    /// <param name="arena">The arena the portal windings live in.</param>
    /// <param name="diskId">A leaf's index in LUMP_LEAFS (<c>node_t::diskId</c>), -1 if none.</param>
    /// <param name="firstWaterTexInfo">
    /// <c>FirstWaterTexinfo</c> (<c>ivp.cpp:1069</c>): the first original side
    /// of the leaf's brush list with the contents, for a volume with no
    /// surface (a leaked map).
    /// </param>
    /// <param name="sink">Where depth texinfos are created, or null to skip that.</param>
    /// <param name="compliance">Which stock defects to reproduce; null is correct.</param>
    public WaterVolumeBuilder(
        IReadOnlyList<Plane> planes,
        WindingArena arena,
        Func<IBspNode, int> diskId,
        Func<IBspNode, int, int> firstWaterTexInfo,
        IWaterTexInfoSink? sink,
        ComplianceOptions? compliance = null)
    {
        _compliance = compliance ?? ComplianceOptions.Correct;
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(diskId);
        ArgumentNullException.ThrowIfNull(firstWaterTexInfo);
        _planes = planes;
        _arena = arena;
        _diskId = diskId;
        _firstWaterTexInfo = firstWaterTexInfo;
        _sink = sink;
    }

    /// <summary>LUMP_LEAFWATERDATA so far (<c>dleafwaterdata</c>).</summary>
    public List<DLeafWaterData> LeafWaterData { get; } = [];

    /// <summary>The water volumes so far (<c>g_WaterModels</c>).</summary>
    public List<WaterModel> WaterModels { get; } = [];

    /// <summary>Each water model's depth texinfo, parallel to <see cref="WaterModels"/>.</summary>
    public List<int> DepthTexInfos { get; } = [];

    /// <summary>
    /// <c>GetWaterTextureName</c>, <c>ivp.cpp:785</c>:
    /// <c>maps/&lt;map&gt;/&lt;material&gt;_depth_&lt;depth&gt;</c>, lower-cased.
    /// </summary>
    /// <param name="mapName">The map's base name.</param>
    /// <param name="material">The water material.</param>
    /// <param name="depth">The depth, already truncated.</param>
    /// <returns>The mangled material name.</returns>
    public static string WaterTextureName(string mapName, string material, int depth) =>
        string.Create(CultureInfo.InvariantCulture, $"maps/{mapName}/{material}_depth_{depth}").ToLowerInvariant();

    /// <summary>Finds one model's water volumes.</summary>
    /// <param name="modelIndex">The model being written (<c>nummodels</c>).</param>
    /// <param name="headNode">The model's tree.</param>
    public void EmitWaterVolumesForModel(int modelIndex, IBspNode headNode)
    {
        ArgumentNullException.ThrowIfNull(headNode);

        List<IBspNode> anyWater = [];
        EnumLeaves(anyWater, headNode, CollisionContents.MaskWater);

        List<WaterLeaf> list = [];
        foreach (IBspNode leaf in anyWater)
        {
            InsertSortWaterLeaf(list, BuildWaterLeaf(leaf));
        }

        HashSet<int> visited = [];
        List<IBspNode> area = [];
        for (int i = 0; i < list.Count; i++)
        {
            FloodFindConnectedWaterVolumes(area, list[i].Node, list[i], visited);
            if (area.Count == 0)
            {
                continue;
            }

            WaterLeaf data = list[i];
            float waterDepth = data.SurfaceDist - data.MinZ;
            if (data.SurfaceTexInfo < 0)
            {
                // "the map has probably leaked in this case, but output something anyway"
                data.SurfaceTexInfo = _firstWaterTexInfo(data.Node, data.Node.Contents);
            }

            DepthTexInfos.Add(_sink?.FindOrCreateWaterTexInfo(data.SurfaceTexInfo, waterDepth) ?? -1);
            int fog = FindOrCreateLeafWaterData(data.SurfaceDist, data.MinZ, data.SurfaceTexInfo);

            WaterModels.Add(new WaterModel(
                modelIndex,
                data.Node.Contents,
                data.HasSurface,
                data.SurfaceNormal,
                data.SurfaceDist,
                fog,
                [.. area.Select(_diskId)],
                data.SurfaceTexInfo));
            area.Clear();
        }
    }

    /// <summary><c>FindOrCreateLeafWaterData</c>, <c>ivp.cpp:924</c>: exact float match.</summary>
    private int FindOrCreateLeafWaterData(float surfaceZ, float minZ, int surfaceTexInfo)
    {
        for (int i = 0; i < LeafWaterData.Count; i++)
        {
            DLeafWaterData d = LeafWaterData[i];
            if (d.SurfaceZ == surfaceZ && d.MinZ == minZ && d.SurfaceTexInfoId == surfaceTexInfo)
            {
                return i;
            }
        }

        LeafWaterData.Add(new DLeafWaterData { SurfaceZ = surfaceZ, MinZ = minZ, SurfaceTexInfoId = (short)surfaceTexInfo });
        return LeafWaterData.Count - 1;
    }

    /// <summary><c>EnumLeaves_r</c>, <c>ivp.cpp:947</c>: front before back.</summary>
    private static void EnumLeaves(List<IBspNode> list, IBspNode node, int contentsMask)
    {
        if (!node.IsLeaf())
        {
            EnumLeaves(list, node.Front!, contentsMask);
            EnumLeaves(list, node.Back!, contentsMask);
            return;
        }

        if ((node.Contents & contentsMask) != 0)
        {
            list.Add(node);
        }
    }

    /// <summary><c>BuildWaterLeaf</c>, <c>ivp.cpp:966</c>.</summary>
    private WaterLeaf BuildWaterLeaf(IBspNode leaf)
    {
        WaterLeaf result = new(leaf)
        {
            SurfaceDist = CollisionContents.MaxCoordInteger,
            SurfaceNormal = new Vec3(0f, 0f, 1f),
            MinZ = CollisionContents.MaxCoordInteger,
        };

        for (Portal? p = leaf.Portals; p is not null; p = p.NextAt(p.SideOf(leaf)))
        {
            // "not visible, can't be the portals we're looking for..."
            if (p.Side is null)
            {
                continue;
            }

            IBspNode opposite = p.NodeAt(1 - p.SideOf(leaf))!;
            if ((opposite.Contents & CollisionContents.MaskWater) != 0 || (opposite.Contents & CollisionContents.MaskSolid) != 0)
            {
                continue;
            }

            Plane plane = _planes[p.Side.PlaneNumber];
            if (result.HasSurface)
            {
                // "Sort to find the most upward facing normal (skips sides)"
                if (result.SurfaceNormal.Z > plane.Normal.Z)
                {
                    continue;
                }

                if (result.SurfaceNormal.Z == plane.Normal.Z && result.SurfaceDist >= plane.Dist)
                {
                    continue;
                }
            }

            // "water surface needs to point at least somewhat up"
            if (plane.Normal.Z <= 0)
            {
                continue;
            }

            result.SurfaceDist = plane.Dist;
            result.SurfaceNormal = plane.Normal;
            result.HasSurface = true;
            result.SurfaceTexInfo = p.Side.TexInfo;
        }

        return result;
    }

    /// <summary>
    /// <c>IsLowerLeaf</c>, <c>ivp.cpp:709</c>: whether a new leaf sorts before
    /// the current one. The one pointing most up goes first, and a leaf with a
    /// surface before one without.
    /// </summary>
    /// <param name="newHasSurface">The new leaf has a surface.</param>
    /// <param name="newNormalZ">Its surface normal's z.</param>
    /// <param name="newDist">Its surface distance.</param>
    /// <param name="currentHasSurface">The listed leaf has a surface.</param>
    /// <param name="currentNormalZ">Its surface normal's z.</param>
    /// <param name="currentDist">Its surface distance.</param>
    /// <param name="compliance">
    /// Under <see cref="StockQuirk.WaterLeafSortTie"/> the near-equal branch
    /// is stock's dead one (true either way, so equal leaves end up in REVERSE
    /// discovery order); corrected, near-equal leaves sort lower distance first.
    /// </param>
    /// <returns>True when the new leaf goes first.</returns>
    public static bool IsLowerLeaf(
        bool newHasSurface,
        float newNormalZ,
        float newDist,
        bool currentHasSurface,
        float currentNormalZ,
        float currentDist,
        ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(compliance);

        if (newHasSurface && currentHasSurface)
        {
            // "the one with the upmost pointing z goes first"
            if (currentNormalZ > newNormalZ)
            {
                return false;
            }

            if (Math.Abs(currentNormalZ - newNormalZ) < 0.01)
            {
                if (newDist < currentDist)
                {
                    return true;
                }

                if (!compliance.Emulates(StockQuirk.WaterLeafSortTie))
                {
                    return false;
                }
            }

            return true;
        }

        // "the leaf with a surface always goes first"
        return newHasSurface;
    }

    /// <summary><c>InsertSortWaterLeaf</c>, <c>ivp.cpp:1017</c>.</summary>
    private void InsertSortWaterLeaf(List<WaterLeaf> list, WaterLeaf leaf)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (IsLowerLeaf(
                leaf.HasSurface, leaf.SurfaceNormal.Z, leaf.SurfaceDist,
                list[i].HasSurface, list[i].SurfaceNormal.Z, list[i].SurfaceDist,
                _compliance))
            {
                list.Insert(i, leaf);
                return;
            }
        }

        list.Add(leaf);
    }

    /// <summary><c>PortalCrossesWater</c>, <c>ivp.cpp:911</c>.</summary>
    private bool PortalCrossesWater(WaterLeaf baseLeaf, Portal portal)
    {
        if (!baseLeaf.HasSurface)
        {
            return false;
        }

        PlaneSide side = _arena.OnPlaneSide(portal.Winding, baseLeaf.SurfaceNormal, baseLeaf.SurfaceDist);
        return side is PlaneSide.Cross or PlaneSide.Front;
    }

    /// <summary>
    /// <c>Flood_FindConnectedWaterVolumes_r</c>, <c>ivp.cpp:1038</c>, with an
    /// explicit stack. The set of leaves reached does not depend on the visit
    /// order: a leaf is refused on its OWN portals, never on the path taken.
    /// </summary>
    private void FloodFindConnectedWaterVolumes(List<IBspNode> list, IBspNode start, WaterLeaf baseLeaf, HashSet<int> visited)
    {
        int waterMask = baseLeaf.Node.Contents & CollisionContents.MaskWater;
        Stack<IBspNode> pending = new();
        pending.Push(start);

        while (pending.Count > 0)
        {
            IBspNode leaf = pending.Pop();
            int disk = _diskId(leaf);
            if (disk < 0 || visited.Contains(disk) || (leaf.Contents & waterMask) == 0)
            {
                continue;
            }

            bool crosses = false;
            for (Portal? p = leaf.Portals; p is not null; p = p.NextAt(p.SideOf(leaf)))
            {
                if (PortalCrossesWater(baseLeaf, p))
                {
                    crosses = true;
                    break;
                }
            }

            if (crosses)
            {
                continue;
            }

            visited.Add(disk);
            list.Add(leaf);
            baseLeaf.MinZ = MathF.Min(leaf.Mins.Z, baseLeaf.MinZ);

            List<IBspNode> next = [];
            for (Portal? p = leaf.Portals; p is not null; p = p.NextAt(p.SideOf(leaf)))
            {
                next.Add(p.NodeAt(1 - p.SideOf(leaf))!);
            }

            // Reverse, so the stack pops them in the recursion's order.
            for (int i = next.Count - 1; i >= 0; i--)
            {
                pending.Push(next[i]);
            }
        }
    }

    /// <summary><c>waterleaf_t</c>, <c>ivp.cpp:693</c>; a class, because the flood updates <c>minZ</c> in place.</summary>
    private sealed class WaterLeaf(IBspNode node)
    {
        public IBspNode Node { get; } = node;

        public Vec3 SurfaceNormal { get; set; }

        public float SurfaceDist { get; set; }

        public float MinZ { get; set; }

        public bool HasSurface { get; set; }

        public int SurfaceTexInfo { get; set; } = -1;
    }
}
