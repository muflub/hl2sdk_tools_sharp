//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The playable area's source (the rooms design, 18.2): the face rule on
/// both sides of the walkable slope, the faces that are not floor, the
/// brush entities a player stands on, and one placement's cut: cell, turn,
/// doorway, furniture and snap.
/// </summary>
public sealed class RoomMapFacesTests
{
    /// <summary>A synthetic compile: faces in models, and an entity lump.</summary>
    internal sealed class FaceBsp
    {
        private readonly List<Vec3> _vertices = [];
        private readonly List<DEdge> _edges = [default];
        private readonly List<int> _surfEdges = [];
        private readonly List<DFace> _faces = [];
        private readonly List<DPlane> _planes = [];
        private readonly List<TexInfo> _texInfos = [];
        private readonly List<DModel> _models = [];
        private readonly List<BspEntity> _entities = [new BspEntity { Pairs = { new("classname", "worldspawn") } }];
        private readonly List<DispInfo> _dispInfos = [];
        private readonly List<DispVert> _dispVerts = [];
        private readonly List<DispTri> _dispTris = [];
        private int _modelStart;

        /// <summary>Adds a face to the model being built: its points in order, its plane's normal, its surface flags.</summary>
        public FaceBsp Face(Vec3 normal, SurfaceFlags flags, params Vec3[] points) => Face(normal, flags, -1, points);

        /// <summary>As <see cref="Face(Vec3, SurfaceFlags, Vec3[])"/>, with a displacement index.</summary>
        public FaceBsp Face(Vec3 normal, SurfaceFlags flags, short displacement, params Vec3[] points)
        {
            _planes.Add(new DPlane { Normal = normal, Dist = Vec3.Dot(normal, points[0]) });
            TexInfo texInfo = default;
            texInfo.Flags = (int)flags;
            _texInfos.Add(texInfo);
            int first = _surfEdges.Count;
            foreach (Vec3 p in points)
            {
                _vertices.Add(p);
            }

            int v0 = _vertices.Count - points.Length;
            for (int i = 0; i < points.Length; i++)
            {
                DEdge edge = default;
                edge.V[0] = (ushort)(v0 + i);
                edge.V[1] = (ushort)(v0 + ((i + 1) % points.Length));
                _surfEdges.Add(_edges.Count);
                _edges.Add(edge);
            }

            _faces.Add(new DFace
            {
                PlaneNum = (ushort)(_planes.Count - 1),
                FirstEdge = first,
                NumEdges = (short)points.Length,
                TexInfo = (short)(_texInfos.Count - 1),
                DispInfo = displacement,
            });
            return this;
        }

        /// <summary>
        /// Adds a face with a displacement on it: the base face's points in
        /// winding order (its start the first), its plane's normal, and each
        /// vertex moved along one vector by <paramref name="distance"/> at
        /// (i, j), i running from the first point to the second and j from
        /// the first to the fourth.
        /// </summary>
        public FaceBsp Displaced(
            Vec3 normal, SurfaceFlags flags, int power, Vec3 vector, Func<int, int, float> distance, Func<int, bool>? removed, params Vec3[] points)
        {
            DispInfo info = default;
            info.StartPosition = points[0];
            info.Power = power;
            info.DispVertStart = _dispVerts.Count;
            info.DispTriStart = _dispTris.Count;
            info.MapFace = (ushort)_faces.Count;
            int n = (1 << Math.Clamp(power, 2, 4)) + 1;
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    _dispVerts.Add(new DispVert { Vector = vector, Dist = distance(i, j), Alpha = 0 });
                }
            }

            for (int t = 0; t < 2 * (n - 1) * (n - 1); t++)
            {
                _dispTris.Add(new DispTri { Tags = (ushort)(DispTriTags.Surface | (removed?.Invoke(t) == true ? DispTriTags.Remove : 0)) });
            }

