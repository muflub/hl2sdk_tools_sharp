//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The brush fold on hand-made brushes: which pairs merge, which do not and
/// why, what the merged box is made of, and the order it all comes out in.
/// </summary>
public sealed class LinkBrushFoldTests
{
    private const int Solid = 1;
    private const int Wall = 0; // texdata of the default material
    private const int Other = 1;

    // ---- which boxes merge --------------------------------------------------

    /// <summary>
    /// Two boxes with identical y and z extents that touch on x merge into
    /// their union: one brush, six sides, no bevel, no displacement, the map
    /// sending both to it.
    /// </summary>
    [Fact]
    public void TwoBoxesTouchingOnOneAxisWithTheOthersIdenticalMerge()
    {
        BoxMap map = new();
        map.Box(new(0, 0, 0), new(16, 64, 128));
        map.Box(new(16, 0, 0), new(40, 64, 128));
        BrushFoldResult fold = map.Fold();

        DBrush merged = Assert.Single(fold.Brushes);
        Assert.Equal(6, merged.NumSides);
        Assert.Equal(Solid, merged.Contents);
        Assert.All(fold.Sides, s => Assert.Equal((0, 0), ((int)s.Bevel, (int)s.DispInfo)));
        Assert.Equal((new Vec3(0, 0, 0), new Vec3(40, 64, 128)), map.BoxOf(fold, 0));
        Assert.Equal([0, 0], fold.Map);
        Assert.Equal([0, 1], Assert.Single(fold.Groups));
        Assert.Equal(1, fold.Removed);
    }

    /// <summary>The same along y and along z; the order of the two brushes does not matter.</summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void BoxesMergeAlongEveryAxisInEitherOrder(int axis, bool reversed)
    {
        // Split the box (0,0,0)..(40,30,100) along the axis (neither half
        // is a slab, which folds only with slabs).
        Vec3 lo = new(0, 0, 0), hi = new(40, 30, 100);
        Vec3 cutHi = axis == 1 ? new(40, 16, 100) : new(40, 30, 50);
        Vec3 cutLo = axis == 1 ? new(0, 16, 0) : new(0, 0, 50);
        BoxMap map = new();
        if (reversed)
        {
            map.Box(cutLo, hi);
            map.Box(lo, cutHi);
        }
        else
        {
            map.Box(lo, cutHi);
            map.Box(cutLo, hi);
        }

        BrushFoldResult fold = map.Fold();
        Assert.Single(fold.Brushes);
        Assert.Equal((lo, hi), map.BoxOf(fold, 0));
    }

    /// <summary>
    /// Boxes whose extents differ on a second axis stay apart: their union is
    /// not a box. So do boxes with a gap between them, or that overlap.
    /// </summary>
    [Theory]
    [InlineData("extent", 16, 0, 0, 40, 60, 128)]
    [InlineData("gap", 17, 0, 0, 40, 64, 128)]
    [InlineData("overlap", 15, 0, 0, 40, 64, 128)]
    [InlineData("height", 16, 0, 0, 40, 64, 127)]
    public void BoxesWhoseUnionIsNotABoxStayApart(string why, float x, float y, float z, float hx, float hy, float hz)
    {
        BoxMap map = new();
        map.Box(new(0, 0, 0), new(16, 64, 128));
        map.Box(new(x, y, z), new(hx, hy, hz));
        Assert.True(map.Fold().Brushes.Length == 2, why);
    }

    /// <summary>Boxes of different contents stay apart, even solid against solid-and-detail.</summary>
    [Fact]
    public void BoxesOfDifferentContentsStayApart()
    {
        BoxMap map = new();
        map.Box(new(0, 0, 0), new(16, 64, 128), contents: Solid);
        map.Box(new(16, 0, 0), new(40, 64, 128), contents: Solid | 0x8000000);
        Assert.Equal(2, map.Fold().Brushes.Length);
    }

