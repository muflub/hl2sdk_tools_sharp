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
/// <param name="ThroughDoor">Whether the step crosses a door between two rooms.</param>
public readonly record struct Nav3dNeighbour(int Leaf, bool ThroughDoor);

/// <summary>
/// Reads a <c>.nav3d</c> file in place: every query reads the file's bytes
/// directly, so a game can keep one buffer per level and ask it questions
/// at run time without building an object graph.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why in place.</b> The game mod is written in C# and references this
/// assembly directly, and its AI asks "which leaf is this point in" and
/// "what are this leaf's neighbours" many times a frame. So the reader
/// validates the file once, in <see cref="Open"/>, remembers where each
/// section is, and after that answers from the bytes: a leaf is a
/// sixteen-byte record read with <see cref="BinaryPrimitives"/>, a point
/// lookup is a walk of at most the octree's depth, and a leaf's neighbours
/// come from a <c>ref struct</c> enumerator over the adjacency section. None
/// of those allocates. The calls that return strings or whole records
/// (<see cref="Poi"/>, <see cref="Cell"/>, <see cref="ToLevel"/>) do, and are
/// meant for loading and tools, not per-frame use.
/// </para>
/// <para>
/// <b>Validated up front.</b> <see cref="Open"/> checks the header, the
/// directory, every section's size against the counts, every node's child
/// or leaf index, every adjacency entry and every root, so a query on an
/// opened file never indexes outside a section and never loops: a point
/// lookup descends at most the header's octree depth.
/// </para>
/// </remarks>
public sealed class Nav3dReader
{
    private readonly ReadOnlyMemory<byte> _file;
    private readonly Section _strings;
    private readonly Section _agents;
    private readonly Section _cells;
    private readonly Section _doors;
    private readonly Section _pois;
    private readonly AgentSections[] _agentSections;

