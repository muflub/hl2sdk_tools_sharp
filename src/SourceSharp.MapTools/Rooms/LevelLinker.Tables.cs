//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Validation;

namespace SourceSharp.MapTools.Rooms;

public static partial class LevelLinker
{
    /// <summary>
    /// The linked map's plane lump, shared by every room: a pair whose
    /// content another room (or the top tree, or a doorway carve) already
    /// brought is not appended again, it is named.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why share.</b> Rooms built on one kit stand on one grid, so most of
    /// their planes are the same planes: every floor and ceiling of a kind is
    /// the same z plane in every cell, and a wall on a cell face is the same
    /// x or y plane as the neighbour's wall and the top tree's split. Appended
    /// room by room, a 16 x 16 level of the stress library carried 17,000
    /// planes of which most were copies, and the plane count is a
    /// <c>ushort</c> field in every face and brush side.
    /// </para>
    /// <para>
    /// <b>Pairs stay pairs.</b> The format stores a plane and its flip as
    /// the pair <c>(2k, 2k + 1)</c>, and the engine finds a plane's opposite
    /// as <c>index ^ 1</c>, so the unit of sharing is the pair, keyed by its
    /// even half. The odd half is always the exact negation of the even one
    /// (the relocation and the carve both build it so), so a pair is fully
    /// named by its even plane. An axial pair's even half is its positive
    /// normal (the engine's walk reads an axial plane by one coordinate, as if
    /// its normal were the positive axis), and every axial pair that reaches
    /// this table is stored that way, so two axial pairs only ever match
    /// head-on. A non-axial pair may arrive in the opposite order from the
    /// stored one (vbsp orders non-axial pairs by nothing the format fixes),
    /// and then it is shared <em>flipped</em>: a reference to one of its
    /// halves takes the other index of the pair, which holds the same
    /// oriented plane, and a node on it swaps its children
    /// (<see cref="Orient"/>). Either way every reference resolves to the
    /// same oriented plane it named before, so the geometry is unchanged.
    /// </para>
    /// <para>
    /// <b>Exact content.</b> Two planes are the same when their normal,
    /// distance and type are equal as numbers: the relocation is integer
    /// exact (a quarter turn permutes components and the translation adds an
    /// integer), so a shared plane is bit for bit the same plane. The one
    /// difference in bits that is not a difference in value is the sign of
    /// zero: a quarter turn writes <c>-y</c> into x, which for a normal with
    /// no y is <c>-0</c>, and without folding it every turned room would
    /// miss every unturned one's axial planes. The key folds <c>-0</c> to
    /// <c>+0</c>; the stored plane is the first one's, as it came.
    /// </para>
    /// <para>
    /// <b>Order.</b> Pairs are numbered in the order they are first asked
    /// for, and the link asks sequentially, in layout order, after the
    /// parallel planning is done, so the lump is the same on any thread
    /// count. Indices 0 and 1 are the null pair, which no key names.
    /// </para>
    /// </remarks>
    internal sealed class LinkPlanes
    {
        private readonly List<DPlane> _planes = [new(), new()];
        private readonly Dictionary<PlaneKey, int> _byEven = [];

        /// <summary>The planes so far, pairs in order.</summary>
        public List<DPlane> Planes => _planes;

        /// <summary>How many planes the lump holds so far (twice the pairs).</summary>
        public int Count => _planes.Count;

        /// <summary>
        /// The pair holding <paramref name="even"/> and its flip: an existing
        /// pair when one holds the same plane (head-on) or its flip (then
        /// <c>Flipped</c>), else a new pair with <paramref name="even"/> first.
        /// </summary>
        /// <param name="even">The plane to find; its flip is its exact negation.</param>
        /// <returns>
        /// The linked even index, and whether the stored even half is the flip
        /// of <paramref name="even"/>.
        /// </returns>
        public (int Even, bool Flipped) Intern(DPlane even)
        {
            PlaneKey key = PlaneKey.Of(even.Normal, even.Dist, even.Type);
            if (_byEven.TryGetValue(key, out int at))
            {
                return (at, false);
            }

            if (_byEven.TryGetValue(PlaneKey.Of(-even.Normal, -even.Dist, even.Type), out at))
            {
                return (at, true);
            }

            at = _planes.Count;
            _planes.Add(even);
            _planes.Add(new DPlane { Normal = -even.Normal, Dist = -even.Dist, Type = even.Type });
            _byEven[key] = at;
            return (at, false);
        }