    /// <summary>
    /// A side that would coalesce (one around the axis of the merge) with a
    /// different material, or the same material with different surface
    /// flags, keeps the boxes apart: a trace there would report another
    /// surface.
    /// </summary>
    [Theory]
    [InlineData(2, Other, 0)] // -y side: material
    [InlineData(5, Wall, 0x80)] // +z side: flags
    [InlineData(3, Other, 0x80)] // +y side: both
    public void ACoalescingSideWithAnotherSurfaceKeepsTheBoxesApart(int slot, int texData, int flags)
    {
        BoxMap map = new();
        map.Box(new(0, 0, 0), new(16, 64, 128));
        map.Box(new(16, 0, 0), new(40, 64, 128), surface: s => s == slot ? map.Info(texData, flags) : map.Info(Wall, 0));
        Assert.Equal(2, map.Fold().Brushes.Length);
    }

    /// <summary>
    /// The sides that do not coalesce may differ: the two that meet inside
    /// the new box are dropped, and each end cap is one box's own.
    /// </summary>
    [Fact]
    public void TheSeamAndTheCapsMayHaveAnySurface()
    {
        BoxMap map = new();
        int capA = map.Info(Other, 0x80, offset: 1), seamA = map.Info(Other, 0x4, offset: 2);
        int seamB = map.Info(Wall, 0x200, offset: 3), capB = map.Info(Other, 0x400, offset: 4);
        map.Box(new(0, 0, 0), new(16, 64, 128), surface: s => s switch { 0 => capA, 1 => seamA, _ => map.Info(Wall, 0) });
        map.Box(new(16, 0, 0), new(40, 64, 128), surface: s => s switch { 0 => seamB, 1 => capB, _ => map.Info(Wall, 0) });
        BrushFoldResult fold = map.Fold();
        Assert.Single(fold.Brushes);
        Assert.Equal(capA, map.SideAt(fold, 0, slot: 0).TexInfo);
        Assert.Equal(capB, map.SideAt(fold, 0, slot: 1).TexInfo);
        Assert.DoesNotContain(fold.Sides, s => s.TexInfo == seamA || s.TexInfo == seamB);
    }

    /// <summary>
    /// A brush that is not exactly an axial box never merges: a seventh side,
    /// a bevel, a displacement, a plane that is not axial, two sides on one
    /// axial plane, or five sides.
    /// </summary>
    [Theory]
    [InlineData("seven sides")]
    [InlineData("bevel")]
    [InlineData("displacement")]
    [InlineData("slanted")]
    [InlineData("repeated slot")]
    [InlineData("five sides")]
    public void ABrushThatIsNotABoxNeverMerges(string how)
    {
        BoxMap map = new();
        map.Box(new(0, 0, 0), new(16, 64, 128));
        map.Box(new(16, 0, 0), new(40, 64, 128));
        map.Spoil(1, how);
        BrushFoldResult fold = map.Fold();
        Assert.Equal(2, fold.Brushes.Length);
        Assert.Equal([0, 1], fold.Map);
    }

    /// <summary>A brush the caller marks as not foldable (an entity's) never merges.</summary>
    [Fact]
    public void AnEntityBrushNeverMerges()
    {
        BoxMap map = new();
        map.Box(new(0, 0, 0), new(16, 64, 128));
        map.Box(new(16, 0, 0), new(40, 64, 128));
        map.Box(new(40, 0, 0), new(80, 64, 128));
        BrushFoldResult fold = map.Fold(foldable: [true, true, false]);
        Assert.Equal(2, fold.Brushes.Length);
        Assert.Equal([0, 0, 1], fold.Map);
        Assert.Equal(map.Brushes[2].Contents, fold.Brushes[1].Contents);
        Assert.Equal(map.SidesOf(2), fold.Sides.Skip(fold.Brushes[1].FirstSide).Take(6));
    }

