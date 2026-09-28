//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Text;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Nav;

/// <summary>One neighbour of a leaf, as <see cref="Nav3dReader.Neighbours"/> yields it.</summary>
/// <param name="Leaf">The neighbouring leaf.</param>
/// <param name="Direction">Which way the step goes.</param>
/// <param name="ThroughDoor">Whether the step crosses from one room's cell into another's: the only way it can is through a joined door.</param>
public readonly record struct Nav3dNeighbour(int Leaf, Nav3dDirection Direction, bool ThroughDoor);

/// <summary>
/// Reads a <c>.nav3d</c> file: validates it once, derives what the file
/// leaves out, and then answers point, neighbour and clearance queries
/// without allocating.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why in place.</b> The game mod is written in C# and references this
/// assembly directly, and its AI asks "which leaf is this point in", "what
/// are this leaf's neighbours" and "does this agent fit here" many times a
/// frame. So the reader validates the file once, in <see cref="Open"/>,
/// remembers where each section is, and after that answers from the bytes
/// and from arrays it built at load: a leaf is a 24-byte record, a point
/// lookup is one column's short list of runs, a clearance test is a few
/// comparisons against its record's staircase, and a neighbour walk is a
/// <c>ref struct</c> over one array. None of those allocates. The calls that
/// return strings or whole records (<see cref="Poi"/>, <see cref="Cell"/>,
/// <see cref="Obstacle"/>, <see cref="ToLevel"/>) do, and are meant for
/// loading and tools, not per-frame use.
/// </para>
/// <para>
/// <b>Derived at load, not stored.</b> Version 1 stored every leaf's
/// neighbours, about half its file. They are a pure function of the columns:
/// two leaves neighbour when they are in the same column and touch, or in
/// columns side by side (across a cell face too) and their voxel runs
/// overlap. The reader works them out in one pass, in a fixed order, so every
/// load of a file derives the same arrays. The same goes for each preset's
/// connected components, each point's leaf, each obstacle's leaves and each
/// leaf's jump links. A game server has cycles to spare at start; it pays
/// once and the queries stay as cheap as a stored table.
/// </para>
/// <para>
/// <b>Validated up front.</b> <see cref="Open"/> checks the header, the
/// directory, every section's size against the counts, every root and
/// column start, every leaf's run and clearance record, every obstacle and
/// brush index a record names, and every jump, so a query on an opened file
/// never indexes outside a section.
/// </para>
/// </remarks>
public sealed class Nav3dReader
{
    private readonly ReadOnlyMemory<byte> _file;
    private readonly Sections _s;
    private readonly int[] _leafColumn;
    private readonly int[] _blockCell;
    private readonly uint[] _adjacencyStart;
    private readonly uint[] _adjacency;
    private readonly uint[] _jumpStart;
    private readonly int[] _jumpIndex;
    private readonly NavBrush[] _brushes;
    private readonly int[] _poiLeaf;
    private readonly uint[] _obstacleStart;
    private readonly int[] _obstacleLeaves;
    private readonly int[][] _components;
    private readonly int[] _componentCounts;

    private Nav3dReader(ReadOnlyMemory<byte> file, Header header, Sections sections, NavBrush[] brushes)
    {
        _file = file;
        _s = sections;
        _brushes = brushes;
        Version = header.Version;
        Codec = header.Codec;
        CellSize = header.CellSize;
        VoxelSize = header.VoxelSize;
        CellVoxels = header.CellVoxels;
        Columns = header.Columns;
        Rows = header.Rows;
        Origin = header.Origin;
        FloorNormalZ = header.FloorNormalZ;
        StepHeight = header.StepHeight;
        JumpHeight = header.JumpHeight;
        JumpDistance = header.JumpDistance;
        PoiCount = header.PoiCount;
        DoorCount = header.DoorCount;
        SpawnPoi = header.Spawn;
        UpArrivalPoi = header.Up;
        DownArrivalPoi = header.Down;
        LevelId = header.LevelId;
        PackId = header.PackId;
        PresetCount = header.PresetCount;
        LeafCount = header.LeafCount;
        ObstacleCount = header.ObstacleCount;
        JumpCount = header.JumpCount;

        (_leafColumn, _blockCell) = DeriveColumns();
        (_adjacencyStart, _adjacency) = DeriveAdjacency();
        (_jumpStart, _jumpIndex) = DeriveJumps();
        _poiLeaf = [.. Enumerable.Range(0, PoiCount).Select(p => FindLeaf(PoiPosition(p)))];
        (_obstacleStart, _obstacleLeaves) = DeriveObstacleLeaves();
        _components = new int[PresetCount][];
        _componentCounts = new int[PresetCount];
        for (int p = 0; p < PresetCount; p++)
        {
            (_components[p], _componentCounts[p]) = DeriveComponents(p);
        }
    }

    /// <summary>The file's format version.</summary>
    public int Version { get; }

    /// <summary>How the file's image was stored.</summary>
    public NavCodec Codec { get; }

    /// <summary>A cell's edge, in units.</summary>
    public float CellSize { get; }

    /// <summary>A voxel's edge, in units.</summary>
    public float VoxelSize { get; }

    /// <summary>Voxels along a cell's edge.</summary>
    public int CellVoxels { get; }

    /// <summary>The grid's columns, west to east.</summary>
    public int Columns { get; }

    /// <summary>The grid's rows, south to north.</summary>
    public int Rows { get; }

    /// <summary>The grid's low corner in level coordinates.</summary>
    public Vec3 Origin { get; }

    /// <summary>The least normal z of a walkable floor.</summary>
    public float FloorNormalZ { get; }

    /// <summary>The highest step an agent walks up without jumping.</summary>
    public float StepHeight { get; }

    /// <summary>The highest ledge a jump link climbs, and the deepest it drops.</summary>
    public float JumpHeight { get; }

    /// <summary>The farthest a jump link reaches, between column centres.</summary>
    public float JumpDistance { get; }

    /// <summary>How many agent presets the file records.</summary>
    public int PresetCount { get; }

    /// <summary>How many cells the grid has.</summary>
    public int CellCount => Columns * Rows;

    /// <summary>How many leaves the grid has.</summary>
    public int LeafCount { get; }

    /// <summary>How many dynamic obstacles the file names.</summary>
    public int ObstacleCount { get; }

    /// <summary>How many jump links the file holds.</summary>
    public int JumpCount { get; }

    /// <summary>How many points of interest the file holds.</summary>
    public int PoiCount { get; }

    /// <summary>How many door records the file holds.</summary>
    public int DoorCount { get; }

    /// <summary>The spawn point's index, or -1.</summary>
    public int SpawnPoi { get; }

    /// <summary>The up room's arrival point, or -1.</summary>
    public int UpArrivalPoi { get; }

    /// <summary>The down room's arrival point, or -1.</summary>
    public int DownArrivalPoi { get; }

    /// <summary>
    /// The level id: the same one the map's worldspawn carries as
    /// <c>ss_level_id</c> when the two files come from one link. Compare with
    /// <see cref="MatchesMap"/>.
    /// </summary>
    public Guid LevelId { get; }

    /// <summary>The id of the room pack the level was linked from, or <see cref="Guid.Empty"/>.</summary>
    public Guid PackId { get; }

    private ReadOnlySpan<byte> Bytes => _file.Span;

    /// <summary>Whether this navigation belongs to a map: its level id equals the map's <c>ss_level_id</c> worldspawn value.</summary>
    /// <param name="mapLevelId">The map's <c>ss_level_id</c>, as the game reads it from the worldspawn.</param>
    /// <returns>True when both ids parse and are equal, and are not empty.</returns>
    public bool MatchesMap(string? mapLevelId) =>
        LevelId != Guid.Empty && Guid.TryParse(mapLevelId, out Guid id) && id == LevelId;

