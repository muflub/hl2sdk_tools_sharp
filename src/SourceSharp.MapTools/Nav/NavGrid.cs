//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Nav;

namespace SourceSharp.MapTools.Nav;

/// <summary>A box of voxels to classify: its low corner, its size in voxels and the voxel's edge.</summary>
/// <param name="OriginX">The low corner's x.</param>
/// <param name="OriginY">The low corner's y.</param>
/// <param name="OriginZ">The low corner's z.</param>
/// <param name="SizeX">Voxels along x.</param>
/// <param name="SizeY">Voxels along y.</param>
/// <param name="SizeZ">Voxels along z.</param>
/// <param name="VoxelSize">A voxel's edge.</param>
public readonly record struct NavRegion(
    double OriginX, double OriginY, double OriginZ, int SizeX, int SizeY, int SizeZ, double VoxelSize)
{
    /// <summary>A voxel's index, x fastest, then y, then z.</summary>
    /// <param name="x">Along x.</param>
    /// <param name="y">Along y.</param>
    /// <param name="z">Along z.</param>
    /// <returns>The index.</returns>
    public int Index(int x, int y, int z) => (((z * SizeY) + y) * SizeX) + x;

    /// <summary>A voxel's box.</summary>
    /// <param name="x">Along x.</param>
    /// <param name="y">Along y.</param>
    /// <param name="z">Along z.</param>
    /// <returns>The box, computed as the grid computes voxel bounds everywhere: origin plus index times edge.</returns>
    public NavBox Voxel(int x, int y, int z) => new(
        OriginX + (x * VoxelSize), OriginY + (y * VoxelSize), OriginZ + (z * VoxelSize),
        OriginX + ((x + 1) * VoxelSize), OriginY + ((y + 1) * VoxelSize), OriginZ + ((z + 1) * VoxelSize));
}

/// <summary>
/// Everything the grid knows about one voxel: its clearance record for each
/// clip class, its flags and cost, and, for a class that stands on solid
/// right under it, the floor's height.
/// </summary>
/// <param name="PlayerRecord">The player class's record, an index into the grid's <see cref="NavRecordTable"/>.</param>
/// <param name="NpcRecord">The NPC class's record.</param>
/// <param name="Flags">Water, ladder, and per class grounded and walkable.</param>
/// <param name="Cost">The cost in 8.8 fixed point.</param>
/// <param name="PlayerFloorZ">The player class's floor height when grounded, else 0.</param>
/// <param name="NpcFloorZ">The NPC class's, likewise.</param>
public readonly record struct NavVoxelKey(
    int PlayerRecord, int NpcRecord, Nav3dLeafFlags Flags, ushort Cost, float PlayerFloorZ, float NpcFloorZ)
{
    /// <summary>The flags that belong to a floor: they describe a run's bottom, not every voxel of it.</summary>
    public const Nav3dLeafFlags FloorFlags =
        Nav3dLeafFlags.GroundedPlayer | Nav3dLeafFlags.GroundedNpc | Nav3dLeafFlags.WalkablePlayer | Nav3dLeafFlags.WalkableNpc;

    /// <summary>A voxel solid for both classes: no leaf holds it.</summary>
    public static NavVoxelKey Solid => new(NavRecordTable.BlockedIndex, NavRecordTable.BlockedIndex, Nav3dLeafFlags.None, 256, 0, 0);

    /// <summary>Whether the voxel is solid for both classes.</summary>
    public bool IsSolid => PlayerRecord == NavRecordTable.BlockedIndex && NpcRecord == NavRecordTable.BlockedIndex;

    /// <summary>A class's record.</summary>
    /// <param name="clipClass">The class.</param>
    /// <returns>The record's index.</returns>
    public int Record(Nav3dClipClass clipClass) => clipClass == Nav3dClipClass.Npc ? NpcRecord : PlayerRecord;

    /// <summary>Whether two voxels one above the other may share a run: the same records, flags (floors aside) and cost.</summary>
    /// <param name="other">The other voxel.</param>
    /// <returns>True when they may.</returns>
    public bool SameRun(NavVoxelKey other) =>
        PlayerRecord == other.PlayerRecord && NpcRecord == other.NpcRecord && Cost == other.Cost
        && (Flags & ~FloorFlags) == (other.Flags & ~FloorFlags);
}

