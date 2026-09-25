using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.Tests.MapTools.Bsp.Collision;

/// <summary>
/// Hand-built BSP tables for the emitter's unit facts: axial box brushes in
/// leaves under one node per model, a texinfo per side, and a small
/// surface-property table.
/// </summary>
internal sealed class CollisionFixture
{
    public const string SurfaceText = """
        "default" { "density" "2000" "thickness" "0" }
        "metal" { "density" "2700" }
        "wood" { "density" "700" }
        "shell" { "density" "1000" "thickness" "0.5" }
        "water" { "density" "1000" }
        """;

    private readonly List<DPlane> _planes = [];
    private readonly List<DBrush> _brushes = [];
    private readonly List<DBrushSide> _sides = [];
    private readonly List<TexInfo> _texInfos = [];
    private readonly List<int> _surfaceProperties = [];
    private readonly List<(int Model, int Brush)> _brushModel = [];
    private readonly List<(int Leaf, int Contents)> _extraLeaves = [];
    private readonly List<DFace> _faces = [];
    private readonly List<(int Model, int Face)> _faceModel = [];

    public CollisionFixture()
    {
        Props.ParseSurfaceData("surfaceproperties.txt", SurfaceText);
    }

    public SurfacePropertyTable Props { get; } = new();

    public List<WaterModel> Water { get; } = [];

    public List<CollisionDisplacement> Displacements { get; } = [];

    public List<IReadOnlyList<bool>>? SideVisible { get; set; }

    public bool NoVirtualMesh { get; set; }

    /// <summary>A texinfo whose texdata resolves to the named surface property (null: -1).</summary>
    public int TexInfoFor(string? surfaceProp)
    {
        _surfaceProperties.Add(surfaceProp is null ? -1 : Props.GetSurfaceIndex(surfaceProp));
        TexInfo info = default;
        info.TexData = _surfaceProperties.Count - 1;
        _texInfos.Add(info);
        return _texInfos.Count - 1;
    }

    /// <summary>Adds an axial box brush to a model; sides in +X -X +Y -Y +Z -Z order.</summary>
    public int Box(int model, Vec3 mins, Vec3 maxs, int contents, params int[] texInfos)
    {
        int first = _sides.Count;
        (Vec3 n, float d)[] planes =
        [
            (new(1, 0, 0), maxs.X), (new(-1, 0, 0), -mins.X),
            (new(0, 1, 0), maxs.Y), (new(0, -1, 0), -mins.Y),
            (new(0, 0, 1), maxs.Z), (new(0, 0, -1), -mins.Z),
        ];

        for (int i = 0; i < 6; i++)
        {
            _planes.Add(new DPlane { Normal = planes[i].n, Dist = planes[i].d });
            _sides.Add(new DBrushSide
            {
                PlaneNum = (ushort)(_planes.Count - 1),
                TexInfo = (short)texInfos[texInfos.Length == 1 ? 0 : i],
            });
        }

        _brushes.Add(new DBrush { FirstSide = first, NumSides = 6, Contents = contents });
        _brushModel.Add((model, _brushes.Count - 1));
        return _brushes.Count - 1;
    }

    /// <summary>Gives a model a face (for the brush-entity mass).</summary>
    public void Face(int model, int texInfo, float area)
    {
        DFace face = default;
        face.TexInfo = (short)texInfo;
        face.Area = area;
        _faces.Add(face);
        _faceModel.Add((model, _faces.Count - 1));
    }

    /// <summary>
    /// The input. Each model is one node whose front child is a leaf holding
    /// all of that model's brushes and whose back child is an empty leaf.
    /// </summary>
    public PhysCollisionInput Build(int modelCount = 1)
    {
        List<DNode> nodes = [];
        List<DLeaf> leafs = [];
        List<ushort> leafBrushes = [];
        List<DModel> models = [];
        List<DFace> faces = [];

        for (int m = 0; m < modelCount; m++)
        {
            int[] brushes = [.. _brushModel.Where(b => b.Model == m).Select(b => b.Brush)];
            DLeaf full = new()
            {
                Contents = brushes.Length > 0 ? _brushes[brushes[0]].Contents : 0,
                FirstLeafBrush = (ushort)leafBrushes.Count,
                NumLeafBrushes = (ushort)brushes.Length,
                LeafWaterDataId = 7,
            };
            leafBrushes.AddRange(brushes.Select(b => (ushort)b));
            leafs.Add(full);
            int fullLeaf = leafs.Count - 1;
            leafs.Add(new DLeaf { Contents = CollisionContents.TestFogVolume | 0x20, LeafWaterDataId = 3 });
            int emptyLeaf = leafs.Count - 1;

            DNode node = default;
            node.Children[0] = -1 - fullLeaf;
            node.Children[1] = -1 - emptyLeaf;
            nodes.Add(node);

            int firstFace = faces.Count;
            faces.AddRange(_faceModel.Where(f => f.Model == m).Select(f => _faces[f.Face]));
            models.Add(new DModel
            {
                HeadNode = nodes.Count - 1,
                FirstFace = firstFace,
                NumFaces = faces.Count - firstFace,
                Mins = new Vec3(-64, -64, -64),
                Maxs = new Vec3(64, 64, 64),
            });
        }

        return new PhysCollisionInput
        {
            Planes = _planes,
            Brushes = _brushes,
            BrushSides = _sides,
            Nodes = nodes,
            Leafs = leafs,
            LeafBrushes = leafBrushes,
            Models = models,
            Faces = faces,
            TexInfos = _texInfos,
            SurfaceProperties = _surfaceProperties,
            SurfaceProps = Props,
            WaterModels = Water,
            Displacements = Displacements,
            SideVisible = SideVisible,
            NoVirtualMesh = NoVirtualMesh,
        };
    }

    /// <summary>A flat-ish power-2 displacement over a 64-unit square at the origin.</summary>
    public static CoreDispInfo Displacement(int power = 2, float alpha = 0f)
    {
        CoreDispInfo core = new(power);
        Vec3[] corners = [new(0, 0, 0), new(0, 64, 0), new(64, 64, 0), new(64, 0, 0)];
        for (int i = 0; i < 4; i++)
        {
            core.Surface.SetPoint(i, corners[i]);
        }

        core.Surface.PointStart = corners[0];
        core.Surface.FindSurfPointStartIndex();
        core.Surface.AdjustSurfPointData();

        int size = core.Size;
        float[] alphas = [.. Enumerable.Repeat(alpha, size)];
        Vec3[] vectors = [.. Enumerable.Repeat(new Vec3(0, 0, 1), size)];
        float[] distances = [.. Enumerable.Range(0, size).Select(i => (float)(i % 3))];
        core.InitDispInfo(unchecked((int)0x80000000), alphas, vectors, distances);
        core.Create();
        core.AllowedVertsSetAll();
        return core;
    }
}