        /// <summary>
        /// The pair of a plane given by normal and distance, typed from its
        /// normal: how the top tree's cell faces and the doorway carve's
        /// planes join the table.
        /// </summary>
        public (int Even, bool Flipped) Intern(Vec3 normal, float dist) =>
            Intern(new DPlane { Normal = normal, Dist = dist, Type = (int)new Plane(normal, dist).Type });
    }

    /// <summary>
    /// Finds one room's planes, strings, texdatas and texinfos in the shared
    /// tables (adding what no earlier room brought) and records, per room
    /// entry, the linked entry every reference of the room takes; then holds
    /// the planes and texinfos to their limits, naming the room.
    /// </summary>
    /// <remarks>
    /// The planes are the room's moved pairs (<see cref="RoomPlan.TransformedPlanes"/>,
    /// each pair's even half keying it). The texinfos are the room's moved
    /// texinfos with their texdata remapped first, so two rooms' texinfos
    /// that name the same material by different room-local texdata indices
    /// are still one texinfo.
    /// </remarks>
    /// <remarks>
    /// A placement's cubemap patches (<paramref name="cubemaps"/>) are
    /// interned under their linked names, which hold the level's name and
    /// the placement's position, so each placement of a room with patches
    /// brings its own (<see cref="PlacementCubemaps"/>).
    /// </remarks>
    private static void InternRoomTables(RoomPlan plan, LinkPlanes planes, LinkTextures textures, PlacementCubemaps? cubemaps)
    {
        DPlane[] moved = plan.TransformedPlanes;
        int pairs = moved.Length / 2;
        plan.PlanePairs = new int[pairs];
        plan.PlanePairFlipped = new bool[pairs];
        for (int k = 0; k < pairs; k++)
        {
            (plan.PlanePairs[k], plan.PlanePairFlipped[k]) = planes.Intern(moved[2 * k]);
        }

        string name = plan.Placement.Room.Definition.Name;
        plan.StringMap = textures.InternStrings(plan.Bsp, name, cubemaps?.Strings);
        int[] texDatas = textures.InternTexDatas(plan.Bsp, plan.StringMap);
        plan.TexInfoMap = textures.InternTexInfos(plan.TexInfos, texDatas);
        InternLocalTables(plan, planes, textures, texDatas);

        CheckSharedTables(
            name,
            plan.Placement.Instance.Placement.CellX,
            plan.Placement.Instance.Placement.CellY,
            planes.Count,
            textures.TexInfos.Count);
    }

    /// <summary>
    /// Refuses shared tables past their limits once a room's entries are in:
    /// the planes past a face's and a brush side's <c>ushort</c> plane
    /// number, the texinfos past <c>MAX_MAP_TEXINFO</c>.
    /// </summary>
    /// <remarks>
    /// These two are checked as the tables are built rather than before any
    /// room is planned (<see cref="CheckCapacity"/>): how many planes and
    /// texinfos a room shares depends on the cell it stands in, which is
    /// only known once it is moved. The texdatas and strings do not, and are
    /// checked up front. What the top tree, the carve and the nodraw copies
    /// add after the rooms is checked where it is added.
    /// </remarks>
    internal static void CheckSharedTables(string room, int cellX, int cellY, int planes, int texInfos)
    {
        Limit(room, cellX, cellY, "planes", planes, ushort.MaxValue + 1);
        LoaderLimit(
            room, cellX, cellY, "texinfos", texInfos, BspLimits.Caps.First(c => c.Lump == BspLump.TexInfo).Max, "MAX_MAP_TEXINFO");
    }

    /// <summary>
    /// A node's children as they must be stored on the even half of its
    /// shared pair: swapped when that half is the flip of the plane the node
    /// was built on, so the front child stays on the front of the same
    /// oriented plane.
    /// </summary>
    internal static IntArray2 Orient(IntArray2 children, bool flipped)
    {
        if (!flipped)
        {
            return children;
        }

        IntArray2 swapped = default;
        swapped[0] = children[1];
        swapped[1] = children[0];
        return swapped;
    }

    /// <summary>
    /// A plane's identity as <see cref="LinkPlanes"/> compares it: the
    /// normal, distance and type, with <c>-0</c> folded to <c>+0</c>.
    /// </summary>
    internal readonly record struct PlaneKey(uint X, uint Y, uint Z, uint Dist, int Type)
    {
        public static PlaneKey Of(Vec3 normal, float dist, int type) =>
            new(Bits(normal.X), Bits(normal.Y), Bits(normal.Z), Bits(dist), type);
    }