    private Nav3dReader(ReadOnlyMemory<byte> file, Header header, Section strings, Section agents, Section cells,
        Section doors, Section pois, AgentSections[] agentSections)
    {
        _file = file;
        _strings = strings;
        _agents = agents;
        _cells = cells;
        _doors = doors;
        _pois = pois;
        _agentSections = agentSections;
        Version = header.Version;
        Codec = header.Codec;
        CellSize = header.CellSize;
        VoxelSize = header.VoxelSize;
        CellVoxels = header.CellVoxels;
        OctreeDepth = header.Depth;
        Columns = header.Columns;
        Rows = header.Rows;
        Origin = header.Origin;
        FloorNormalZ = header.FloorNormalZ;
        PoiCount = header.PoiCount;
        DoorCount = header.DoorCount;
        SpawnPoi = header.Spawn;
        UpArrivalPoi = header.Up;
        DownArrivalPoi = header.Down;
        LevelId = header.LevelId;
        PackId = header.PackId;
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

    /// <summary>Each cell root's depth: its cube is <c>2^OctreeDepth</c> voxels on a side.</summary>
    public int OctreeDepth { get; }

    /// <summary>The grid's columns, west to east.</summary>
    public int Columns { get; }

    /// <summary>The grid's rows, south to north.</summary>
    public int Rows { get; }

    /// <summary>The grid's low corner in level coordinates.</summary>
    public Vec3 Origin { get; }

    /// <summary>The least normal z of a floor.</summary>
    public float FloorNormalZ { get; }

    /// <summary>How many agents the file describes.</summary>
    public int AgentCount => _agentSections.Length;

    /// <summary>How many cells the grid has.</summary>
    public int CellCount => Columns * Rows;

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

    /// <summary>Whether this navigation belongs to a map: its level id equals the map's <c>ss_level_id</c> worldspawn value.</summary>
    /// <param name="mapLevelId">The map's <c>ss_level_id</c>, as the game reads it from the worldspawn.</param>
    /// <returns>True when both ids parse and are equal, and are not empty.</returns>
    public bool MatchesMap(string? mapLevelId) =>
        LevelId != Guid.Empty && Guid.TryParse(mapLevelId, out Guid id) && id == LevelId;

    private ReadOnlySpan<byte> Bytes => _file.Span;

    /// <summary>Opens a file held in memory, validating all of it.</summary>
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
            AgentCount = BinaryPrimitives.ReadInt32LittleEndian(b[8..]),
            CellSize = BinaryPrimitives.ReadSingleLittleEndian(b[12..]),
            VoxelSize = BinaryPrimitives.ReadSingleLittleEndian(b[16..]),
            CellVoxels = BinaryPrimitives.ReadInt32LittleEndian(b[20..]),
            Depth = BinaryPrimitives.ReadInt32LittleEndian(b[24..]),
            Columns = BinaryPrimitives.ReadInt32LittleEndian(b[28..]),
            Rows = BinaryPrimitives.ReadInt32LittleEndian(b[32..]),
            Origin = new Vec3(
                BinaryPrimitives.ReadSingleLittleEndian(b[36..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[40..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[44..])),
            FloorNormalZ = BinaryPrimitives.ReadSingleLittleEndian(b[48..]),
            PoiCount = BinaryPrimitives.ReadInt32LittleEndian(b[52..]),
            DoorCount = BinaryPrimitives.ReadInt32LittleEndian(b[56..]),
            Spawn = BinaryPrimitives.ReadInt32LittleEndian(b[60..]),
            Up = BinaryPrimitives.ReadInt32LittleEndian(b[64..]),
            Down = BinaryPrimitives.ReadInt32LittleEndian(b[68..]),
            LevelId = new Guid(b.Slice(72, 16), bigEndian: true),
            PackId = new Guid(b.Slice(88, 16), bigEndian: true),
        };

        if (header.HeaderBytes < Nav3dFormat.HeaderBytes || header.SectionCount < 0
            || header.AgentCount is < 0 or > Nav3dFormat.MaxAgents)
        {
            throw new InvalidDataException("the .nav3d header's sizes are out of range.");
        }

        if (!(header.CellSize > 0) || !(header.VoxelSize > 0) || header.CellVoxels < 1 || header.Columns < 1 || header.Rows < 1
            || header.Depth != Nav3dFormat.DepthFor(header.CellVoxels) || header.Depth > 16
            || (long)header.Columns * header.Rows > int.MaxValue || header.PoiCount < 0 || header.DoorCount < 0)
        {
            throw new InvalidDataException("the .nav3d header describes no valid grid.");
        }

        long directoryEnd = header.HeaderBytes + ((long)header.SectionCount * Nav3dFormat.DirectoryEntryBytes);
        if (directoryEnd > b.Length)
        {
            throw new InvalidDataException("the .nav3d section directory runs past the end of the file.");
        }

        Dictionary<(string, uint), Section> sections = [];
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

            if (!sections.TryAdd((tag, index), new Section((int)offset, (int)length)))
            {
                throw new InvalidDataException($"the .nav3d file has two \"{tag}\" sections for index {index}.");
            }
        }

        int cells = header.Columns * header.Rows;
        Section strings = Require(sections, Nav3dFormat.StringsTag, Nav3dFormat.LevelIndex, 1, -1);
        if (b[strings.Offset] != 0 || b[strings.Offset + strings.Length - 1] != 0)
        {
            throw new InvalidDataException("the .nav3d string table must start with the empty string and end with a NUL.");
        }

        Section agents = Require(sections, Nav3dFormat.AgentsTag, Nav3dFormat.LevelIndex, Nav3dFormat.AgentRecordBytes, header.AgentCount);
        Section cellSection = Require(sections, Nav3dFormat.CellsTag, Nav3dFormat.LevelIndex, Nav3dFormat.CellRecordBytes, cells);
        Section doors = Require(sections, Nav3dFormat.DoorsTag, Nav3dFormat.LevelIndex, Nav3dFormat.DoorRecordBytes, header.DoorCount);
        Section pois = Require(sections, Nav3dFormat.PoisTag, Nav3dFormat.LevelIndex, Nav3dFormat.PoiRecordBytes, header.PoiCount);
        if (header.Spawn < -1 || header.Spawn >= header.PoiCount || header.Up < -1 || header.Up >= header.PoiCount
            || header.Down < -1 || header.Down >= header.PoiCount)
        {
            throw new InvalidDataException("the .nav3d spawn or arrival index names no point of interest.");
        }

        AgentSections[] perAgent = new AgentSections[header.AgentCount];
        for (int a = 0; a < header.AgentCount; a++)
        {
            uint index = (uint)a;
            Section nodes = Require(sections, Nav3dFormat.NodesTag, index, 4, -1);
            Section leaves = Require(sections, Nav3dFormat.LeavesTag, index, Nav3dFormat.LeafRecordBytes, -1);
            int leafCount = leaves.Length / Nav3dFormat.LeafRecordBytes;
            Section components = Require(sections, Nav3dFormat.ComponentsTag, index, Nav3dFormat.ComponentRecordBytes, -1);
            perAgent[a] = new AgentSections
            {
                Roots = Require(sections, Nav3dFormat.RootsTag, index, 4, cells),
                Nodes = nodes,
                Leaves = leaves,
                AdjacencyStart = Require(sections, Nav3dFormat.AdjacencyStartTag, index, 4, leafCount + 1),
                Adjacency = Require(sections, Nav3dFormat.AdjacencyTag, index, 4, -1),
                Links = Require(sections, Nav3dFormat.LinksTag, index, Nav3dFormat.LinkRecordBytes, -1),
                Components = components,
                PoiLeaves = Require(sections, Nav3dFormat.PoiLeavesTag, index, 4, header.PoiCount),
            };
            ValidateAgent(b, perAgent[a], cells, components.Length / Nav3dFormat.ComponentRecordBytes, a);
        }

        return new Nav3dReader(file, header, strings, agents, cellSection, doors, pois, perAgent);
    }