/// <summary>
/// The distinct clearance records of a grid, each stored once and named by
/// index. Index <see cref="BlockedIndex"/> is always the record that blocks
/// everything.
/// </summary>
/// <remarks>
/// A room has a few hundred distinct records against thousands of voxels,
/// and a record is its canonical bytes (<see cref="Nav3dClearance"/>), so two
/// voxels share a record exactly when their clearance is the same for every
/// agent. Looked up by content without copying, so building a cell does not
/// allocate a record per voxel.
/// </remarks>
public sealed class NavRecordTable
{
    /// <summary>The index of the blocked record.</summary>
    public const int BlockedIndex = 0;

    private readonly List<byte[]> _records = [];
    private readonly Dictionary<byte[], int> _index = new(new ByteContent());
    private readonly List<bool> _hasBrushes = [];

    /// <summary>A table holding only the blocked record.</summary>
    public NavRecordTable()
    {
        _ = Add(Nav3dClearance.BlockedRecord);
    }

    /// <summary>How many records.</summary>
    public int Count => _records.Count;

    /// <summary>The records, in index order.</summary>
    public IReadOnlyList<byte[]> Records => _records;

    /// <summary>One record's bytes.</summary>
    /// <param name="index">The record.</param>
    /// <returns>Its bytes.</returns>
    public byte[] this[int index] => _records[index];

    /// <summary>Whether a record lists an overhanging brush: a voxel with one is a leaf of its own.</summary>
    /// <param name="index">The record.</param>
    /// <returns>True when it lists one.</returns>
    public bool HasBrushes(int index) => _hasBrushes[index];

    /// <summary>A record's index, adding it when new.</summary>
    /// <param name="record">The record's bytes; copied when added.</param>
    /// <returns>The index.</returns>
    public int Add(ReadOnlySpan<byte> record)
    {
        Dictionary<byte[], int>.AlternateLookup<ReadOnlySpan<byte>> lookup = _index.GetAlternateLookup<ReadOnlySpan<byte>>();
        if (lookup.TryGetValue(record, out int found))
        {
            return found;
        }

        byte[] copy = record.ToArray();
        _index[copy] = _records.Count;
        _records.Add(copy);
        _hasBrushes.Add(Nav3dClearance.BrushCount(copy) > 0);
        return _records.Count - 1;
    }

    /// <summary>Compares byte arrays by content, and a span against one without copying.</summary>
    private sealed class ByteContent : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x is null ? y is null : y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode([DisallowNull] byte[] obj) => Hash(obj);

        public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);

        public int GetHashCode(ReadOnlySpan<byte> alternate) => Hash(alternate);

        public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();

        private static int Hash(ReadOnlySpan<byte> bytes)
        {
            HashCode hash = new();
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }
    }
}

/// <summary>A classified box of voxels: each voxel's key, and the records the keys name.</summary>
/// <param name="Region">The box.</param>
/// <param name="Records">The records.</param>
/// <param name="Keys">Each voxel's key, x fastest, then y, then z.</param>
public sealed record NavGrid(NavRegion Region, NavRecordTable Records, NavVoxelKey[] Keys)
{
    /// <summary>One voxel's key.</summary>
    /// <param name="x">Along x.</param>
    /// <param name="y">Along y.</param>
    /// <param name="z">Along z.</param>
    /// <returns>The key.</returns>
    public NavVoxelKey this[int x, int y, int z] => Keys[Region.Index(x, y, z)];

    /// <summary>One voxel's record for a class.</summary>
    /// <param name="x">Along x.</param>
    /// <param name="y">Along y.</param>
    /// <param name="z">Along z.</param>
    /// <param name="clipClass">The class.</param>
    /// <returns>The record's bytes.</returns>
    public byte[] Record(int x, int y, int z, Nav3dClipClass clipClass) => Records[this[x, y, z].Record(clipClass)];
}

/// <summary>A leaf of a room or level before it is written: a run of voxels in one column with one key.</summary>
/// <param name="ZLo">The run's lowest voxel.</param>
/// <param name="Height">How many voxels it holds.</param>
/// <param name="Key">The key its voxels share; the floor fields are its bottom voxel's.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NavRun(byte ZLo, byte Height, NavVoxelKey Key);