    // ---- what the merged box is made of --------------------------------------

    /// <summary>
    /// A coalesced side takes the texinfo of the constituent first in brush
    /// order, even when that one is not first along the axis; the merged
    /// box's sides are in that constituent's side order, on its planes.
    /// </summary>
    [Fact]
    public void ACoalescedSideTakesTheFirstBrushsTexinfoAndSideOrder()
    {
        BoxMap map = new();
        int[] order = [5, 4, 3, 2, 1, 0];
        int first = map.Info(Wall, 0, offset: 7), second = map.Info(Wall, 0, offset: 9);
        map.Box(new(16, 0, 0), new(40, 64, 128), surface: _ => first, order: order); // brush 0, high along x
        map.Box(new(0, 0, 0), new(16, 64, 128), surface: _ => second); // brush 1, low along x
        BrushFoldResult fold = map.Fold();
        Assert.Single(fold.Brushes);
        Assert.Equal(order, map.SlotsOf(fold, 0));
        for (int slot = 2; slot < 6; slot++)
        {
            Assert.Equal(first, map.SideAt(fold, 0, slot).TexInfo);
        }

        Assert.Equal(second, map.SideAt(fold, 0, slot: 0).TexInfo); // low cap: brush 1's
        Assert.Equal(first, map.SideAt(fold, 0, slot: 1).TexInfo); // high cap: brush 0's
        Assert.All(fold.Sides, s => Assert.Contains(s.PlaneNum, map.Sides.Select(o => o.PlaneNum)));
    }

    /// <summary>
    /// A chain merges whole: four boxes in a row are one; a gap splits it
    /// into two chains; a box that fits neither stays alone.
    /// </summary>
    [Fact]
    public void ChainsAreMaximalAndStopAtAGap()
    {
        BoxMap map = new();
        for (int i = 0; i < 4; i++)
        {
            map.Box(new(i * 10, 0, 0), new((i + 1) * 10, 64, 128));
        }

        map.Box(new(50, 0, 0), new(60, 64, 128));
        map.Box(new(60, 0, 0), new(70, 64, 128));
        map.Box(new(70, 0, 0), new(80, 64, 100));
        BrushFoldResult fold = map.Fold();
        Assert.Equal(3, fold.Brushes.Length);
        Assert.Equal([0, 0, 0, 0, 1, 1, 2], fold.Map);
        Assert.Equal((new Vec3(0, 0, 0), new Vec3(40, 64, 128)), map.BoxOf(fold, 0));
        Assert.Equal((new Vec3(50, 0, 0), new Vec3(70, 64, 128)), map.BoxOf(fold, 1));
    }