    /// <summary>An agent's name.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>Its name.</returns>
    public string AgentName(int agent) => String(ReadU32(_agents, agent, Nav3dFormat.AgentRecordBytes, 0)) ?? string.Empty;

    /// <summary>An agent's box, relative to its origin.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The box's two corners.</returns>
    public (Vec3 Mins, Vec3 Maxs) AgentBox(int agent)
    {
        ReadOnlySpan<byte> r = Record(_agents, agent, Nav3dFormat.AgentRecordBytes);
        return (
            new Vec3(BinaryPrimitives.ReadSingleLittleEndian(r[4..]), BinaryPrimitives.ReadSingleLittleEndian(r[8..]),
                BinaryPrimitives.ReadSingleLittleEndian(r[12..])),
            new Vec3(BinaryPrimitives.ReadSingleLittleEndian(r[16..]), BinaryPrimitives.ReadSingleLittleEndian(r[20..]),
                BinaryPrimitives.ReadSingleLittleEndian(r[24..])));
    }

    /// <summary>The contents bits an agent collides with.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The mask.</returns>
    public int AgentContentsMask(int agent) => (int)ReadU32(_agents, agent, Nav3dFormat.AgentRecordBytes, 28);

    /// <summary>Finds an agent by name.</summary>
    /// <param name="name">The name, compared exactly.</param>
    /// <returns>The agent's index, or -1.</returns>
    public int FindAgent(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (int a = 0; a < AgentCount; a++)
        {
            if (AgentName(a) == name)
            {
                return a;
            }
        }

        return -1;
    }

    /// <summary>How many free leaves an agent has.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The count.</returns>
    public int LeafCount(int agent) => _agentSections[agent].Leaves.Length / Nav3dFormat.LeafRecordBytes;