    /// <summary>Opens a file held in memory, validating all of it and deriving its neighbours, components and indexes.</summary>
    /// <param name="file">
    /// The whole file. A raw file is kept and read in place; a compressed
    /// one is decoded once into a buffer the reader keeps.
    /// </param>
    /// <returns>The reader.</returns>
    /// <exception cref="InvalidDataException">The bytes are not a valid <c>.nav3d</c> file of this version; the message says what is wrong.</exception>
    public static Nav3dReader Open(ReadOnlyMemory<byte> file)
    {
        ReadOnlySpan<byte> envelope = file.Span;
        if (envelope.Length < Nav3dFormat.EnvelopeBytes || !envelope[..8].SequenceEqual(Nav3dFormat.Magic))
        {
            throw new InvalidDataException("not a .nav3d file: the magic is missing.");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(envelope[8..]);
        if (version != Nav3dFormat.Version)
        {
            throw new InvalidDataException($".nav3d version {version}; this build reads version {Nav3dFormat.Version}.");
        }

        NavCodec codec = (NavCodec)envelope[12];
        int imageLength = BinaryPrimitives.ReadInt32LittleEndian(envelope[16..]);
        int storedLength = BinaryPrimitives.ReadInt32LittleEndian(envelope[20..]);
        if (imageLength < Nav3dFormat.HeaderBytes || storedLength < 0 || (long)Nav3dFormat.EnvelopeBytes + storedLength != envelope.Length)
        {
            throw new InvalidDataException(
                $"the .nav3d envelope says {storedLength} stored bytes for a {imageLength}-byte image, but {envelope.Length - Nav3dFormat.EnvelopeBytes} follow it.");
        }

        // Raw: the image is read where it lies, no copy. Compressed: decoded
        // once, here, into the one buffer every later query reads.
        ReadOnlyMemory<byte> stored = file[Nav3dFormat.EnvelopeBytes..];
        file = codec == NavCodec.None
            ? (storedLength == imageLength ? stored : throw new InvalidDataException("a raw .nav3d image whose stored and image lengths differ."))
            : NavCompression.Decompress(codec, stored.Span, imageLength);
        ReadOnlySpan<byte> b = file.Span;
        Header header = new()
        {
            Version = version,
            Codec = codec,
            HeaderBytes = BinaryPrimitives.ReadInt32LittleEndian(b),
            SectionCount = BinaryPrimitives.ReadInt32LittleEndian(b[4..]),
            PresetCount = BinaryPrimitives.ReadInt32LittleEndian(b[8..]),
            CellSize = BinaryPrimitives.ReadSingleLittleEndian(b[12..]),
            VoxelSize = BinaryPrimitives.ReadSingleLittleEndian(b[16..]),
            CellVoxels = BinaryPrimitives.ReadInt32LittleEndian(b[20..]),
            Columns = BinaryPrimitives.ReadInt32LittleEndian(b[24..]),
            Rows = BinaryPrimitives.ReadInt32LittleEndian(b[28..]),
            Origin = new Vec3(
                BinaryPrimitives.ReadSingleLittleEndian(b[32..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[36..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[40..])),
            FloorNormalZ = BinaryPrimitives.ReadSingleLittleEndian(b[44..]),
            StepHeight = BinaryPrimitives.ReadSingleLittleEndian(b[48..]),
            JumpHeight = BinaryPrimitives.ReadSingleLittleEndian(b[52..]),
            JumpDistance = BinaryPrimitives.ReadSingleLittleEndian(b[56..]),
            PoiCount = BinaryPrimitives.ReadInt32LittleEndian(b[60..]),
            DoorCount = BinaryPrimitives.ReadInt32LittleEndian(b[64..]),
            Spawn = BinaryPrimitives.ReadInt32LittleEndian(b[68..]),
            Up = BinaryPrimitives.ReadInt32LittleEndian(b[72..]),
            Down = BinaryPrimitives.ReadInt32LittleEndian(b[76..]),
            ColumnCount = BinaryPrimitives.ReadInt32LittleEndian(b[80..]),
            LeafCount = BinaryPrimitives.ReadInt32LittleEndian(b[84..]),
            ObstacleCount = BinaryPrimitives.ReadInt32LittleEndian(b[88..]),
            BrushCount = BinaryPrimitives.ReadInt32LittleEndian(b[92..]),
            JumpCount = BinaryPrimitives.ReadInt32LittleEndian(b[96..]),
            ClearanceBytes = BinaryPrimitives.ReadInt32LittleEndian(b[100..]),
            LevelId = new Guid(b.Slice(104, 16), bigEndian: true),
            PackId = new Guid(b.Slice(120, 16), bigEndian: true),
        };

        if (header.HeaderBytes < Nav3dFormat.HeaderBytes || header.SectionCount < 0
            || header.PresetCount is < 0 or > Nav3dFormat.MaxPresets || header.PoiCount < 0 || header.DoorCount < 0
            || header.ColumnCount < 0 || header.LeafCount < 0 || header.ObstacleCount < 0 || header.BrushCount < 0
            || header.JumpCount < 0 || header.ClearanceBytes < 0)
        {
            throw new InvalidDataException("the .nav3d header's sizes are out of range.");
        }

        if (!(header.CellSize > 0) || !(header.VoxelSize > 0) || header.CellVoxels is < 1 or > Nav3dFormat.MaxCellVoxels
            || header.Columns < 1 || header.Rows < 1 || (long)header.Columns * header.Rows > int.MaxValue
            || !float.IsFinite(header.CellSize) || !float.IsFinite(header.VoxelSize))
        {
            throw new InvalidDataException("the .nav3d header describes no valid grid.");
        }

        long directoryEnd = header.HeaderBytes + ((long)header.SectionCount * Nav3dFormat.DirectoryEntryBytes);
        if (directoryEnd > b.Length)
        {
            throw new InvalidDataException("the .nav3d section directory runs past the end of the file.");
        }

        Dictionary<string, Section> sections = new(StringComparer.Ordinal);
        for (int i = 0; i < header.SectionCount; i++)
        {
            ReadOnlySpan<byte> e = b.Slice(header.HeaderBytes + (i * Nav3dFormat.DirectoryEntryBytes), Nav3dFormat.DirectoryEntryBytes);
            string tag = Encoding.ASCII.GetString(e[..4]);
            uint index = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            if (offset < directoryEnd || (offset & 3) != 0 || (long)offset + length > b.Length)
            {
                throw new InvalidDataException($"the .nav3d section \"{tag}\" lies outside the file or off a four-byte boundary.");
            }

            if (index != Nav3dFormat.LevelIndex)
            {
                // A section for some other index: a later version's, skipped.
                continue;
            }

            if (!sections.TryAdd(tag, new Section((int)offset, (int)length)))
            {
                throw new InvalidDataException($"the .nav3d file has two \"{tag}\" sections.");
            }
        }

        int cells = header.Columns * header.Rows;
        Sections s = new()
        {
            Strings = Require(sections, Nav3dFormat.StringsTag, 1, -1),
            Presets = Require(sections, Nav3dFormat.PresetsTag, Nav3dFormat.PresetRecordBytes, header.PresetCount),
            Cells = Require(sections, Nav3dFormat.CellsTag, Nav3dFormat.CellRecordBytes, cells),
            Doors = Require(sections, Nav3dFormat.DoorsTag, Nav3dFormat.DoorRecordBytes, header.DoorCount),
            Pois = Require(sections, Nav3dFormat.PoisTag, Nav3dFormat.PoiRecordBytes, header.PoiCount),
            Roots = Require(sections, Nav3dFormat.RootsTag, 4, cells),
            ColumnStarts = Require(sections, Nav3dFormat.ColumnsTag, 4, header.ColumnCount + 1),
            Leaves = Require(sections, Nav3dFormat.LeavesTag, Nav3dFormat.LeafRecordBytes, header.LeafCount),
            Clearance = Require(sections, Nav3dFormat.ClearanceTag, 1, header.ClearanceBytes),
            Obstacles = Require(sections, Nav3dFormat.ObstaclesTag, Nav3dFormat.ObstacleRecordBytes, header.ObstacleCount),
            BrushIndex = Require(sections, Nav3dFormat.BrushIndexTag, 4, header.BrushCount + 1),
            BrushPlanes = Require(sections, Nav3dFormat.BrushPlanesTag, 16, -1),
            Jumps = Require(sections, Nav3dFormat.JumpsTag, Nav3dFormat.JumpRecordBytes, header.JumpCount),
        };

        if (b[s.Strings.Offset] != 0 || b[s.Strings.Offset + s.Strings.Length - 1] != 0)
        {
            throw new InvalidDataException("the .nav3d string table must start with the empty string and end with a NUL.");
        }

        if (header.Spawn < -1 || header.Spawn >= header.PoiCount || header.Up < -1 || header.Up >= header.PoiCount
            || header.Down < -1 || header.Down >= header.PoiCount)
        {
            throw new InvalidDataException("the .nav3d spawn or arrival index names no point of interest.");
        }

        ValidateGrid(b, s, header, cells);
        NavBrush[] brushes = ValidateBrushes(b, s, header.BrushCount);
        ValidateRecords(b, s, header, cells);
        return new Nav3dReader(file, header, s, brushes);
    }

    /// <summary>One agent preset.</summary>
    /// <param name="preset">The preset.</param>
    /// <returns>Its record.</returns>
    public Nav3dPreset Preset(int preset)
    {
        ReadOnlySpan<byte> r = Record(_s.Presets, preset, Nav3dFormat.PresetRecordBytes);
        return new Nav3dPreset(
            String(BinaryPrimitives.ReadUInt32LittleEndian(r)) ?? string.Empty,
            BinaryPrimitives.ReadSingleLittleEndian(r[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(r[8..]),
            (Nav3dClipClass)r[12]);
    }

    /// <summary>Finds a preset by name.</summary>
    /// <param name="name">The name, compared exactly.</param>
    /// <returns>The preset's index, or -1.</returns>
    public int FindPreset(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (int p = 0; p < PresetCount; p++)
        {
            if (Preset(p).Name == name)
            {
                return p;
            }
        }

        return -1;
    }

    /// <summary>One leaf.</summary>
    /// <param name="leaf">The leaf.</param>
    /// <returns>Its record.</returns>
    public Nav3dLeaf Leaf(int leaf)
    {
        ReadOnlySpan<byte> r = Record(_s.Leaves, leaf, Nav3dFormat.LeafRecordBytes);
        return new Nav3dLeaf(
            r[0],
            r[1],
            (Nav3dLeafFlags)BinaryPrimitives.ReadUInt16LittleEndian(r[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(r[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(r[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(r[12..]),
            BinaryPrimitives.ReadSingleLittleEndian(r[16..]),
            BinaryPrimitives.ReadSingleLittleEndian(r[20..]));
    }

    /// <summary>Where a leaf's column is: its cell and the column within the cell.</summary>
    /// <param name="leaf">The leaf.</param>
    /// <returns>The cell (<c>row × columns + column</c>) and the voxel column's x and y within it.</returns>
    public (int Cell, int X, int Y) LeafColumn(int leaf)
    {
        int column = _leafColumn[leaf];
        int block = CellVoxels * CellVoxels;
        int within = column % block;
        return (_blockCell[column / block], within % CellVoxels, within / CellVoxels);
    }

    /// <summary>A leaf's box in level coordinates: its column's footprint from its lowest voxel's bottom to its highest voxel's top.</summary>
    /// <param name="leaf">The leaf.</param>
    /// <returns>The box's corners.</returns>
    public (Vec3 Mins, Vec3 Maxs) LeafBounds(int leaf)
    {
        (int cell, int x, int y) = LeafColumn(leaf);
        Nav3dLeaf l = Leaf(leaf);
        double s = VoxelSize;
        double x0 = Origin.X + ((((cell % Columns) * CellVoxels) + x) * s);
        double y0 = Origin.Y + ((((cell / Columns) * CellVoxels) + y) * s);
        return (new Vec3((float)x0, (float)y0, (float)(Origin.Z + (l.ZLo * s))),
            new Vec3((float)(x0 + s), (float)(y0 + s), (float)(Origin.Z + ((l.ZHi + 1) * s))));
    }

    /// <summary>A cell's first voxel column, for a placed cell.</summary>
    /// <param name="cell">The cell, <c>row × columns + column</c>.</param>
    /// <returns>The column's index, or -1 when no room stands in the cell.</returns>
    public int CellRoot(int cell) => (int)ReadU32(_s.Roots, cell, 4, 0);

    /// <summary>The leaf holding a point: the run of free voxels an agent's origin (its feet) there is in.</summary>
    /// <param name="point">The point, in level coordinates.</param>
    /// <returns>The leaf, or -1 when the point is outside the grid, in an empty cell, or in solid.</returns>
    /// <remarks>
    /// A point on a voxel boundary belongs to the voxel above it on each axis
    /// (voxels are half-open, low side in). Allocation-free: one column's
    /// runs are searched.
    /// </remarks>
    public int FindLeaf(Vec3 point)
    {
        if (!TryVoxel(point, out int column, out int row, out int vx, out int vy, out int vz))
        {
            return -1;
        }

        return FindLeaf((row * Columns) + column, vx, vy, vz);
    }

    /// <summary>The leaf holding a voxel of a cell.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="x">The voxel within the cell along x, 0 to <see cref="CellVoxels"/> - 1.</param>
    /// <param name="y">Along y.</param>
    /// <param name="z">Along z.</param>
    /// <returns>The leaf, or -1.</returns>
    public int FindLeaf(int cell, int x, int y, int z)
    {
        if ((uint)cell >= (uint)CellCount || (uint)x >= (uint)CellVoxels || (uint)y >= (uint)CellVoxels
            || (uint)z >= (uint)CellVoxels)
        {
            return -1;
        }

        int root = CellRoot(cell);
        if (root < 0)
        {
            return -1;
        }

        int column = root + (y * CellVoxels) + x;
        int first = (int)ReadU32(_s.ColumnStarts, column, 4, 0);
        int end = (int)ReadU32(_s.ColumnStarts, column + 1, 4, 0);
        ReadOnlySpan<byte> leaves = Slice(_s.Leaves);
        for (int l = first; l < end; l++)
        {
            int zLo = leaves[l * Nav3dFormat.LeafRecordBytes];
            if (z < zLo)
            {
                return -1;
            }

            if (z < zLo + leaves[(l * Nav3dFormat.LeafRecordBytes) + 1])
            {
                return l;
            }
        }

        return -1;
    }

    /// <summary>A leaf's clearance record for a clip class (<see cref="Nav3dClearance"/>).</summary>
    /// <param name="leaf">The leaf.</param>
    /// <param name="clipClass">The class.</param>
    /// <returns>The record's bytes (and what follows it in its section).</returns>
    public ReadOnlySpan<byte> ClearanceRecord(int leaf, Nav3dClipClass clipClass)
    {
        uint offset = ReadU32(_s.Leaves, leaf, Nav3dFormat.LeafRecordBytes, clipClass == Nav3dClipClass.Npc ? 12 : 8);
        return Slice(_s.Clearance)[(int)offset..];
    }

    /// <summary>
    /// Whether an agent fits in one voxel of a leaf: its box, with its origin
    /// anywhere in the voxel, overlaps no solid of its class.
    /// </summary>
    /// <param name="leaf">The leaf.</param>
    /// <param name="z">The voxel, within the cell, inside the leaf's run.</param>
    /// <param name="width">The agent's width (and depth).</param>
    /// <param name="height">The agent's height.</param>
    /// <param name="clipClass">Its clip class.</param>
    /// <param name="blocking">Per dynamic obstacle, whether it is in the way now; empty for none (every obstacle open).</param>
    /// <returns>True when it fits; false outside the leaf's run.</returns>
    /// <remarks>Exact for every size: the record's corners, and for an overhanging brush the separating-axis test itself. Allocation-free.</remarks>
    public bool Passable(int leaf, int z, float width, float height, Nav3dClipClass clipClass, ReadOnlySpan<bool> blocking = default)
    {
        Nav3dLeaf l = Leaf(leaf);
        if (z < l.ZLo || z > l.ZHi)
        {
            return false;
        }

        ReadOnlySpan<byte> record = ClearanceRecord(leaf, clipClass);
        double r = width * 0.5;
        double top = VoxelTop(z);
        if (Nav3dClearance.CornersBlock(record, r, height, top, blocking))
        {
            return false;
        }

        int brushes = Nav3dClearance.BrushCount(record);
        if (brushes == 0)
        {
            return true;
        }

        NavBox box = SweptBox(leaf, z, r, height);
        for (int i = 0; i < brushes; i++)
        {
            if (_brushes[Nav3dClearance.Brush(record, i)].Overlaps(box))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether an agent stands in a voxel of a leaf: it fits there and cannot
    /// sink a voxel, because solid is right under it (then the floor must be
    /// walkable) or the leaf below is too narrow or low for it (it rests on
    /// the rim, which counts as walkable).
    /// </summary>
    /// <param name="leaf">The leaf.</param>
    /// <param name="z">The voxel.</param>
    /// <param name="width">The agent's width.</param>
    /// <param name="height">The agent's height.</param>
    /// <param name="clipClass">Its clip class.</param>
    /// <param name="blocking">Per dynamic obstacle, whether it is in the way now; empty for none.</param>
    /// <returns>True when it stands there.</returns>
    /// <remarks>
    /// An agent that fits in a voxel of a run fits in every voxel of the run
    /// below it (the fit is a prefix of the run), so only a run's bottom
    /// voxel can be stood in.
    /// </remarks>
    public bool Standable(int leaf, int z, float width, float height, Nav3dClipClass clipClass, ReadOnlySpan<bool> blocking = default)
    {
        Nav3dLeaf l = Leaf(leaf);
        if (z != l.ZLo || !Passable(leaf, z, width, height, clipClass, blocking))
        {
            return false;
        }

        if (l.IsGrounded(clipClass))
        {
            return l.IsWalkable(clipClass);
        }

        int below = LeafBelow(leaf);
        return below >= 0 && !Passable(below, z - 1, width, height, clipClass, blocking);
    }

    /// <summary>
    /// The highest voxel of a leaf an agent fits in: an agent that fits in a
    /// voxel fits in every voxel below it in the run, so its room is the run
    /// from the bottom up to this.
    /// </summary>
    /// <param name="leaf">The leaf.</param>
    /// <param name="width">The agent's width.</param>
    /// <param name="height">The agent's height.</param>
    /// <param name="clipClass">Its clip class.</param>
    /// <param name="blocking">Per dynamic obstacle, whether it is in the way now; empty for none.</param>
    /// <returns>The voxel, or -1 when it fits nowhere in the leaf.</returns>
    public int FitTop(int leaf, float width, float height, Nav3dClipClass clipClass, ReadOnlySpan<bool> blocking = default)
    {
        Nav3dLeaf l = Leaf(leaf);
        ReadOnlySpan<byte> record = ClearanceRecord(leaf, clipClass);
        if (Nav3dClearance.BrushCount(record) > 0)
        {
            return Passable(leaf, l.ZLo, width, height, clipClass, blocking) ? l.ZHi : -1;
        }

        double room = Nav3dClearance.HeadRoom(record, width * 0.5, 0, blocking);
        if (double.IsNegativeInfinity(room))
        {
            return -1;
        }

        int top = l.ZHi;
        if (!double.IsPositiveInfinity(room))
        {
            // room is (T + e) at voxel top 0: the fit ends where the voxel's
            // top plus the height passes it. A first guess, then the exact
            // comparison decides the boundary voxel.
            double guess = Math.Floor((room - height - Origin.Z) / VoxelSize) - 1;
            top = (int)Math.Clamp(guess, l.ZLo - 1, l.ZHi);
            while (top < l.ZHi && !Nav3dClearance.CornersBlock(record, width * 0.5, height, VoxelTop(top + 1), blocking))
            {
                top++;
            }

            while (top >= l.ZLo && Nav3dClearance.CornersBlock(record, width * 0.5, height, VoxelTop(top), blocking))
            {
                top--;
            }
        }

        return top >= l.ZLo ? top : -1;
    }

    /// <summary>The most height an agent of a given width has in a voxel of a leaf: it fits exactly when its height is at most this.</summary>
    /// <param name="leaf">The leaf.</param>
    /// <param name="z">The voxel.</param>
    /// <param name="width">The agent's width.</param>
    /// <param name="clipClass">Its clip class.</param>
    /// <param name="blocking">Per dynamic obstacle, whether it is in the way now; empty for none.</param>
    /// <returns>The head room; <see cref="double.PositiveInfinity"/> when unlimited, negative (or <see cref="double.NegativeInfinity"/>) when the width does not fit at all.</returns>
    public double VerticalClearance(int leaf, int z, float width, Nav3dClipClass clipClass, ReadOnlySpan<bool> blocking = default)
    {
        Nav3dLeaf l = Leaf(leaf);
        if (z < l.ZLo || z > l.ZHi)
        {
            return double.NegativeInfinity;
        }

        ReadOnlySpan<byte> record = ClearanceRecord(leaf, clipClass);
        double r = width * 0.5;
        double room = Nav3dClearance.HeadRoom(record, r, VoxelTop(z), blocking);
        int brushes = Nav3dClearance.BrushCount(record);
        if (brushes > 0)
        {
            NavBox box = SweptBox(leaf, z, r, 0);
            for (int i = 0; i < brushes; i++)
            {
                room = Math.Min(room, _brushes[Nav3dClearance.Brush(record, i)].GrowthThreshold(box, NavGrowth.Upward, out _));
            }
        }

        return room;
    }

    /// <summary>The most width an agent of a given height has in a voxel of a leaf: it fits exactly when its width is at most this.</summary>
    /// <param name="leaf">The leaf.</param>
    /// <param name="z">The voxel.</param>
    /// <param name="height">The agent's height.</param>
    /// <param name="clipClass">Its clip class.</param>
    /// <param name="blocking">Per dynamic obstacle, whether it is in the way now; empty for none.</param>
    /// <returns>The width; <see cref="double.PositiveInfinity"/> when unlimited, negative when not even a point fits.</returns>
    public double HorizontalClearance(int leaf, int z, float height, Nav3dClipClass clipClass, ReadOnlySpan<bool> blocking = default)
    {
        Nav3dLeaf l = Leaf(leaf);
        if (z < l.ZLo || z > l.ZHi)
        {
            return double.NegativeInfinity;
        }

        ReadOnlySpan<byte> record = ClearanceRecord(leaf, clipClass);
        double room = Nav3dClearance.WidthRoom(record, height, VoxelTop(z), blocking);
        int brushes = Nav3dClearance.BrushCount(record);
        if (brushes > 0)
        {
            NavBox box = SweptBox(leaf, z, 0, height);
            for (int i = 0; i < brushes; i++)
            {
                room = Math.Min(room, _brushes[Nav3dClearance.Brush(record, i)].GrowthThreshold(box, NavGrowth.Sideways, out _));
            }
        }

        return room * 2;
    }

    /// <summary>The step up from one leaf's floor to another's, for a clip class.</summary>
    /// <param name="from">The leaf stepped from.</param>
    /// <param name="to">The leaf stepped to.</param>
    /// <param name="clipClass">The class.</param>
    /// <returns><c>to</c>'s floor minus <c>from</c>'s; <see cref="float.NaN"/> when either is not grounded.</returns>
    /// <remarks>An agent walks it when it is at most <see cref="StepHeight"/> (and down any drop it can survive); more, and it takes a jump link.</remarks>
    public float StepUp(int from, int to, Nav3dClipClass clipClass)
    {
        Nav3dLeaf a = Leaf(from);
        Nav3dLeaf b = Leaf(to);
        return a.IsGrounded(clipClass) && b.IsGrounded(clipClass) ? b.FloorZ(clipClass) - a.FloorZ(clipClass) : float.NaN;
    }

    /// <summary>A leaf's neighbours: the leaves touching it in its column and overlapping it in the four columns beside it.</summary>
    /// <param name="leaf">The leaf.</param>
    /// <returns>An allocation-free list, east, north, west, south (each low to high), then up, then down.</returns>
    /// <remarks>
    /// Agent-independent: whether an agent can make the step is its fit in
    /// both leaves (<see cref="FitTop"/>). A neighbour in another cell is
    /// reached through a joined door; the kit's walls leave no other opening
    /// between cells.
    /// </remarks>
    public NeighbourList Neighbours(int leaf) =>
        new(_adjacency.AsSpan((int)_adjacencyStart[leaf], (int)(_adjacencyStart[leaf + 1] - _adjacencyStart[leaf])), _leafColumn,
            CellVoxels * CellVoxels, _leafColumn[leaf] / (CellVoxels * CellVoxels));

    /// <summary>The leaf directly below a leaf in its column, touching it, or -1.</summary>
    /// <param name="leaf">The leaf.</param>
    /// <returns>The leaf below, or -1.</returns>
    public int LeafBelow(int leaf)
    {
        if (leaf == 0 || _leafColumn[leaf - 1] != _leafColumn[leaf])
        {
            return -1;
        }

        return Leaf(leaf - 1).ZHi + 1 == Leaf(leaf).ZLo ? leaf - 1 : -1;
    }

    /// <summary>One jump link.</summary>
    /// <param name="jump">The link.</param>
    /// <returns>Its record.</returns>
    public Nav3dJump Jump(int jump)
    {
        ReadOnlySpan<byte> r = Record(_s.Jumps, jump, Nav3dFormat.JumpRecordBytes);
        return new Nav3dJump(
            BinaryPrimitives.ReadUInt32LittleEndian(r),
            BinaryPrimitives.ReadUInt32LittleEndian(r[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(r[8..]),
            r[12],
            (Nav3dDirection)r[13],
            r[14]);
    }

    /// <summary>The jump links a leaf takes part in, at either end.</summary>
    /// <param name="leaf">The leaf.</param>
    /// <returns>The links' indexes, ascending; allocation-free.</returns>
    public ReadOnlySpan<int> Jumps(int leaf) => _jumpIndex.AsSpan((int)_jumpStart[leaf], (int)(_jumpStart[leaf + 1] - _jumpStart[leaf]));

    /// <summary>How many connected components a preset's leaves form, over the grid as stored (every obstacle open).</summary>
    /// <param name="preset">The preset.</param>
    /// <returns>The count.</returns>
    public int ComponentCount(int preset) => _componentCounts[preset];

    /// <summary>The connected component of a leaf for a preset.</summary>
    /// <param name="preset">The preset.</param>
    /// <param name="leaf">The leaf.</param>
    /// <returns>The component, numbered in order of first leaf, or -1 when the preset fits nowhere in the leaf.</returns>
    /// <remarks>
    /// Two leaves are in one component when the preset can move between them
    /// by face steps through voxels it fits in (walking, climbing or flying:
    /// the movement rule is the caller's). Derived at load.
    /// </remarks>
    public int Component(int preset, int leaf) => _components[preset][leaf];

    /// <summary>How many dynamic obstacles name a leaf: the leaves whose clearance can change when it moves.</summary>
    /// <param name="obstacle">The obstacle.</param>
    /// <returns>The leaves, ascending; allocation-free.</returns>
    public ReadOnlySpan<int> ObstacleLeaves(int obstacle) =>
        _obstacleLeaves.AsSpan((int)_obstacleStart[obstacle], (int)(_obstacleStart[obstacle + 1] - _obstacleStart[obstacle]));

    /// <summary>One dynamic obstacle.</summary>
    /// <param name="obstacle">The obstacle.</param>
    /// <returns>Its record.</returns>
    public Nav3dObstacle Obstacle(int obstacle)
    {
        ReadOnlySpan<byte> r = Record(_s.Obstacles, obstacle, Nav3dFormat.ObstacleRecordBytes);
        return new Nav3dObstacle(
            String(BinaryPrimitives.ReadUInt32LittleEndian(r)),
            String(BinaryPrimitives.ReadUInt32LittleEndian(r[4..])) ?? string.Empty,
            BinaryPrimitives.ReadUInt32LittleEndian(r[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(r[12..]),
            (Nav3dObstacleKind)r[16],
            new Vec3(BinaryPrimitives.ReadSingleLittleEndian(r[20..]), BinaryPrimitives.ReadSingleLittleEndian(r[24..]), BinaryPrimitives.ReadSingleLittleEndian(r[28..])),
            new Vec3(BinaryPrimitives.ReadSingleLittleEndian(r[32..]), BinaryPrimitives.ReadSingleLittleEndian(r[36..]), BinaryPrimitives.ReadSingleLittleEndian(r[40..])));
    }

    /// <summary>A point of interest's position, without reading its strings.</summary>
    /// <param name="poi">The point.</param>
    /// <returns>Its position in level coordinates.</returns>
    public Vec3 PoiPosition(int poi)
    {
        ReadOnlySpan<byte> r = Record(_s.Pois, poi, Nav3dFormat.PoiRecordBytes);
        return new Vec3(BinaryPrimitives.ReadSingleLittleEndian(r), BinaryPrimitives.ReadSingleLittleEndian(r[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(r[8..]));
    }

    /// <summary>A point of interest's yaw in degrees.</summary>
    /// <param name="poi">The point.</param>
    /// <returns>The yaw.</returns>
    public float PoiYaw(int poi) => BinaryPrimitives.ReadSingleLittleEndian(Record(_s.Pois, poi, Nav3dFormat.PoiRecordBytes)[12..]);

    /// <summary>A point of interest's flags.</summary>
    /// <param name="poi">The point.</param>
    /// <returns>The flags.</returns>
    public Nav3dPoiFlags PoiFlags(int poi) =>
        (Nav3dPoiFlags)BinaryPrimitives.ReadUInt16LittleEndian(Record(_s.Pois, poi, Nav3dFormat.PoiRecordBytes)[44..]);

    /// <summary>A point of interest's type, as UTF-8 bytes (no allocation).</summary>
    /// <param name="poi">The point.</param>
    /// <returns>The type's bytes, without the NUL.</returns>
    public ReadOnlySpan<byte> PoiTypeUtf8(int poi) =>
        StringUtf8(BinaryPrimitives.ReadUInt32LittleEndian(Record(_s.Pois, poi, Nav3dFormat.PoiRecordBytes)[20..]));

    /// <summary>A point of interest's leaf: the leaf holding its position (derived at load).</summary>
    /// <param name="poi">The point.</param>
    /// <returns>The leaf, or -1 when the point is in solid (a capped doorway's door point).</returns>
    public int PoiLeaf(int poi) => _poiLeaf[poi];

    /// <summary>A whole point of interest, strings and all.</summary>
    /// <param name="poi">The point.</param>
    /// <returns>The point.</returns>
    public Nav3dPoi Poi(int poi)
    {
        ReadOnlySpan<byte> r = Record(_s.Pois, poi, Nav3dFormat.PoiRecordBytes);
        return new Nav3dPoi(
            PoiPosition(poi),
            BinaryPrimitives.ReadSingleLittleEndian(r[12..]),
            BinaryPrimitives.ReadSingleLittleEndian(r[16..]),
            String(BinaryPrimitives.ReadUInt32LittleEndian(r[20..])) ?? string.Empty,
            String(BinaryPrimitives.ReadUInt32LittleEndian(r[24..])) ?? string.Empty,
            String(BinaryPrimitives.ReadUInt32LittleEndian(r[28..])),
            BinaryPrimitives.ReadUInt32LittleEndian(r[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(r[36..]),
            BinaryPrimitives.ReadInt32LittleEndian(r[40..]),
            (Nav3dPoiFlags)BinaryPrimitives.ReadUInt16LittleEndian(r[44..]),
            (Nav3dRoomRole)r[46]);
    }

    /// <summary>
    /// Where a player spawns on a fresh start, and which way it faces: the
    /// point <see cref="SpawnPoi"/> names.
    /// </summary>
    /// <param name="position">The spawn position (the player's origin: its feet), in level coordinates.</param>
    /// <param name="yaw">The facing, degrees counter-clockwise from +x.</param>
    /// <returns>False when the level names no spawn point.</returns>
    public bool TryGetSpawn(out Vec3 position, out float yaw)
    {
        if (SpawnPoi < 0)
        {
            position = default;
            yaw = 0;
            return false;
        }

        position = PoiPosition(SpawnPoi);
        yaw = PoiYaw(SpawnPoi);
        return true;
    }

    /// <summary>One grid cell.</summary>
    /// <param name="cell">The cell, <c>row × columns + column</c>.</param>
    /// <returns>Its record.</returns>
    public Nav3dCell Cell(int cell)
    {
        ReadOnlySpan<byte> r = Record(_s.Cells, cell, Nav3dFormat.CellRecordBytes);
        return new Nav3dCell(String(BinaryPrimitives.ReadUInt32LittleEndian(r)), r[4], (Nav3dRoomRole)r[5], r[6], r[7]);
    }

    /// <summary>One door record.</summary>
    /// <param name="door">The door.</param>
    /// <returns>Its record.</returns>
    public Nav3dDoor Door(int door)
    {
        ReadOnlySpan<byte> r = Record(_s.Doors, door, Nav3dFormat.DoorRecordBytes);
        return new Nav3dDoor(
            BinaryPrimitives.ReadUInt32LittleEndian(r),
            r[4],
            r[5] != 0,
            String(BinaryPrimitives.ReadUInt32LittleEndian(r[8..])) ?? string.Empty,
            BinaryPrimitives.ReadInt32LittleEndian(r[12..]));
    }

    /// <summary>A string of the string table, as UTF-8 bytes without its NUL.</summary>
    /// <param name="offset">The string's offset; <see cref="Nav3dFormat.NoString"/> reads as empty.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="InvalidDataException">The offset is outside the table.</exception>
    public ReadOnlySpan<byte> StringUtf8(uint offset)
    {
        if (offset == Nav3dFormat.NoString)
        {
            return [];
        }

        ReadOnlySpan<byte> table = Slice(_s.Strings);
        if (offset >= (uint)table.Length)
        {
            throw new InvalidDataException($"string offset {offset} is outside the {table.Length}-byte string table.");
        }

        ReadOnlySpan<byte> rest = table[(int)offset..];
        return rest[..rest.IndexOf((byte)0)];
    }

    /// <summary>A string of the string table.</summary>
    /// <param name="offset">The string's offset.</param>
    /// <returns>The string, or null for <see cref="Nav3dFormat.NoString"/>.</returns>
    public string? String(uint offset) => offset == Nav3dFormat.NoString ? null : Encoding.UTF8.GetString(StringUtf8(offset));

    /// <summary>Reads the whole file into a <see cref="Nav3dLevel"/>: for tools and round-trip checks, not per-frame use.</summary>
    /// <returns>The level.</returns>
    public Nav3dLevel ToLevel()
    {
        ReadOnlySpan<byte> planes = Slice(_s.BrushPlanes);
        List<float[]> brushes = [];
        for (int b = 0; b < _brushes.Length; b++)
        {
            int first = (int)ReadU32(_s.BrushIndex, b, 4, 0);
            int end = (int)ReadU32(_s.BrushIndex, b + 1, 4, 0);
            float[] floats = new float[(end - first) * 4];
            for (int i = 0; i < floats.Length; i++)
            {
                floats[i] = BinaryPrimitives.ReadSingleLittleEndian(planes[(((first * 4) + i) * 4)..]);
            }

            brushes.Add(floats);
        }

        int columnCount = _s.ColumnStarts.Length / 4;
        return new Nav3dLevel
        {
            CellSize = CellSize,
            VoxelSize = VoxelSize,
            CellVoxels = CellVoxels,
            Columns = Columns,
            Rows = Rows,
            Origin = Origin,
            FloorNormalZ = FloorNormalZ,
            StepHeight = StepHeight,
            JumpHeight = JumpHeight,
            JumpDistance = JumpDistance,
            Cells = [.. Enumerable.Range(0, CellCount).Select(Cell)],
            Doors = [.. Enumerable.Range(0, DoorCount).Select(Door)],
            Pois = [.. Enumerable.Range(0, PoiCount).Select(Poi)],
            Presets = [.. Enumerable.Range(0, PresetCount).Select(Preset)],
            Roots = [.. Enumerable.Range(0, CellCount).Select(CellRoot)],
            ColumnStarts = [.. Enumerable.Range(0, columnCount).Select(j => ReadU32(_s.ColumnStarts, j, 4, 0))],
            Leaves = [.. Enumerable.Range(0, LeafCount).Select(Leaf)],
            Clearance = Slice(_s.Clearance).ToArray(),
            Obstacles = [.. Enumerable.Range(0, ObstacleCount).Select(Obstacle)],
            Brushes = brushes,
            Jumps = [.. Enumerable.Range(0, JumpCount).Select(Jump)],
            SpawnPoi = SpawnPoi,
            UpArrivalPoi = UpArrivalPoi,
            DownArrivalPoi = DownArrivalPoi,
            LevelId = LevelId,
            PackId = PackId,
        };
    }

    /// <summary>
    /// The cell and in-cell voxel holding a level point, or false when the
    /// point is outside the grid. Voxels are half-open, low side in.
    /// </summary>
    /// <param name="point">The point.</param>
    /// <param name="column">The cell's column.</param>
    /// <param name="row">The cell's row.</param>
    /// <param name="x">The voxel within the cell, along x.</param>
    /// <param name="y">Along y.</param>
    /// <param name="z">Along z.</param>
    /// <returns>Whether the point is in the grid.</returns>
    public bool TryVoxel(Vec3 point, out int column, out int row, out int x, out int y, out int z)
    {
        column = row = x = y = z = 0;
        double lx = point.X - Origin.X;
        double ly = point.Y - Origin.Y;
        double lz = point.Z - Origin.Z;
        if (!(lx >= 0 && ly >= 0 && lz >= 0))
        {
            return false;
        }

        long gx = (long)Math.Floor(lx / VoxelSize);
        long gy = (long)Math.Floor(ly / VoxelSize);
        long gz = (long)Math.Floor(lz / VoxelSize);
        if (gx >= (long)Columns * CellVoxels || gy >= (long)Rows * CellVoxels || gz >= CellVoxels)
        {
            return false;
        }

        column = (int)(gx / CellVoxels);
        row = (int)(gy / CellVoxels);
        x = (int)(gx % CellVoxels);
        y = (int)(gy % CellVoxels);
        z = (int)gz;
        return true;
    }

    private static Section Require(Dictionary<string, Section> sections, string tag, int record, int count)
    {
        if (!sections.TryGetValue(tag, out Section section))
        {
            throw new InvalidDataException($"the .nav3d file has no \"{tag}\" section.");
        }

        if (section.Length % record != 0 || (count >= 0 && section.Length != (long)count * record) || (count < 0 && record == 1 && section.Length == 0))
        {
            throw new InvalidDataException($"the .nav3d \"{tag}\" section is {section.Length} bytes, which its counts do not allow.");
        }

        return section;
    }

    private static void ValidateGrid(ReadOnlySpan<byte> b, Sections s, Header header, int cells)
    {
        int block = header.CellVoxels * header.CellVoxels;
        int columns = header.ColumnCount;
        bool[] used = new bool[(columns / block) + 1];
        int placed = 0;
        for (int c = 0; c < cells; c++)
        {
            int root = BinaryPrimitives.ReadInt32LittleEndian(b[(s.Roots.Offset + (c * 4))..]);
            if (root == -1)
            {
                continue;
            }

            if (root < 0 || root % block != 0 || (long)root + block > columns || used[root / block])
            {
                throw new InvalidDataException($"cell {c} has root {root}, which is not its own block of {block} of the {columns} columns.");
            }

            used[root / block] = true;
            placed++;
        }

        if ((long)placed * block != columns)
        {
            throw new InvalidDataException($"{placed} placed cells own {(long)placed * block} columns, but the file has {columns}.");
        }

        uint previous = 0;
        for (int j = 0; j <= columns; j++)
        {
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(b[(s.ColumnStarts.Offset + (j * 4))..]);
            if (start < previous || start > header.LeafCount || (j == 0 && start != 0) || (j == columns && start != header.LeafCount))
            {
                throw new InvalidDataException($"the column starts are not a partition of the {header.LeafCount} leaves.");
            }

            if (j > 0)
            {
                int last = -1;
                for (uint l = previous; l < start; l++)
                {
                    ReadOnlySpan<byte> r = b.Slice(s.Leaves.Offset + ((int)l * Nav3dFormat.LeafRecordBytes), Nav3dFormat.LeafRecordBytes);
                    if (r[1] < 1 || r[0] <= last || r[0] + r[1] > header.CellVoxels)
                    {
                        throw new InvalidDataException($"leaf {l} runs from voxel {r[0]} for {r[1]}, overlapping its column's last or leaving its cell.");
                    }

                    last = r[0] + r[1] - 1;
                }
            }

            previous = start;
        }
    }

    private static NavBrush[] ValidateBrushes(ReadOnlySpan<byte> b, Sections s, int count)
    {
        NavBrush[] brushes = new NavBrush[count];
        int planes = s.BrushPlanes.Length / 16;
        uint previous = 0;
        for (int i = 0; i <= count; i++)
        {
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(b[(s.BrushIndex.Offset + (i * 4))..]);
            if (start > planes || (i == 0 && start != 0) || (i == count && start != planes) || (i > 0 && start < previous + 4))
            {
                throw new InvalidDataException($"the brush plane starts are not a partition of the {planes} planes into brushes of four or more.");
            }

            if (i > 0)
            {
                float[] floats = new float[(start - previous) * 4];
                for (int f = 0; f < floats.Length; f++)
                {
                    floats[f] = BinaryPrimitives.ReadSingleLittleEndian(b[(s.BrushPlanes.Offset + (((int)previous * 16) + (f * 4)))..]);
                }

                brushes[i - 1] = NavBrush.FromPlaneFloats(floats, 1)
                    ?? throw new InvalidDataException($"brush {i - 1}'s planes bound no volume.");
            }

            previous = start;
        }

        return brushes;
    }

    private static void ValidateRecords(ReadOnlySpan<byte> b, Sections s, Header header, int cells)
    {
        ReadOnlySpan<byte> clearance = b.Slice(s.Clearance.Offset, s.Clearance.Length);
        for (int l = 0; l < header.LeafCount; l++)
        {
            ReadOnlySpan<byte> r = b.Slice(s.Leaves.Offset + (l * Nav3dFormat.LeafRecordBytes), Nav3dFormat.LeafRecordBytes);
            for (int at = 8; at <= 12; at += 4)
            {
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(r[at..]);
                if (offset % 4 != 0 || offset >= clearance.Length
                    || Nav3dClearance.Problem(clearance[(int)offset..], header.ObstacleCount, header.BrushCount) is { } problem)
                {
                    throw new InvalidDataException($"leaf {l}'s clearance record at {offset} is out of range or malformed.");
                }

                if (Nav3dClearance.BrushCount(clearance[(int)offset..]) > 0 && r[1] != 1)
                {
                    throw new InvalidDataException($"leaf {l} lists an overhanging brush but runs {r[1]} voxels; such a leaf is one voxel.");
                }
            }
        }

        for (int o = 0; o < header.ObstacleCount; o++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(b[(s.Obstacles.Offset + (o * Nav3dFormat.ObstacleRecordBytes) + 8)..]) >= cells)
            {
                throw new InvalidDataException($"obstacle {o} names a cell out of range.");
            }
        }

        for (int j = 0; j < header.JumpCount; j++)
        {
            ReadOnlySpan<byte> r = b.Slice(s.Jumps.Offset + (j * Nav3dFormat.JumpRecordBytes), Nav3dFormat.JumpRecordBytes);
            uint a = BinaryPrimitives.ReadUInt32LittleEndian(r);
            uint c = BinaryPrimitives.ReadUInt32LittleEndian(r[4..]);
            if (a >= c || c >= header.LeafCount || r[12] is 0 or > 3 || r[13] > 3)
            {
                throw new InvalidDataException($"jump link {j} names leaves {a} and {c} of {header.LeafCount}, or an unknown class or direction.");
            }
        }

        for (int p = 0; p < header.PoiCount; p++)
        {
            ReadOnlySpan<byte> r = b.Slice(s.Pois.Offset + (p * Nav3dFormat.PoiRecordBytes), Nav3dFormat.PoiRecordBytes);
            int door = BinaryPrimitives.ReadInt32LittleEndian(r[40..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(r[32..]) >= cells || door < -1 || door >= header.DoorCount)
            {
                throw new InvalidDataException($"point of interest {p} names a cell or door out of range.");
            }
        }

        for (int d = 0; d < header.DoorCount; d++)
        {
            ReadOnlySpan<byte> r = b.Slice(s.Doors.Offset + (d * Nav3dFormat.DoorRecordBytes), Nav3dFormat.DoorRecordBytes);
            int other = BinaryPrimitives.ReadInt32LittleEndian(r[12..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(r) >= cells || r[4] > 3 || other < -1 || other >= header.DoorCount)
            {
                throw new InvalidDataException($"door {d} names a cell, direction or facing door out of range.");
            }
        }
    }

    private (int[] LeafColumn, int[] BlockCell) DeriveColumns()
    {
        int block = CellVoxels * CellVoxels;
        int columns = (_s.ColumnStarts.Length / 4) - 1;
        int[] blockCell = new int[columns / block];
        for (int c = 0; c < CellCount; c++)
        {
            int root = CellRoot(c);
            if (root >= 0)
            {
                blockCell[root / block] = c;
            }
        }

        int[] leafColumn = new int[LeafCount];
        for (int j = 0; j < columns; j++)
        {
            int end = (int)ReadU32(_s.ColumnStarts, j + 1, 4, 0);
            for (int l = (int)ReadU32(_s.ColumnStarts, j, 4, 0); l < end; l++)
            {
                leafColumn[l] = j;
            }
        }

        return (leafColumn, blockCell);
    }

    /// <summary>
    /// Every leaf's neighbours, derived from the columns: for each leaf in
    /// leaf order, the overlapping leaves of the columns east, north, west and
    /// south of it (across a cell face into the next placed cell), then the
    /// touching leaf above, then below. Entries are <c>leaf &lt;&lt; 3 | direction</c>.
    /// </summary>
    private (uint[] Start, uint[] Entries) DeriveAdjacency()
    {
        int n = CellVoxels;
        uint[] start = new uint[LeafCount + 1];
        List<uint> entries = new(LeafCount * 6);
        for (int leaf = 0; leaf < LeafCount; leaf++)
        {
            start[leaf] = (uint)entries.Count;
            (int cell, int x, int y) = LeafColumn(leaf);
            Nav3dLeaf here = Leaf(leaf);
            for (int d = 0; d < 4; d++)
            {
                (int dx, int dy) = d switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };
                int nx = x + dx;
                int ny = y + dy;
                int ncell = cell;
                if (nx < 0 || nx >= n || ny < 0 || ny >= n)
                {
                    int cx = (cell % Columns) + dx;
                    int cy = (cell / Columns) + dy;
                    if (cx < 0 || cy < 0 || cx >= Columns || cy >= Rows)
                    {
                        continue;
                    }

                    ncell = (cy * Columns) + cx;
                    nx = (nx + n) % n;
                    ny = (ny + n) % n;
                }

                int root = CellRoot(ncell);
                if (root < 0)
                {
                    continue;
                }

                int column = root + (ny * n) + nx;
                int end = (int)ReadU32(_s.ColumnStarts, column + 1, 4, 0);
                for (int other = (int)ReadU32(_s.ColumnStarts, column, 4, 0); other < end; other++)
                {
                    Nav3dLeaf there = Leaf(other);
                    if (there.ZLo <= here.ZHi && there.ZHi >= here.ZLo)
                    {
                        entries.Add(((uint)other << 3) | (uint)d);
                    }
                }
            }

            if (leaf + 1 < LeafCount && _leafColumn[leaf + 1] == _leafColumn[leaf] && Leaf(leaf + 1).ZLo == here.ZHi + 1)
            {
                entries.Add(((uint)(leaf + 1) << 3) | (uint)Nav3dDirection.Up);
            }

            if (LeafBelow(leaf) is int below and >= 0)
            {
                entries.Add(((uint)below << 3) | (uint)Nav3dDirection.Down);
            }
        }

        start[LeafCount] = (uint)entries.Count;
        return (start, [.. entries]);
    }

    private (uint[] Start, int[] Index) DeriveJumps()
    {
        uint[] start = new uint[LeafCount + 1];
        for (int j = 0; j < JumpCount; j++)
        {
            Nav3dJump jump = Jump(j);
            start[jump.LeafA + 1]++;
            start[jump.LeafB + 1]++;
        }

        for (int l = 0; l < LeafCount; l++)
        {
            start[l + 1] += start[l];
        }

        int[] index = new int[start[LeafCount]];
        uint[] fill = new uint[LeafCount];
        for (int j = 0; j < JumpCount; j++)
        {
            Nav3dJump jump = Jump(j);
            index[start[jump.LeafA] + fill[jump.LeafA]++] = j;
            index[start[jump.LeafB] + fill[jump.LeafB]++] = j;
        }

        return (start, index);
    }

    private (uint[] Start, int[] Leaves) DeriveObstacleLeaves()
    {
        List<(int Obstacle, int Leaf)> pairs = [];
        for (int l = 0; l < LeafCount; l++)
        {
            int before = pairs.Count;
            foreach (Nav3dClipClass clipClass in new[] { Nav3dClipClass.Player, Nav3dClipClass.Npc })
            {
                ReadOnlySpan<byte> record = ClearanceRecord(l, clipClass);
                for (int i = 0; i < Nav3dClearance.DynamicCount(record); i++)
                {
                    int obstacle = Nav3dClearance.Dynamic(record, i).Obstacle;
                    bool seen = false;
                    for (int k = before; k < pairs.Count; k++)
                    {
                        seen |= pairs[k].Obstacle == obstacle;
                    }

                    if (!seen)
                    {
                        pairs.Add((obstacle, l));
                    }
                }
            }
        }

        uint[] start = new uint[ObstacleCount + 1];
        foreach ((int obstacle, _) in pairs)
        {
            start[obstacle + 1]++;
        }

        for (int o = 0; o < ObstacleCount; o++)
        {
            start[o + 1] += start[o];
        }

        int[] leaves = new int[pairs.Count];
        uint[] fill = new uint[ObstacleCount];
        foreach ((int obstacle, int leaf) in pairs)
        {
            leaves[start[obstacle] + fill[obstacle]++] = leaf;
        }

        return (start, leaves);
    }

    /// <summary>
    /// A preset's connected components over the grid as stored: each leaf's
    /// fitting run (from its bottom up to <see cref="FitTop"/>), joined to a
    /// neighbour's when the two share a face: overlapping runs side by side,
    /// or, in one column, a run that fits to its top under one that fits from
    /// its bottom.
    /// </summary>
    private (int[] Component, int Count) DeriveComponents(int preset)
    {
        Nav3dPreset p = Preset(preset);
        int[] top = new int[LeafCount];
        for (int l = 0; l < LeafCount; l++)
        {
            top[l] = FitTop(l, p.Width, p.Height, p.ClipClass);
        }

        int[] parent = new int[LeafCount];
        for (int l = 0; l < LeafCount; l++)
        {
            parent[l] = l;
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        for (int l = 0; l < LeafCount; l++)
        {
            if (top[l] < 0)
            {
                continue;
            }

            int lo = Leaf(l).ZLo;
            foreach (uint entry in _adjacency.AsSpan((int)_adjacencyStart[l], (int)(_adjacencyStart[l + 1] - _adjacencyStart[l])))
            {
                int other = (int)(entry >> 3);
                if (other < l || top[other] < 0)
                {
                    continue;
                }

                Nav3dDirection direction = (Nav3dDirection)(entry & 7);
                int otherLo = Leaf(other).ZLo;
                bool joined = direction switch
                {
                    Nav3dDirection.Up => top[l] == Leaf(l).ZHi,
                    Nav3dDirection.Down => top[other] == Leaf(other).ZHi,
                    _ => lo <= top[other] && otherLo <= top[l],
                };
                if (joined)
                {
                    int ra = Find(l);
                    int rb = Find(other);
                    if (ra != rb)
                    {
                        parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
                    }
                }
            }
        }

        // A root is its component's smallest leaf, so walking leaves in order
        // meets each component first at its root.
        int[] component = new int[LeafCount];
        int count = 0;
        for (int l = 0; l < LeafCount; l++)
        {
            if (top[l] < 0)
            {
                component[l] = -1;
                continue;
            }

            int root = Find(l);
            component[l] = root == l ? count++ : component[root];
        }

        return (component, count);
    }

    private double VoxelTop(int z) => Origin.Z + ((z + 1) * (double)VoxelSize);

    /// <summary>A leaf's voxel swept by an agent's box: its footprint grown by the half-width, from its bottom to its top plus the height.</summary>
    private NavBox SweptBox(int leaf, int z, double halfWidth, double height)
    {
        (int cell, int x, int y) = LeafColumn(leaf);
        double s = VoxelSize;
        double x0 = Origin.X + ((((cell % Columns) * CellVoxels) + x) * s);
        double y0 = Origin.Y + ((((cell / Columns) * CellVoxels) + y) * s);
        double z0 = Origin.Z + (z * s);
        return new NavBox(x0 - halfWidth, y0 - halfWidth, z0, x0 + s + halfWidth, y0 + s + halfWidth, VoxelTop(z) + height);
    }

    private ReadOnlySpan<byte> Slice(Section section) => Bytes.Slice(section.Offset, section.Length);

    private ReadOnlySpan<byte> Record(Section section, int index, int size)
    {
        if ((uint)index >= (uint)(section.Length / size))
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "no such record.");
        }

        return Bytes.Slice(section.Offset + (index * size), size);
    }

    private uint ReadU32(Section section, int index, int size, int at) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Record(section, index, size)[at..]);

    /// <summary>An allocation-free list of a leaf's neighbours.</summary>
    public readonly ref struct NeighbourList
    {
        private readonly ReadOnlySpan<uint> _entries;
        private readonly int[] _leafColumn;
        private readonly int _block;
        private readonly int _originBlock;

        internal NeighbourList(ReadOnlySpan<uint> entries, int[] leafColumn, int block, int originBlock)
        {
            _entries = entries;
            _leafColumn = leafColumn;
            _block = block;
            _originBlock = originBlock;
        }

        /// <summary>How many neighbours.</summary>
        public int Count => _entries.Length;

        /// <summary>One neighbour.</summary>
        /// <param name="index">Which, 0 to <see cref="Count"/> - 1.</param>
        /// <returns>The neighbour.</returns>
        public Nav3dNeighbour this[int index] => Decode(_entries[index]);

        /// <summary>The enumerator, for <c>foreach</c>.</summary>
        /// <returns>The enumerator.</returns>
        public Enumerator GetEnumerator() => new(this);

        private Nav3dNeighbour Decode(uint entry)
        {
            int leaf = (int)(entry >> 3);
            Nav3dDirection direction = (Nav3dDirection)(entry & 7);
            return new Nav3dNeighbour(leaf, direction, _leafColumn[leaf] / _block != _originBlock);
        }

        /// <summary>Walks a <see cref="NeighbourList"/>.</summary>
        public ref struct Enumerator
        {
            private readonly NeighbourList _list;
            private int _at;

            internal Enumerator(NeighbourList list)
            {
                _list = list;
                _at = -1;
            }

            /// <summary>The current neighbour.</summary>
            public readonly Nav3dNeighbour Current => _list[_at];

            /// <summary>Moves to the next neighbour.</summary>
            /// <returns>False past the last.</returns>
            public bool MoveNext() => ++_at < _list.Count;
        }
    }

    private readonly record struct Section(int Offset, int Length);

    private readonly record struct Sections
    {
        public Section Strings { get; init; }

        public Section Presets { get; init; }

        public Section Cells { get; init; }

        public Section Doors { get; init; }

        public Section Pois { get; init; }

        public Section Roots { get; init; }

        public Section ColumnStarts { get; init; }

        public Section Leaves { get; init; }

        public Section Clearance { get; init; }

        public Section Obstacles { get; init; }

        public Section BrushIndex { get; init; }

        public Section BrushPlanes { get; init; }

        public Section Jumps { get; init; }
    }

    private readonly record struct Header
    {
        public int Version { get; init; }

        public NavCodec Codec { get; init; }

        public int HeaderBytes { get; init; }

        public int SectionCount { get; init; }

        public int PresetCount { get; init; }

        public float CellSize { get; init; }

        public float VoxelSize { get; init; }

        public int CellVoxels { get; init; }

        public int Columns { get; init; }

        public int Rows { get; init; }

        public Vec3 Origin { get; init; }

        public float FloorNormalZ { get; init; }

        public float StepHeight { get; init; }

        public float JumpHeight { get; init; }

        public float JumpDistance { get; init; }

        public int PoiCount { get; init; }

        public int DoorCount { get; init; }

        public int Spawn { get; init; }

        public int Up { get; init; }

        public int Down { get; init; }

        public int ColumnCount { get; init; }

        public int LeafCount { get; init; }

        public int ObstacleCount { get; init; }

        public int BrushCount { get; init; }

        public int JumpCount { get; init; }

        public int ClearanceBytes { get; init; }

        public Guid LevelId { get; init; }

        public Guid PackId { get; init; }
    }
}