    /// <summary>
    /// Floor tiles become rows along x and then one sheet along y; a missing
    /// tile leaves rows that differ, and they stay rows.
    /// </summary>
    [Fact]
    public void FloorTilesBecomeRowsThenASheet()
    {
        BoxMap full = new();
        BoxMap holed = new();
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                full.Box(new(x * 256, y * 256, 0), new((x + 1) * 256, (y + 1) * 256, 16));
                if ((x, y) != (3, 1))
                {
                    holed.Box(new(x * 256, y * 256, 0), new((x + 1) * 256, (y + 1) * 256, 16));
                }
            }
        }

        BrushFoldResult sheet = full.Fold();
        Assert.Single(sheet.Brushes);
        Assert.Equal((new Vec3(0, 0, 0), new Vec3(1024, 768, 16)), full.BoxOf(sheet, 0));

        // Rows 0 and 2 are 4 wide, row 1 is 3 wide: no two neighbouring
        // rows are identical, so three rows.
        BrushFoldResult rows = holed.Fold();
        Assert.Equal(3, rows.Brushes.Length);
        Assert.Equal((new Vec3(0, 256, 0), new Vec3(768, 512, 16)), holed.BoxOf(rows, 1));
    }

    /// <summary>
    /// Floors fold only with floors: a slab and an upright piece that could
    /// merge (identical extents on two axes, touching) are left apart,
    /// because the slabs are done first and on their own.
    /// </summary>
    [Fact]
    public void SlabsFoldOnlyWithSlabs()
    {
        BoxMap map = new();
        map.Box(new(0, 0, 0), new(64, 64, 16)); // slab
        map.Box(new(0, 0, 16), new(64, 64, 128)); // upright box on it
        Assert.Equal(2, map.Fold().Brushes.Length);
    }

    /// <summary>
    /// Walls back to back merge across their thin axis first, and the pairs
    /// then merge in line; the result is the same as the brute union.
    /// </summary>
    [Fact]
    public void WallsMergeBackToBackThenInLine()
    {
        BoxMap map = new();

        // Two rooms' walls against x = 256, each in two pieces along y.
        map.Box(new(240, 16, 16), new(256, 128, 240));
        map.Box(new(256, 16, 16), new(272, 128, 240));
        map.Box(new(240, 128, 16), new(256, 240, 240));
        map.Box(new(256, 128, 16), new(272, 240, 240));
        BrushFoldResult fold = map.Fold();
        Assert.Single(fold.Brushes);
        Assert.Equal((new Vec3(240, 16, 16), new Vec3(272, 240, 240)), map.BoxOf(fold, 0));
    }

    // ---- order ---------------------------------------------------------------

    /// <summary>
    /// The output is in the order of each brush's first constituent; a brush
    /// that merged with nothing (a box or not) is copied with its sides as
    /// it was.
    /// </summary>
    [Fact]
    public void TheOutputIsInFirstConstituentOrderAndTheRestIsCopied()
    {
        BoxMap map = new();
        map.Box(new(500, 0, 0), new(510, 10, 10)); // 0: alone
        map.Box(new(16, 0, 0), new(40, 64, 128)); // 1: with 3
        map.Box(new(0, 0, 0), new(8, 8, 8)); // 2: spoiled
        map.Box(new(0, 0, 0), new(16, 64, 128)); // 3
        map.Spoil(2, "seven sides");
        BrushFoldResult fold = map.Fold();
        Assert.Equal([0, 1, 2, 1], fold.Map);
        Assert.Equal(map.SidesOf(0), fold.Sides.Skip(fold.Brushes[0].FirstSide).Take(6));
        Assert.Equal(map.SidesOf(2), fold.Sides.Skip(fold.Brushes[2].FirstSide).Take(7));
        Assert.Equal([1, 3], fold.Groups[1]);

        int next = 0;
        foreach (DBrush brush in fold.Brushes)
        {
            Assert.Equal(next, brush.FirstSide);
            next += brush.NumSides;
        }

        Assert.Equal(next, fold.Sides.Length);
    }

    /// <summary>The same brushes always fold to the same result.</summary>
    [Fact]
    public void TheFoldIsAPureFunctionOfItsInput()
    {
        BoxMap map = new();
        Random random = new(5);
        for (int i = 0; i < 300; i++)
        {
            int x = random.Next(8) * 16, y = random.Next(8) * 16, z = random.Next(4) * 16;
            map.Box(new(x, y, z), new(x + (16 * (1 + random.Next(2))), y + 16, z + 16), contents: 1 + random.Next(2));
        }

        BrushFoldResult a = map.Fold(), b = map.Fold();
        Assert.Equal(a.Map, b.Map);
        Assert.Equal(a.Brushes, b.Brushes);
        Assert.Equal(a.Sides, b.Sides);
        Assert.True(a.Removed > 0, "nothing folded");
    }

    // ---- leaf brush runs -------------------------------------------------------

    /// <summary>
    /// Each leaf's run is renumbered through the map with repeats dropped
    /// (two constituents of one box in one leaf are one brush there), a run
    /// several leaves share is written once, and a leaf without brushes is
    /// left alone.
    /// </summary>
    [Fact]
    public void LeafBrushRunsAreRenumberedOnceAndDeduplicated()
    {
        List<DLeaf> leafs =
        [
            new() { FirstLeafBrush = 0, NumLeafBrushes = 3 },
            new() { FirstLeafBrush = 0, NumLeafBrushes = 0 },
            new() { FirstLeafBrush = 3, NumLeafBrushes = 2 },
            new() { FirstLeafBrush = 0, NumLeafBrushes = 3 }, // shares leaf 0's run
            new() { FirstLeafBrush = 5, NumLeafBrushes = 1 },
        ];
        List<int> leafBrushes = [4, 1, 2, 3, 0, 5];
        int[] map = [0, 1, 1, 2, 0, 3];
        LevelLinker.RemapLeafBrushes(leafs, leafBrushes, map);

        Assert.Equal([0, 1, 2, 0, 3], leafBrushes);
        Assert.Equal((0, 2), (leafs[0].FirstLeafBrush, leafs[0].NumLeafBrushes));
        Assert.Equal((0, 0), (leafs[1].FirstLeafBrush, leafs[1].NumLeafBrushes));
        Assert.Equal((2, 2), (leafs[2].FirstLeafBrush, leafs[2].NumLeafBrushes));
        Assert.Equal((0, 2), (leafs[3].FirstLeafBrush, leafs[3].NumLeafBrushes));
        Assert.Equal((4, 1), (leafs[4].FirstLeafBrush, leafs[4].NumLeafBrushes));
    }

    /// <summary>Hand-made brushes on a shared plane and texinfo table.</summary>
    private sealed class BoxMap
    {
        private readonly Dictionary<(int Slot, float At), ushort> _planeOf = [];
        private readonly Dictionary<(int, int, float), int> _infoOf = [];

        public List<DPlane> Planes { get; } = [];

        public List<TexInfo> Infos { get; } = [];

        public List<DBrush> Brushes { get; } = [];

        public List<DBrushSide> Sides { get; } = [];

        /// <summary>A texinfo of a material and flags; <paramref name="offset"/> makes otherwise equal ones distinct.</summary>
        public int Info(int texData, int flags, float offset = 0)
        {
            if (!_infoOf.TryGetValue((texData, flags, offset), out int at))
            {
                TexInfo info = new() { TexData = texData, Flags = flags };
                info.TextureVecsTexelsPerWorldUnits[3] = offset;
                at = Infos.Count;
                Infos.Add(info);
                _infoOf[(texData, flags, offset)] = at;
            }

            return at;
        }

        /// <summary>Adds a box brush with a side per slot (axis x 2, +1 high), in <paramref name="order"/>.</summary>
        public void Box(Vec3 lo, Vec3 hi, int contents = Solid, Func<int, int>? surface = null, int[]? order = null)
        {
            order ??= [0, 1, 2, 3, 4, 5];
            surface ??= _ => Info(Wall, 0);
            Brushes.Add(new DBrush { FirstSide = Sides.Count, NumSides = 6, Contents = contents });
            foreach (int slot in order)
            {
                int axis = slot >> 1;
                float at = (slot & 1) == 1 ? Component(hi, axis) : -Component(lo, axis);
                Sides.Add(new DBrushSide { PlaneNum = Plane(slot, at), TexInfo = (short)surface(slot) });
            }
        }

        /// <summary>Makes brush <paramref name="brush"/> not a box in the named way.</summary>
        public void Spoil(int brush, string how)
        {
            DBrush b = Brushes[brush];
            DBrushSide first = Sides[b.FirstSide];
            switch (how)
            {
                case "seven sides":
                case "five sides":
                    // Rebuild the brush at the end with a side more or less.
                    List<DBrushSide> own = [.. Sides.Skip(b.FirstSide).Take(b.NumSides)];
                    if (how == "seven sides")
                    {
                        own.Add(own[0] with { });
                        own[^1] = new DBrushSide { PlaneNum = Slanted(), TexInfo = own[0].TexInfo };
                    }
                    else
                    {
                        own.RemoveAt(own.Count - 1);
                    }

                    Brushes[brush] = b with { FirstSide = Sides.Count, NumSides = own.Count };
                    Sides.AddRange(own);
                    break;
                case "bevel":
                    Sides[b.FirstSide] = first with { Bevel = 1 };
                    break;
                case "displacement":
                    Sides[b.FirstSide] = first with { DispInfo = 3 };
                    break;
                case "slanted":
                    Sides[b.FirstSide] = first with { PlaneNum = Slanted() };
                    break;
                case "repeated slot":
                    Sides[b.FirstSide + 1] = Sides[b.FirstSide + 1] with { PlaneNum = first.PlaneNum };
                    break;
                default:
                    throw new ArgumentException(how, nameof(how));
            }
        }

        public BrushFoldResult Fold(bool[]? foldable = null) =>
            LinkBrushFold.Fold(Brushes, Sides, Planes, Infos, foldable ?? [.. Enumerable.Repeat(true, Brushes.Count)]);

        public IEnumerable<DBrushSide> SidesOf(int brush) => Sides.Skip(Brushes[brush].FirstSide).Take(Brushes[brush].NumSides);

        /// <summary>A folded brush's box, read back from its planes.</summary>
        public (Vec3 Lo, Vec3 Hi) BoxOf(BrushFoldResult fold, int brush)
        {
            float[] lo = new float[3], hi = new float[3];
            foreach (int slot in SlotsOf(fold, brush))
            {
                DPlane p = Planes[SideAt(fold, brush, slot).PlaneNum];
                if ((slot & 1) == 1)
                {
                    hi[slot >> 1] = p.Dist;
                }
                else
                {
                    lo[slot >> 1] = -p.Dist;
                }
            }

            return (new Vec3(lo[0], lo[1], lo[2]), new Vec3(hi[0], hi[1], hi[2]));
        }

        /// <summary>A folded brush's sides' slots, in side order.</summary>
        public int[] SlotsOf(BrushFoldResult fold, int brush) =>
            [.. fold.Sides.Skip(fold.Brushes[brush].FirstSide).Take(fold.Brushes[brush].NumSides).Select(s => SlotOf(Planes[s.PlaneNum]))];

        public DBrushSide SideAt(BrushFoldResult fold, int brush, int slot) =>
            fold.Sides.Skip(fold.Brushes[brush].FirstSide).Take(fold.Brushes[brush].NumSides).Single(s => SlotOf(Planes[s.PlaneNum]) == slot);

        private static int SlotOf(DPlane p) =>
            p.Normal.X != 0 ? (p.Normal.X > 0 ? 1 : 0) : p.Normal.Y != 0 ? (p.Normal.Y > 0 ? 3 : 2) : (p.Normal.Z > 0 ? 5 : 4);

        private ushort Plane(int slot, float at)
        {
            if (!_planeOf.TryGetValue((slot, at), out ushort index))
            {
                float sign = (slot & 1) == 1 ? 1 : -1;
                Vec3 normal = (slot >> 1) switch
                {
                    0 => new Vec3(sign, 0, 0),
                    1 => new Vec3(0, sign, 0),
                    _ => new Vec3(0, 0, sign),
                };
                index = (ushort)Planes.Count;
                Planes.Add(new DPlane { Normal = normal, Dist = at, Type = slot >> 1 });
                _planeOf[(slot, at)] = index;
            }

            return index;
        }

        private ushort Slanted()
        {
            ushort index = (ushort)Planes.Count;
            Planes.Add(new DPlane { Normal = new Vec3(0.6f, 0.8f, 0), Dist = 10, Type = 3 });
            return index;
        }

        private static float Component(Vec3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
    }
}