    /// <summary>A float's bits with <c>-0</c> folded to <c>+0</c>, which is equal as a number.</summary>
    internal static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value == 0f ? 0f : value);

    /// <summary>
    /// The linked map's material tables, shared by every room: the texdata
    /// string table and its string data, the texdatas, and the texinfos.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why share.</b> Every room of a library uses the library's handful of
    /// materials, and every room compile writes its own texdata for each. The
    /// engine loads at most <c>MAX_MAP_TEXDATA</c> (2048) of them, so
    /// appended room by room the texdata was the first loader cap a large
    /// level passed: at five materials a room, a 21 x 21 level of the stress
    /// library was refused with every material in it already present
    /// hundreds of times. Shared, the texdata lump is the library's
    /// materials, whatever the level's size.
    /// </para>
    /// <para>
    /// <b>What is equal.</b> A string is its bytes up to the terminating NUL.
    /// A texdata is its reflectivity, its name (as the shared string it
    /// names), and its four sizes. A texinfo is its texture and lightmap
    /// axes and offsets, its flags, and its texdata (as the shared texdata it
    /// names). Floats compare as numbers, <c>-0</c> equal to <c>+0</c>, as
    /// <see cref="LinkPlanes"/> explains. A texinfo is placement-specific
    /// (its offsets take the placement's translation), so texinfos mostly
    /// share within a cell's own tools and nodraw copies and between rooms
    /// whose translation leaves an axis alone; the texdata and strings share
    /// across the whole level.
    /// </para>
    /// <para>
    /// <b>Order.</b> Entries are numbered in the order they are first asked
    /// for, sequentially and in layout order, so the tables are the same on
    /// any thread count. The same class, over the same rooms in the same
    /// order, is what <see cref="CheckCapacity"/> counts the texdata and
    /// strings with before any room is planned, so the count it refuses at
    /// is the count the assembly writes.
    /// </para>
    /// </remarks>
    internal sealed class LinkTextures
    {
        private readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);
        private readonly Dictionary<TexDataKey, int> _texDatas = [];
        private readonly Dictionary<byte[], int> _texInfos = new(new ByteContent());

        /// <summary>The string table: per string, its byte offset into <see cref="StringData"/>.</summary>
        public List<int> StringTable { get; } = [];

        /// <summary>The string data: every shared string once, NUL-terminated.</summary>
        public List<byte> StringData { get; } = [];

        /// <summary>The shared texdatas, their names indexing <see cref="StringTable"/>.</summary>
        public List<DTexData> TexDatas { get; } = [];

        /// <summary>The shared texinfos, their texdata indexing <see cref="TexDatas"/>.</summary>
        public List<TexInfo> TexInfos { get; } = [];

        /// <summary>
        /// Every entry of a room's string table, as the linked string it
        /// names: indexed by the room's string-table id.
        /// </summary>
        /// <exception cref="LinkException">An entry points outside the room's string data.</exception>
        public int[] InternStrings(BspData room, string name) => InternStrings(room, name, renamed: null);

        /// <summary>
        /// Every entry of a room's string table, as the linked string it
        /// names, the entries in <paramref name="renamed"/> under the name
        /// given there instead of their own (a placement's cubemap patches,
        /// <see cref="PlacementCubemaps.Strings"/>).
        /// </summary>
        /// <exception cref="LinkException">An entry points outside the room's string data.</exception>
        public int[] InternStrings(BspData room, string name, IReadOnlyDictionary<int, string>? renamed)
        {
            ReadOnlySpan<int> table = BspStructView.As<int>(room[BspLump.TexDataStringTable]);
            ReadOnlySpan<byte> data = room[BspLump.TexDataStringData].Data.Span;
            int[] map = new int[table.Length];
            for (int i = 0; i < table.Length; i++)
            {
                string text = TableString(data, table[i], i, name);
                map[i] = InternString(renamed is not null && renamed.TryGetValue(i, out string? linked) ? linked : text);
            }

            return map;
        }

        /// <summary>
        /// Every texdata of a room, as the linked texdata with the same
        /// content, its name remapped through <paramref name="stringMap"/>.
        /// </summary>
        public int[] InternTexDatas(BspData room, int[] stringMap)
        {
            ReadOnlySpan<DTexData> datas = BspStructView.As<DTexData>(room[BspLump.TexData]);
            int[] map = new int[datas.Length];
            for (int i = 0; i < datas.Length; i++)
            {
                DTexData data = datas[i];
                data.NameStringTableId = Remap(stringMap, data.NameStringTableId);
                map[i] = InternTexData(data);
            }

            return map;
        }

        /// <summary>
        /// Every texinfo of a room (already moved to its placement), as the
        /// linked texinfo with the same content, its texdata remapped through
        /// <paramref name="texDataMap"/>.
        /// </summary>
        public int[] InternTexInfos(ReadOnlySpan<TexInfo> infos, int[] texDataMap)
        {
            int[] map = new int[infos.Length];
            for (int i = 0; i < infos.Length; i++)
            {
                TexInfo info = infos[i];
                info.TexData = Remap(texDataMap, info.TexData);
                map[i] = InternTexInfo(info);
            }

            return map;
        }

        /// <summary>The linked index of a texinfo already in the linked numbering (its texdata linked).</summary>
        public int InternTexInfo(TexInfo info)
        {
            byte[] key = TexInfoKey(info);
            if (!_texInfos.TryGetValue(key, out int at))
            {
                at = TexInfos.Count;
                TexInfos.Add(info);
                _texInfos[key] = at;
            }

            return at;
        }

        private int InternTexData(DTexData data)
        {
            TexDataKey key = new(
                Bits(data.Reflectivity.X), Bits(data.Reflectivity.Y), Bits(data.Reflectivity.Z),
                data.NameStringTableId, data.Width, data.Height, data.ViewWidth, data.ViewHeight);
            if (!_texDatas.TryGetValue(key, out int at))
            {
                at = TexDatas.Count;
                TexDatas.Add(data);
                _texDatas[key] = at;
            }

            return at;
        }

        private int InternString(string value)
        {
            if (!_strings.TryGetValue(value, out int at))
            {
                at = StringTable.Count;
                StringTable.Add(StringData.Count);
                StringData.AddRange(Encoding.Latin1.GetBytes(value));
                StringData.Add(0);
                _strings[value] = at;
            }

            return at;
        }

        /// <summary>
        /// The string at one table entry: the bytes from its offset to the
        /// first NUL (or the end of the data), one char per byte so the
        /// comparison is of the bytes themselves.
        /// </summary>
        private static string TableString(ReadOnlySpan<byte> data, int offset, int entry, string room)
        {
            if (offset < 0 || offset > data.Length)
            {
                throw new LinkException(
                    $"room {room}'s texdata string table entry {entry} points at byte {offset}, outside its {data.Length} bytes of string data");
            }

            ReadOnlySpan<byte> rest = data[offset..];
            int end = rest.IndexOf((byte)0);
            return Encoding.Latin1.GetString(end < 0 ? rest : rest[..end]);
        }

        /// <summary>A texinfo's bytes with every float's <c>-0</c> folded to <c>+0</c>.</summary>
        private static byte[] TexInfoKey(TexInfo info)
        {
            for (int i = 0; i < 8; i++)
            {
                info.TextureVecsTexelsPerWorldUnits[i] = Fold(info.TextureVecsTexelsPerWorldUnits[i]);
                info.LightmapVecsLuxelsPerWorldUnits[i] = Fold(info.LightmapVecsLuxelsPerWorldUnits[i]);
            }

            return MemoryMarshal.AsBytes(new ReadOnlySpan<TexInfo>(in info)).ToArray();
        }

        private static float Fold(float value) => value == 0f ? 0f : value;
    }

    /// <summary>
    /// A room-local index through a room's map into a shared table; an index
    /// outside the room's table names nothing in the linked one, so it
    /// becomes -1 ("none").
    /// </summary>
    /// <remarks>
    /// A negative index is already "none". An index past the end is what vbsp
    /// leaves in an original face after compacting the texinfo table (see
    /// <c>MarkPlugOriginalFaces</c>): stale, read by nothing, and with no
    /// room base to shift by any more, so it is written as unset rather than
    /// left to point at some other room's entry.
    /// </remarks>
    internal static int Remap(int[] map, int index) =>
        index >= 0 && index < map.Length ? map[index] : -1;

    /// <summary>A texdata's identity: its reflectivity (folded), linked name, and sizes.</summary>
    private readonly record struct TexDataKey(uint R, uint G, uint B, int Name, int Width, int Height, int ViewWidth, int ViewHeight);

    /// <summary>Compares byte arrays by content.</summary>
    private sealed class ByteContent : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj)
        {
            HashCode hash = default;
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