    /// <summary>One free leaf.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="leaf">The leaf.</param>
    /// <returns>The leaf's record.</returns>
    public Nav3dLeaf Leaf(int agent, int leaf)
    {
        ReadOnlySpan<byte> r = Record(_agentSections[agent].Leaves, leaf, Nav3dFormat.LeafRecordBytes);
        return new Nav3dLeaf(
            BinaryPrimitives.ReadUInt16LittleEndian(r),
            BinaryPrimitives.ReadUInt16LittleEndian(r[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(r[4..]),
            r[6],
            (Nav3dLeafFlags)r[7],
            BinaryPrimitives.ReadUInt32LittleEndian(r[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(r[12..]));
    }

    /// <summary>A leaf's low corner in level coordinates.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="leaf">The leaf.</param>
    /// <returns>The corner.</returns>
    public Vec3 LeafMins(int agent, int leaf)
    {
        Nav3dLeaf l = Leaf(agent, leaf);
        return new Vec3(Origin.X + (l.X * VoxelSize), Origin.Y + (l.Y * VoxelSize), Origin.Z + (l.Z * VoxelSize));
    }

    /// <summary>A leaf's edge in units.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="leaf">The leaf.</param>
    /// <returns>The edge.</returns>
    public float LeafSize(int agent, int leaf) => Leaf(agent, leaf).Size * VoxelSize;

    /// <summary>How many octree nodes an agent has, over every cell.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The count.</returns>
    public int NodeCount(int agent) => _agentSections[agent].Nodes.Length / 4;

    /// <summary>One node word (<see cref="Nav3dFormat.KindOf"/>, <see cref="Nav3dFormat.PayloadOf"/>).</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="node">The node.</param>
    /// <returns>The word.</returns>
    public uint Node(int agent, int node) => ReadU32(_agentSections[agent].Nodes, node, 4, 0);

    /// <summary>A cell's root node for an agent.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="cell">The cell, <c>row × columns + column</c>.</param>
    /// <returns>The root, or -1 when no room stands in the cell.</returns>
    public int CellRoot(int agent, int cell) => (int)ReadU32(_agentSections[agent].Roots, cell, 4, 0);

    /// <summary>The free leaf holding a point, for an agent: where the agent's origin may stand.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="point">The point, in level coordinates.</param>
    /// <returns>The leaf, or -1 when the point is outside the grid, in an empty cell, or where the agent does not fit.</returns>
    /// <remarks>
    /// A point on a voxel boundary belongs to the voxel above it on each axis
    /// (voxels are half-open, low side in). Allocation-free: at most
    /// <see cref="OctreeDepth"/> node reads.
    /// </remarks>
    public int FindLeaf(int agent, Vec3 point)
    {
        if (!TryVoxel(point, out int column, out int row, out int vx, out int vy, out int vz))
        {
            return -1;
        }

        return FindLeaf(agent, (row * Columns) + column, vx, vy, vz);
    }

    /// <summary>The free leaf holding a voxel of a cell, for an agent.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="cell">The cell.</param>
    /// <param name="x">The voxel within the cell along x, 0 to <see cref="CellVoxels"/> - 1.</param>
    /// <param name="y">Along y.</param>
    /// <param name="z">Along z.</param>
    /// <returns>The leaf, or -1.</returns>
    public int FindLeaf(int agent, int cell, int x, int y, int z)
    {
        if ((uint)cell >= (uint)CellCount || (uint)x >= (uint)CellVoxels || (uint)y >= (uint)CellVoxels
            || (uint)z >= (uint)CellVoxels)
        {
            return -1;
        }

        int root = CellRoot(agent, cell);
        if (root < 0)
        {
            return -1;
        }

        ReadOnlySpan<byte> nodes = Slice(_agentSections[agent].Nodes);
        uint word = BinaryPrimitives.ReadUInt32LittleEndian(nodes[(root * 4)..]);
        int half = (1 << OctreeDepth) >> 1;
        int x0 = 0;
        int y0 = 0;
        int z0 = 0;
        while (Nav3dFormat.KindOf(word) == Nav3dNodeKind.Inner && half > 0)
        {
            int octant = 0;
            if (x >= x0 + half)
            {
                octant |= 1;
                x0 += half;
            }

            if (y >= y0 + half)
            {
                octant |= 2;
                y0 += half;
            }

            if (z >= z0 + half)
            {
                octant |= 4;
                z0 += half;
            }

            int child = (int)Nav3dFormat.PayloadOf(word) + octant;
            word = BinaryPrimitives.ReadUInt32LittleEndian(nodes[(child * 4)..]);
            half >>= 1;
        }

        return Nav3dFormat.KindOf(word) == Nav3dNodeKind.Free ? (int)Nav3dFormat.PayloadOf(word) : -1;
    }

    /// <summary>A leaf's neighbours: the leaves sharing a face with it, and those it reaches through a door.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="leaf">The leaf.</param>
    /// <returns>An allocation-free enumerable over the neighbours, in ascending leaf order.</returns>
    public NeighbourList Neighbours(int agent, int leaf)
    {
        AgentSections s = _agentSections[agent];
        uint start = ReadU32(s.AdjacencyStart, leaf, 4, 0);
        uint end = ReadU32(s.AdjacencyStart, leaf + 1, 4, 0);
        return new NeighbourList(Slice(s.Adjacency).Slice((int)start * 4, (int)(end - start) * 4));
    }

    /// <summary>How many connected components an agent's leaves form.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The count.</returns>
    public int ComponentCount(int agent) => _agentSections[agent].Components.Length / Nav3dFormat.ComponentRecordBytes;

    /// <summary>One component's size.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="component">The component.</param>
    /// <returns>Its leaf and voxel counts.</returns>
    public Nav3dComponent Component(int agent, int component) => new(
        ReadU32(_agentSections[agent].Components, component, Nav3dFormat.ComponentRecordBytes, 0),
        ReadU32(_agentSections[agent].Components, component, Nav3dFormat.ComponentRecordBytes, 4));

    /// <summary>How many leaf pairs an agent has joined through doors.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The count.</returns>
    public int LinkCount(int agent) => _agentSections[agent].Links.Length / Nav3dFormat.LinkRecordBytes;

    /// <summary>One door link.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="link">The link.</param>
    /// <returns>The pair and its door.</returns>
    public Nav3dDoorLink Link(int agent, int link) => new(
        ReadU32(_agentSections[agent].Links, link, Nav3dFormat.LinkRecordBytes, 0),
        ReadU32(_agentSections[agent].Links, link, Nav3dFormat.LinkRecordBytes, 4),
        ReadU32(_agentSections[agent].Links, link, Nav3dFormat.LinkRecordBytes, 8));

    /// <summary>A point of interest's position, without reading its strings.</summary>
    /// <param name="poi">The point.</param>
    /// <returns>Its position in level coordinates.</returns>
    public Vec3 PoiPosition(int poi)
    {
        ReadOnlySpan<byte> r = Record(_pois, poi, Nav3dFormat.PoiRecordBytes);
        return new Vec3(BinaryPrimitives.ReadSingleLittleEndian(r), BinaryPrimitives.ReadSingleLittleEndian(r[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(r[8..]));
    }

    /// <summary>A point of interest's yaw in degrees.</summary>
    /// <param name="poi">The point.</param>
    /// <returns>The yaw.</returns>
    public float PoiYaw(int poi) => BinaryPrimitives.ReadSingleLittleEndian(Record(_pois, poi, Nav3dFormat.PoiRecordBytes)[12..]);

    /// <summary>A point of interest's flags.</summary>
    /// <param name="poi">The point.</param>
    /// <returns>The flags.</returns>
    public Nav3dPoiFlags PoiFlags(int poi) =>
        (Nav3dPoiFlags)BinaryPrimitives.ReadUInt16LittleEndian(Record(_pois, poi, Nav3dFormat.PoiRecordBytes)[44..]);

    /// <summary>A point of interest's type, as UTF-8 bytes (no allocation).</summary>
    /// <param name="poi">The point.</param>
    /// <returns>The type's bytes, without the NUL.</returns>
    public ReadOnlySpan<byte> PoiTypeUtf8(int poi) =>
        StringUtf8(BinaryPrimitives.ReadUInt32LittleEndian(Record(_pois, poi, Nav3dFormat.PoiRecordBytes)[20..]));

    /// <summary>A whole point of interest, strings and all.</summary>
    /// <param name="poi">The point.</param>
    /// <returns>The point.</returns>
    public Nav3dPoi Poi(int poi)
    {
        ReadOnlySpan<byte> r = Record(_pois, poi, Nav3dFormat.PoiRecordBytes);
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

    /// <summary>A point of interest's leaf for an agent.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="poi">The point.</param>
    /// <returns>The leaf, or -1 when the point does not apply to the agent or lies where it does not fit.</returns>
    public int PoiLeaf(int agent, int poi) => (int)ReadU32(_agentSections[agent].PoiLeaves, poi, 4, 0);

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
        ReadOnlySpan<byte> r = Record(_cells, cell, Nav3dFormat.CellRecordBytes);
        return new Nav3dCell(String(BinaryPrimitives.ReadUInt32LittleEndian(r)), r[4], (Nav3dRoomRole)r[5], r[6], r[7]);
    }

    /// <summary>One door record.</summary>
    /// <param name="door">The door.</param>
    /// <returns>Its record.</returns>
    public Nav3dDoor Door(int door)
    {
        ReadOnlySpan<byte> r = Record(_doors, door, Nav3dFormat.DoorRecordBytes);
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

        ReadOnlySpan<byte> table = Slice(_strings);
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
        List<Nav3dAgent> agents = [];
        for (int a = 0; a < AgentCount; a++)
        {
            (Vec3 mins, Vec3 maxs) = AgentBox(a);
            AgentSections s = _agentSections[a];
            agents.Add(new Nav3dAgent(AgentName(a), mins, maxs, AgentContentsMask(a))
            {
                Roots = [.. Enumerable.Range(0, CellCount).Select(c => CellRoot(a, c))],
                Nodes = [.. Enumerable.Range(0, NodeCount(a)).Select(n => Node(a, n))],
                Leaves = [.. Enumerable.Range(0, LeafCount(a)).Select(l => Leaf(a, l))],
                AdjacencyStart = [.. Enumerable.Range(0, LeafCount(a) + 1).Select(i => ReadU32(s.AdjacencyStart, i, 4, 0))],
                Adjacency = [.. Enumerable.Range(0, s.Adjacency.Length / 4).Select(i => ReadU32(s.Adjacency, i, 4, 0))],
                Links = [.. Enumerable.Range(0, LinkCount(a)).Select(l => Link(a, l))],
                Components = [.. Enumerable.Range(0, ComponentCount(a)).Select(c => Component(a, c))],
                PoiLeaves = [.. Enumerable.Range(0, PoiCount).Select(p => PoiLeaf(a, p))],
            });
        }

        return new Nav3dLevel
        {
            CellSize = CellSize,
            VoxelSize = VoxelSize,
            CellVoxels = CellVoxels,
            Columns = Columns,
            Rows = Rows,
            Origin = Origin,
            FloorNormalZ = FloorNormalZ,
            Cells = [.. Enumerable.Range(0, CellCount).Select(Cell)],
            Doors = [.. Enumerable.Range(0, DoorCount).Select(Door)],
            Pois = [.. Enumerable.Range(0, PoiCount).Select(Poi)],
            Agents = agents,
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

    private static Section Require(Dictionary<(string, uint), Section> sections, string tag, uint index, int record, int count)
    {
        string who = index == Nav3dFormat.LevelIndex ? $"\"{tag}\"" : $"agent {index}'s \"{tag}\"";
        if (!sections.TryGetValue((tag, index), out Section section))
        {
            throw new InvalidDataException($"the .nav3d file has no {who} section.");
        }

        if (section.Length % record != 0 || (count >= 0 && section.Length != (long)count * record) || (count < 0 && record == 1 && section.Length == 0))
        {
            throw new InvalidDataException($"the .nav3d {who} section is {section.Length} bytes, which its counts do not allow.");
        }

        return section;
    }

    private static void ValidateAgent(ReadOnlySpan<byte> b, AgentSections s, int cells, int components, int agent)
    {
        int nodes = s.Nodes.Length / 4;
        int leaves = s.Leaves.Length / Nav3dFormat.LeafRecordBytes;
        int adjacency = s.Adjacency.Length / 4;
        for (int i = 0; i < nodes; i++)
        {
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(b[(s.Nodes.Offset + (i * 4))..]);
            uint payload = Nav3dFormat.PayloadOf(word);
            bool bad = Nav3dFormat.KindOf(word) switch
            {
                Nav3dNodeKind.Inner => (long)payload + 8 > nodes,
                Nav3dNodeKind.Free => payload >= leaves,
                _ => false,
            };
            if (bad)
            {
                throw new InvalidDataException($"agent {agent}'s node {i} points outside its nodes or leaves.");
            }
        }

        for (int c = 0; c < cells; c++)
        {
            int root = BinaryPrimitives.ReadInt32LittleEndian(b[(s.Roots.Offset + (c * 4))..]);
            if (root < -1 || root >= nodes)
            {
                throw new InvalidDataException($"agent {agent}'s cell {c} has root {root}, outside its {nodes} nodes.");
            }
        }

        for (int l = 0; l < leaves; l++)
        {
            ReadOnlySpan<byte> r = b.Slice(s.Leaves.Offset + (l * Nav3dFormat.LeafRecordBytes), Nav3dFormat.LeafRecordBytes);
            if (BinaryPrimitives.ReadUInt32LittleEndian(r[8..]) >= components || BinaryPrimitives.ReadUInt32LittleEndian(r[12..]) >= cells)
            {
                throw new InvalidDataException($"agent {agent}'s leaf {l} names a component or cell out of range.");
            }
        }

        uint previous = 0;
        for (int l = 0; l <= leaves; l++)
        {
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(b[(s.AdjacencyStart.Offset + (l * 4))..]);
            if (start < previous || start > adjacency || (l == 0 && start != 0) || (l == leaves && start != adjacency))
            {
                throw new InvalidDataException($"agent {agent}'s adjacency starts are not a partition of its {adjacency} entries.");
            }

            previous = start;
        }

        for (int i = 0; i < adjacency; i++)
        {
            uint entry = BinaryPrimitives.ReadUInt32LittleEndian(b[(s.Adjacency.Offset + (i * 4))..]) & ~Nav3dFormat.ThroughDoorBit;
            if (entry >= leaves)
            {
                throw new InvalidDataException($"agent {agent}'s adjacency entry {i} names leaf {entry} of {leaves}.");
            }
        }

        for (int i = 0; i < s.Links.Length / Nav3dFormat.LinkRecordBytes; i++)
        {
            ReadOnlySpan<byte> r = b.Slice(s.Links.Offset + (i * Nav3dFormat.LinkRecordBytes), Nav3dFormat.LinkRecordBytes);
            if (BinaryPrimitives.ReadUInt32LittleEndian(r) >= leaves || BinaryPrimitives.ReadUInt32LittleEndian(r[4..]) >= leaves)
            {
                throw new InvalidDataException($"agent {agent}'s door link {i} names a leaf out of range.");
            }
        }

        for (int i = 0; i < s.PoiLeaves.Length / 4; i++)
        {
            int leaf = BinaryPrimitives.ReadInt32LittleEndian(b[(s.PoiLeaves.Offset + (i * 4))..]);
            if (leaf < -1 || leaf >= leaves)
            {
                throw new InvalidDataException($"agent {agent}'s point {i} names leaf {leaf} of {leaves}.");
            }
        }
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
        private readonly ReadOnlySpan<byte> _entries;

        internal NeighbourList(ReadOnlySpan<byte> entries) => _entries = entries;

        /// <summary>How many neighbours.</summary>
        public int Count => _entries.Length / 4;

        /// <summary>One neighbour.</summary>
        /// <param name="index">Which, 0 to <see cref="Count"/> - 1.</param>
        /// <returns>The neighbour.</returns>
        public Nav3dNeighbour this[int index]
        {
            get
            {
                uint entry = BinaryPrimitives.ReadUInt32LittleEndian(_entries[(index * 4)..]);
                return new Nav3dNeighbour((int)(entry & ~Nav3dFormat.ThroughDoorBit), (entry & Nav3dFormat.ThroughDoorBit) != 0);
            }
        }

        /// <summary>The enumerator, for <c>foreach</c>.</summary>
        /// <returns>The enumerator.</returns>
        public Enumerator GetEnumerator() => new(_entries);

        /// <summary>Walks a <see cref="NeighbourList"/>.</summary>
        public ref struct Enumerator
        {
            private readonly ReadOnlySpan<byte> _entries;
            private int _at;

            internal Enumerator(ReadOnlySpan<byte> entries)
            {
                _entries = entries;
                _at = -4;
            }

            /// <summary>The current neighbour.</summary>
            public readonly Nav3dNeighbour Current
            {
                get
                {
                    uint entry = BinaryPrimitives.ReadUInt32LittleEndian(_entries[_at..]);
                    return new Nav3dNeighbour((int)(entry & ~Nav3dFormat.ThroughDoorBit), (entry & Nav3dFormat.ThroughDoorBit) != 0);
                }
            }

            /// <summary>Moves to the next neighbour.</summary>
            /// <returns>False past the last.</returns>
            public bool MoveNext()
            {
                _at += 4;
                return _at < _entries.Length;
            }
        }
    }

    private readonly record struct Section(int Offset, int Length);

    private readonly record struct AgentSections
    {
        public Section Roots { get; init; }

        public Section Nodes { get; init; }

        public Section Leaves { get; init; }

        public Section AdjacencyStart { get; init; }

        public Section Adjacency { get; init; }

        public Section Links { get; init; }

        public Section Components { get; init; }

        public Section PoiLeaves { get; init; }
    }

    private readonly record struct Header
    {
        public int Version { get; init; }

        public NavCodec Codec { get; init; }

        public int HeaderBytes { get; init; }

        public int SectionCount { get; init; }

        public int AgentCount { get; init; }

        public float CellSize { get; init; }

        public float VoxelSize { get; init; }

        public int CellVoxels { get; init; }

        public int Depth { get; init; }

        public int Columns { get; init; }

        public int Rows { get; init; }

        public Vec3 Origin { get; init; }

        public float FloorNormalZ { get; init; }

        public int PoiCount { get; init; }

        public int DoorCount { get; init; }

        public int Spawn { get; init; }

        public int Up { get; init; }

        public int Down { get; init; }

        public Guid LevelId { get; init; }

        public Guid PackId { get; init; }
    }
}