/// <summary>
/// A cell's (or any box's) voxel columns as runs: the leaves. Each column
/// lists the runs of voxels that are free for at least one clip class, low
/// to high, a run being a maximal stack of voxels with the same key.
/// </summary>
/// <param name="SizeX">Columns along x.</param>
/// <param name="SizeY">Columns along y.</param>
/// <param name="ColumnStarts">Where each column's runs start in <paramref name="Runs"/>; one more entry than columns, x fastest.</param>
/// <param name="Runs">The runs.</param>
public sealed record NavColumns(int SizeX, int SizeY, int[] ColumnStarts, NavRun[] Runs)
{
    /// <summary>
    /// The runs of a grid. A voxel starts a new run when the one below it is
    /// solid or has another key, and a voxel whose record lists an
    /// overhanging brush is always a run of its own, so that within a run the
    /// voxels an agent fits in are a prefix from the bottom (the reader
    /// relies on it).
    /// </summary>
    /// <param name="grid">The grid.</param>
    /// <returns>The columns.</returns>
    public static NavColumns Of(NavGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        return Of(grid.Region, grid.Keys, grid.Records);
    }

    /// <summary>The runs of dense voxel keys.</summary>
    /// <param name="region">The box the keys cover.</param>
    /// <param name="keys">Each voxel's key, x fastest.</param>
    /// <param name="records">The records the keys name.</param>
    /// <returns>The columns.</returns>
    public static NavColumns Of(NavRegion region, ReadOnlySpan<NavVoxelKey> keys, NavRecordTable records)
    {
        ArgumentNullException.ThrowIfNull(records);
        int[] starts = new int[(region.SizeX * region.SizeY) + 1];
        List<NavRun> runs = [];
        for (int y = 0; y < region.SizeY; y++)
        {
            for (int x = 0; x < region.SizeX; x++)
            {
                starts[(y * region.SizeX) + x] = runs.Count;
                int start = -1;
                NavVoxelKey first = default;
                for (int z = 0; z <= region.SizeZ; z++)
                {
                    NavVoxelKey key = z < region.SizeZ ? keys[region.Index(x, y, z)] : NavVoxelKey.Solid;
                    bool alone = records.HasBrushes(key.PlayerRecord) || records.HasBrushes(key.NpcRecord);
                    if (start >= 0 && (key.IsSolid || alone || !key.SameRun(first) || records.HasBrushes(first.PlayerRecord)
                        || records.HasBrushes(first.NpcRecord)))
                    {
                        runs.Add(new NavRun((byte)start, (byte)(z - start), first));
                        start = -1;
                    }

                    if (start < 0 && !key.IsSolid)
                    {
                        start = z;
                        first = key;
                    }
                }
            }
        }

        starts[^1] = runs.Count;
        return new NavColumns(region.SizeX, region.SizeY, starts, [.. runs]);
    }

    /// <summary>One column's runs.</summary>
    /// <param name="x">The column along x.</param>
    /// <param name="y">Along y.</param>
    /// <returns>The runs, low to high.</returns>
    public ReadOnlySpan<NavRun> Column(int x, int y)
    {
        int column = (y * SizeX) + x;
        return Runs.AsSpan(ColumnStarts[column], ColumnStarts[column + 1] - ColumnStarts[column]);
    }

    /// <summary>The dense keys the runs stand for: a run's bottom voxel keeps its floor, the voxels above it have none.</summary>
    /// <param name="sizeZ">Voxels along z.</param>
    /// <returns>Each voxel's key, x fastest; <see cref="NavVoxelKey.Solid"/> where no run is.</returns>
    public NavVoxelKey[] Expand(int sizeZ)
    {
        NavVoxelKey[] keys = new NavVoxelKey[SizeX * SizeY * sizeZ];
        Array.Fill(keys, NavVoxelKey.Solid);
        for (int y = 0; y < SizeY; y++)
        {
            for (int x = 0; x < SizeX; x++)
            {
                foreach (NavRun run in Column(x, y))
                {
                    for (int z = run.ZLo; z < run.ZLo + run.Height; z++)
                    {
                        keys[(((z * SizeY) + y) * SizeX) + x] = z == run.ZLo
                            ? run.Key
                            : run.Key with { Flags = run.Key.Flags & ~NavVoxelKey.FloorFlags, PlayerFloorZ = 0, NpcFloorZ = 0 };
                    }
                }
            }
        }

        return keys;
    }
}