            _dispInfos.Add(info);
            return Face(normal, flags, (short)(_dispInfos.Count - 1), points);
        }

        /// <summary>Closes the model being built (the world first).</summary>
        public FaceBsp Model()
        {
            _models.Add(new DModel { FirstFace = _modelStart, NumFaces = _faces.Count - _modelStart });
            _modelStart = _faces.Count;
            return this;
        }

        /// <summary>Adds an entity.</summary>
        public FaceBsp Entity(params (string Key, string Value)[] pairs)
        {
            BspEntity entity = new();
            foreach ((string key, string value) in pairs)
            {
                entity.Pairs.Add(new(key, value));
            }

            _entities.Add(entity);
            return this;
        }

        public BspData Build()
        {
            BspData bsp = new();
            bsp.SetLump(BspLump.Vertexes, Bytes(_vertices));
            bsp.SetLump(BspLump.Edges, Bytes(_edges));
            bsp.SetLump(BspLump.SurfEdges, Bytes(_surfEdges));
            bsp.SetLump(BspLump.Faces, Bytes(_faces));
            bsp.SetLump(BspLump.Planes, Bytes(_planes));
            bsp.SetLump(BspLump.TexInfo, Bytes(_texInfos));
            bsp.SetLump(BspLump.Models, Bytes(_models));
            bsp.SetLump(BspLump.Entities, EntityLump.Write(_entities).Data, 0);
            bsp.SetLump(BspLump.DispInfo, Bytes(_dispInfos));
            bsp.SetLump(BspLump.DispVerts, Bytes(_dispVerts));
            bsp.SetLump(BspLump.DispTris, Bytes(_dispTris));
            return bsp;
        }

        private static byte[] Bytes<T>(List<T> items)
            where T : unmanaged => MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(items)).ToArray();
    }

    private static readonly Vec3 Up = new(0, 0, 1);

    /// <summary>A square floor at z, clockwise seen from above as the compiler winds an up-facing face.</summary>
    internal static Vec3[] Floor(float x0, float y0, float x1, float y1, float z) =>
        [new(x0, y0, z), new(x0, y1, z), new(x1, y1, z), new(x1, y0, z)];

    private static List<(double, double)> Xy(MapFaceSource face) => [.. face.Points.Select(p => (p.X, p.Y))];

    /// <summary>
    /// The walkable slope bound on both sides: a face whose normal z is 0.7 is
    /// floor, one at 0.69 is too steep, and a ceiling (normal down) is not.
    /// </summary>
    [Fact]
    public void TheWalkableSlopeBoundHoldsOnBothSides()
    {
        float at = RoomMapFaces.WalkableNormalZ;
        BspData bsp = new FaceBsp()
            .Face(new Vec3(MathF.Sqrt(1 - (at * at)), 0, at), SurfaceFlags.None, Floor(0, 0, 10, 10, 0))
            .Face(new Vec3(MathF.Sqrt(1 - (0.69f * 0.69f)), 0, 0.69f), SurfaceFlags.None, Floor(20, 0, 30, 10, 0))
            .Face(new Vec3(0, 0, -1), SurfaceFlags.None, Floor(40, 0, 50, 10, 64))
            .Model().Build();
        MapFaceSource face = Assert.Single(RoomMapFaces.Walkable(bsp));
        Assert.Equal([(0d, 0d), (0d, 10d), (10d, 10d), (10d, 0d)], Xy(face));
        Assert.True(face.World);
    }

    /// <summary>
    /// Sky, 2D sky, nodraw, water, trigger (plugs and caps), hint and skip
    /// faces are not floor; nor is a displacement's base face whose
    /// displacement the compile does not hold (no record at its index).
    /// </summary>
    [Fact]
    public void FacesThatAreNotFloorAreLeftOut()
    {
        FaceBsp builder = new FaceBsp().Face(Up, SurfaceFlags.Light | SurfaceFlags.NoShadows, Floor(0, 0, 10, 10, 0));
        float x = 20;
        foreach (SurfaceFlags flag in (ReadOnlySpan<SurfaceFlags>)
            [SurfaceFlags.Sky, SurfaceFlags.Sky2D, SurfaceFlags.NoDraw, SurfaceFlags.Warp, SurfaceFlags.Trigger, SurfaceFlags.Hint, SurfaceFlags.Skip])
        {
            builder.Face(Up, flag, Floor(x, 0, x + 10, 10, 0));
            x += 20;
        }

        builder.Face(Up, SurfaceFlags.None, 0, Floor(x, 0, x + 10, 10, 0));
        Assert.Equal([(0d, 0d), (0d, 10d), (10d, 10d), (10d, 0d)], Xy(Assert.Single(RoomMapFaces.Walkable(builder.Model().Build()))));
    }

    /// <summary>
    /// Brush entities count when a player stands on them: a func_brush (moved
    /// by its origin) and a func_door; not a func_brush that is never solid,
    /// not a trigger, not a model the entity does not name as one.
    /// </summary>
    [Fact]
    public void PlayerSolidBrushEntitiesCountMovedByTheirOrigin()
    {
        BspData bsp = new FaceBsp()
            .Face(Up, SurfaceFlags.None, Floor(0, 0, 10, 10, 0)).Model()
            .Face(Up, SurfaceFlags.None, Floor(0, 0, 4, 4, 8)).Model()
            .Face(Up, SurfaceFlags.None, Floor(0, 0, 5, 5, 8)).Model()
            .Face(Up, SurfaceFlags.None, Floor(0, 0, 6, 6, 8)).Model()
            .Face(Up, SurfaceFlags.None, Floor(0, 0, 7, 7, 8)).Model()
            .Entity(("classname", "func_brush"), ("model", "*1"), ("origin", "100 200 16"))
            .Entity(("classname", "func_brush"), ("model", "*2"), ("Solidity", " 1 "))
            .Entity(("classname", "trigger_multiple"), ("model", "*3"))
            .Entity(("classname", "func_door"), ("model", "*4"))
            .Entity(("classname", "func_door"), ("model", "*9"))
            .Entity(("classname", "func_door"), ("model", "4"))
            .Build();
        List<MapFaceSource> faces = RoomMapFaces.Walkable(bsp);
        Assert.Equal(3, faces.Count);
        Assert.True(faces[0].World);
        Assert.Equal((100d, 200d, 24d), faces[1].Points[0]);
        Assert.False(faces[1].World);
        Assert.Equal([(0d, 0d), (0d, 7d), (7d, 7d), (7d, 0d)], Xy(faces[2]));
        Assert.True(RoomMapFaces.IsPlayerSolid(new BspEntity { Pairs = { new("classname", "func_brush"), new("Solidity", "2") } }));
        Assert.False(RoomMapFaces.IsPlayerSolid(new BspEntity { Pairs = { new("classname", "func_illusionary") } }));
    }

    /// <summary>
    /// The cut of a placement: a face across two cells is clipped to the
    /// placement's, a face above the room's height is dropped, and what is
    /// left is taken into the room's frame, snapped and wound counter-clockwise.
    /// </summary>
    [Fact]
    public void APlacementsCutClipsTurnsAndSnaps()
    {
        RoomPlacement placement = new("r", 1, 0, 1);
        MapCut cut = new((100, 0, 0, 200, 100, 100), placement, 100, [], []);
        List<MapFaceSource> faces =
        [
            new([(50, 10, 16), (50, 30, 16), (150.5, 30, 16), (150.5, 10, 16)], true),
            new([(120, 10, 150), (120, 30, 150), (130, 30, 150)], true),
        ];
        MapFacePolygon only = Assert.Single(RoomMapFaces.Place(faces, cut));

        // World x 100 to 150.5, y 10 to 30, in the frame of a room turned a
        // quarter in cell (1, 0): local x = world y, local y = 200 - world x.
        Assert.Equal([new(10, 100), new(10, 50), new(30, 50), new(30, 100)], only.Points.ToList());
        Assert.Equal((16, 16), (only.ZLow, only.ZHigh));
    }

    /// <summary>
    /// Taking a point into its room's frame undoes the placement's turn and
    /// move exactly, at every turn: the inverse of <see cref="RoomTransform.Apply"/>.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ToLocalUndoesThePlacement(int rotation)
    {
        RoomPlacement placement = new("r", 3, 2, rotation);
        RoomTransform transform = new(placement, 256);
        foreach (Vec3 local in (ReadOnlySpan<Vec3>)[new(0, 0, 0), new(12.5f, 200, 16), new(256, 3, 40), new(100, 256, 0)])
        {
            Vec3 world = transform.Apply(local);
            Assert.Equal(((double)local.X, (double)local.Y, (double)local.Z), RoomMapFaces.ToLocal((world.X, world.Y, world.Z), placement, 256));
        }
    }

    /// <summary>
    /// Floor inside a plug box is the door's: cut away when the face's z meets
    /// the box's, kept when it does not (a floor above the opening).
    /// </summary>
    [Fact]
    public void FloorInsideAPlugBoxIsCutAway()
    {
        Box plug = new(new Vec3(240, 80, 16), new Vec3(256, 176, 240));
        MapCut cut = new((0, 0, 0, 256, 256, 256), null, 256, [plug], []);
        List<MapFaceSource> doorway = [new([(200, 60, 16), (200, 200, 16), (256, 200, 16), (256, 60, 16)], true)];
        MapPolygon union = Assert.Single(MapPolygonUnion.Union(RoomMapFaces.Place(doorway, cut)));
        Assert.Equal(
            [new(200, 60), new(256, 60), new(256, 80), new(240, 80), new(240, 176), new(256, 176), new(256, 200), new(200, 200)],
            union.Outer.ToList());

        List<MapFaceSource> above = [new([(200, 60, 250), (200, 200, 250), (256, 200, 250), (256, 60, 250)], true)];
        Assert.Equal(4, Assert.Single(RoomMapFaces.Place(above, cut)).Points.Count);
    }

    /// <summary>
    /// Socket furniture is door hardware: a brush entity's face inside a
    /// furniture brush's box is left out, a world face in the same place is
    /// not (the box is where the hardware stands, the floor under it is the room's).
    /// </summary>
    [Fact]
    public void AFurnitureBrushsFacesAreLeftOutButNotTheWorlds()
    {
        Box furniture = new(new Vec3(200, 100, 0), new Vec3(240, 150, 30));
        MapCut cut = new((0, 0, 0, 256, 256, 256), null, 256, [], [furniture]);
        (double, double, double)[] top = [(200, 100, 30), (200, 150, 30), (240, 150, 30), (240, 100, 30)];
        Assert.Empty(RoomMapFaces.Place([new(top, false)], cut));
        Assert.Single(RoomMapFaces.Place([new(top, true)], cut));
        Assert.Single(RoomMapFaces.Place([new([(10, 10, 30), (10, 20, 30), (20, 20, 30)], false)], cut));
    }

    /// <summary>The snap is to the nearest whole unit with halves up, in the room's frame; z too.</summary>
    [Fact]
    public void TheSnapRoundsHalvesUp()
    {
        MapCut whole = new(null, null, 0, [], []);
        MapFacePolygon snapped = Assert.Single(RoomMapFaces.Place(
            [new([(-0.5, 0.5, 15.5), (-0.5, 10.49, 16.49), (9.5, 10.5, 16.5), (9.51, -0.51, 15.49)], true)], whole));
        Assert.Equal([new(10, -1), new(10, 11), new(0, 10), new(0, 1)], snapped.Points.ToList());
        Assert.Equal((15, 17), (snapped.ZLow, snapped.ZHigh));
    }
    /// <summary>The square 0 to 64 at z 0 as the compiler winds a floor, its start at the origin.</summary>
    private static Vec3[] Base => Floor(0, 0, 64, 64, 0);

    private static IReadOnlyList<MapPolygon> Map(BspData bsp, MapCut? cut = null) =>
        MapPolygonUnion.Union(RoomMapFaces.Place(RoomMapFaces.Walkable(bsp), cut ?? new MapCut(null, null, 0, [], [])));

    private static (int, int, int, int) Bounds(IReadOnlyList<MapPoint> ring) =>
        (ring.Min(p => p.X), ring.Min(p => p.Y), ring.Max(p => p.X), ring.Max(p => p.Y));

    /// <summary>
    /// A displacement is floor by its surface, not its base face: a patch
    /// raised 10 units over a floor square maps as that square at z 10, and
    /// nothing is at the base face's z.
    /// </summary>
    [Fact]
    public void ADisplacementIsFloorByItsSurfaceNotItsBaseFace()
    {
        BspData bsp = new FaceBsp().Displaced(Up, SurfaceFlags.None, 2, Up, (_, _) => 10, null, Base).Model().Build();
        MapFaceSource source = Assert.Single(RoomMapFaces.Walkable(bsp));
        Assert.NotNull(source.Displacement);
        MapPolygon floor = Assert.Single(Map(bsp));
        Assert.Equal((10, 10), (floor.ZLow, floor.ZHigh));
        Assert.Equal((0, 0, 64, 64), Bounds(floor.Outer));
        Assert.Equal(4, floor.Outer.Count);
    }

    /// <summary>
    /// The walkable slope on both sides, per triangle: a patch rising 16
    /// units every 16 (normal z 0.707) is floor, one rising 17 (0.685) is not.
    /// </summary>
    [Fact]
    public void ADisplacementsSlopeBoundHoldsOnBothSides()
    {
        MapPolygon slope = Assert.Single(Map(new FaceBsp().Displaced(Up, SurfaceFlags.None, 2, Up, (_, j) => 16 * j, null, Base).Model().Build()));
        Assert.Equal((0, 64), (slope.ZLow, slope.ZHigh));
        Assert.Empty(Map(new FaceBsp().Displaced(Up, SurfaceFlags.None, 2, Up, (_, j) => 17 * j, null, Base).Model().Build()));
    }

    /// <summary>
    /// A displacement's steep parts are not floor: a ridge 40 units high along
    /// x = 32 leaves the ground either side of it, two strips at z 0, and
    /// nothing on the ridge's flanks.
    /// </summary>
    [Fact]
    public void ADisplacementsSteepPartsAreNotFloor()
    {
        IReadOnlyList<MapPolygon> map = Map(new FaceBsp().Displaced(Up, SurfaceFlags.None, 2, Up, (_, j) => j == 2 ? 40 : 0, null, Base).Model().Build());
        Assert.Equal(2, map.Count);
        Assert.All(map, p => Assert.Equal((0, 0), (p.ZLow, p.ZHigh)));
        Assert.Equal([(0, 0, 16, 64), (48, 0, 64, 64)], map.Select(p => Bounds(p.Outer)).Order().ToList());
    }

    /// <summary>
    /// A displacement facing down (a ceiling's) is not floor however flat;
    /// nor is one whose base face is nodraw, nor the triangles the author
    /// removed; one whose record the compile cannot build (power 5) is no
    /// source at all.
    /// </summary>
    [Fact]
    public void DisplacementsThatAreNotFloorAreLeftOut()
    {
        Assert.Empty(Map(new FaceBsp().Displaced(new Vec3(0, 0, -1), SurfaceFlags.None, 2, Up, (_, _) => 0, null, Base).Model().Build()));
        Assert.Empty(Map(new FaceBsp().Displaced(Up, SurfaceFlags.NoDraw, 2, Up, (_, _) => 0, null, Base).Model().Build()));
        Assert.Empty(RoomMapFaces.Walkable(new FaceBsp().Displaced(Up, SurfaceFlags.None, 5, Up, (_, _) => 0, null, Base).Model().Build()));

        // Every triangle removed but the first square's two: the square 0 to 16 in x and y.
        MapPolygon kept = Assert.Single(Map(new FaceBsp().Displaced(Up, SurfaceFlags.None, 2, Up, (_, _) => 0, t => t >= 2, Base).Model().Build()));
        Assert.Equal((0, 0, 16, 16), Bounds(kept.Outer));
    }

    /// <summary>
    /// A displacement on a wall is judged by its surface, not its base face's
    /// slope: a west-facing wall's patch pushed out 16 units for every 16 up
    /// is a 45-degree ramp, floor from z 0 to 64; pushed the other way, the
    /// ramp faces down and is not.
    /// </summary>
    [Fact]
    public void AWallsDisplacementSculptedIntoARampIsFloor()
    {
        Vec3 west = new(-1, 0, 0);
        Vec3[] wall = [new(0, 0, 0), new(0, 0, 64), new(0, 64, 64), new(0, 64, 0)];
        MapPolygon ramp = Assert.Single(Map(new FaceBsp().Displaced(west, SurfaceFlags.None, 2, new Vec3(1, 0, 0), (i, _) => 16 * i, null, wall).Model().Build()));
        Assert.Equal((0, 64), (ramp.ZLow, ramp.ZHigh));
        Assert.Equal((0, 0, 64, 64), Bounds(ramp.Outer));
        Assert.Empty(Map(new FaceBsp().Displaced(west, SurfaceFlags.None, 2, west, (i, _) => 16 * i, null, wall).Model().Build()));
    }

    /// <summary>
    /// A placed displacement is rebuilt in its room's frame: the room's
    /// ridged patch placed at every turn in cell (1, 2) of a 256-unit grid,
    /// read from the level's frame through the placement, gives the same
    /// pieces as the room's own; the cell that owns it is the one holding its
    /// base face's centre, and its triangles are cut to that cell in the
    /// room's frame (the part of the patch past the cell's edge is not drawn).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void APlacedDisplacementIsRebuiltInItsRoomsFrame(int rotation)
    {
        // A patch over x 192 to 288, past the cell's east edge at 256; ridged, sloped and offset so no two vertices agree.
        Vec3[] local = Floor(192, 32, 288, 128, 16);
        Vec3 lean = new(0.25f, -0.125f, 0.9602f);
        static float Height(int i, int j) => j == 2 ? 40 : (0.3f * i) + (1.7f * j);
        BspData room = new FaceBsp().Displaced(Up, SurfaceFlags.None, 2, lean, Height, null, local).Model().Build();
        MapCut own = new((0, 0, 0, 256, 256, 256), null, 256, [], []);
        List<MapFacePolygon> expected = RoomMapFaces.Place(RoomMapFaces.Walkable(room), own);
        Assert.NotEmpty(expected);
        Assert.All(expected, p => Assert.All(p.Points, q => Assert.True(q.X <= 256)));

        RoomPlacement placement = new("r", 1, 2, rotation);
        RoomTransform transform = new(placement, 256);
        Vec3 turned = RoomTransform.Rotate(lean, rotation);
        Vec3[] world = [.. local.Select(transform.Apply)];

        // The winding a turn keeps is the same; the start is the corner at the room's (192, 32).
        BspData level = new FaceBsp().Displaced(Up, SurfaceFlags.None, 2, turned, Height, null, world).Model().Build();
        MapFaceSource source = Assert.Single(RoomMapFaces.Walkable(level));
        Assert.Equal((1L, 2L), RoomMapFaces.OwnerCell(source, 256));
        MapCut cut = new((256, 512, 0, 512, 768, 256), placement, 256, [], []);
        Assert.Equal(Describe(expected), Describe(RoomMapFaces.Place([source], cut)));

        static List<string> Describe(List<MapFacePolygon> pieces) =>
            [.. pieces.Select(p => $"{p.ZLow} {p.ZHigh} {string.Join(' ', p.Points.Select(q => $"{q.X},{q.Y}"))}")];
    }
}
