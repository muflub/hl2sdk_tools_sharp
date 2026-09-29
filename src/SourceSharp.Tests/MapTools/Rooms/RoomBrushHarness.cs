//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Rooms with brush entities: brush entity builders (world-coordinate and
/// origin-relative), the harness library of <see cref="RoomPropHarness"/>
/// they are added to, the room compile with the managed cooker (so every
/// brush model has its collision record), the link, the flattened level's
/// compile, and what a game observes of each brush model, for the facts
/// that compare the two.
/// </summary>
internal static class RoomBrushHarness
{
    /// <summary>The origin material: <c>%compileOrigin</c>, what makes a brush an origin brush.</summary>
    public const string OriginMaterial = "unit/origin";

    /// <summary>The harness materials plus the origin one.</summary>
    public static IReadOnlyDictionary<string, byte[]> Files() => new Dictionary<string, byte[]>(RoomPropHarness.Models(), StringComparer.Ordinal)
    {
        [$"materials/{OriginMaterial}.vmt"] = Encoding.ASCII.GetBytes(
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileOrigin\" \"1\"\n}\n"),
    };

    /// <summary>A compile context with the harness content and, when asked, the managed cooker.</summary>
    public static async Task<VbspContext> ContextAsync(string mapBase, ManagedCollisionCooker? cooker)
    {
        VbspContext context = await RoomHarness.ContextAsync(extraFiles: Files());
        context.MapBase = mapBase;
        context.Parallelism = new CompileParallelism { MaxDegree = 1 };
        context.CollisionCooker = cooker;
        return context;
    }

    /// <summary>
    /// A brush entity of one box brush at room-local coordinates; with
    /// <paramref name="origin"/>, a second, origin brush centred there
    /// (8 units a side), which makes the model origin-relative.
    /// </summary>
    public static VmfChunk Brush(
        string classname, int id, Vec3 mins, Vec3 maxs, string material = RoomHarness.Plain, Vec3? origin = null, params (string Key, string Value)[] keys)
    {
        VmfChunk entity = new(MapFileLoader.EntityChunk);
        entity.AddKey("id", id.ToString(CultureInfo.InvariantCulture));
        entity.AddKey("classname", classname);
        foreach ((string key, string value) in keys)
        {
            entity.AddKey(key, value);
        }

        entity.Children.Add(RoomModel.Slab(material, mins, maxs, id * 10));
        if (origin is { } o)
        {
            entity.Children.Add(RoomModel.Slab(OriginMaterial, o - new Vec3(4, 4, 4), o + new Vec3(4, 4, 4), (id * 10) + 1));
        }

        return entity;
    }

    /// <summary>A world-coordinate <c>func_door</c> moving along <c>movedir</c>.</summary>
    public static VmfChunk Door(int id, Vec3 mins, Vec3 maxs, params (string Key, string Value)[] keys) =>
        Brush("func_door", id, mins, maxs, RoomHarness.Plain, null, [("movedir", "0 90 0"), .. keys]);

    /// <summary>An origin-relative <c>func_door_rotating</c> hinged at <paramref name="hinge"/>.</summary>
    public static VmfChunk Rotating(int id, Vec3 mins, Vec3 maxs, Vec3 hinge, params (string Key, string Value)[] keys) =>
        Brush("func_door_rotating", id, mins, maxs, RoomHarness.Plain, hinge, keys);

    /// <summary>A <c>trigger_multiple</c> of trigger material.</summary>
    public static VmfChunk Trigger(int id, Vec3 mins, Vec3 maxs, params (string Key, string Value)[] keys) =>
        Brush("trigger_multiple", id, mins, maxs, RoomHarness.Trigger, null, keys);

    /// <summary>The library's rooms compiled with the managed cooker, as <c>ssmap room</c> compiles them.</summary>
    public static async Task<RoomLibrary> CompileAsync(VmfDocument library)
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        RoomLibrary compiled = new(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize)
        {
            LibraryEntities = split.LibraryEntities,
            Options = split.Options,
        };
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        foreach (LibraryRoom room in split.Rooms)
        {
            compiled.Add(await RoomCompiler.CompileAsync(room.Document, room.Definition, await ContextAsync(room.Definition.Name, cooker)));
        }

        return compiled;
    }

    /// <summary>The level flattened and compiled whole with the managed cooker.</summary>
    public static async Task<BspData> CompileFlatAsync(VmfDocument library, LevelGrid level)
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, library), await ContextAsync("flat", cooker));
        Assert.NotNull(whole.Bsp);
        return whole.Bsp!;
    }

    /// <summary>One brush entity of a map as a game meets it: its entity, and its model.</summary>
    /// <param name="Entity">The entity's keys, in lump order.</param>
    /// <param name="Model">Its model's index.</param>
    public sealed record Placed(BspEntity Entity, int Model)
    {
        /// <summary>The entity's origin, zero when it has none.</summary>
        public Vec3 Origin => Entity.Get("origin") is { } text ? Vec(text) : Vec3.Zero;
    }

    /// <summary>A map's brush entities, each with its model, in entity order.</summary>
    public static List<Placed> BrushEntities(BspData bsp)
    {
        List<Placed> placed = [];
        foreach (BspEntity entity in EntityLump.Parse(bsp[BspLump.Entities]))
        {
            if (entity.Get("model") is { Length: > 1 } model && model[0] == '*')
            {
                placed.Add(new Placed(entity, int.Parse(model[1..], CultureInfo.InvariantCulture)));
            }
        }

        return placed;
    }

    /// <summary>The brush entity with a key's value, and its model.</summary>
    public static Placed Named(BspData bsp, string key, string value) =>
        BrushEntities(bsp).Single(p => p.Entity.Get(key) == value);

    /// <summary>
    /// What a game observes of every brush entity of a map (the rooms
    /// design, 4.1: models by class, name and bounds, the same faces per
    /// model, area per material), with the model's number left out, since
    /// the link and the flatten number models in different orders; the
    /// entity's other keys in order, <c>hammerid</c> and <c>model</c> aside.
    /// Sorted, so the two maps are compared whatever order each wrote them in.
    /// </summary>
    public static List<string> Observed(BspData bsp)
    {
        DModel[] models = BspStructView.As<DModel>(bsp[BspLump.Models]).ToArray();
        List<string> observed = [];
        foreach (Placed placed in BrushEntities(bsp))
        {
            DModel model = models[placed.Model];
            Vec3 o = placed.Origin;
            string keys = string.Join(
                ";",
                placed.Entity.Pairs.Where(p => p.Key is not ("hammerid" or "model")).Select(p => $"{p.Key}={p.Value}").Order(StringComparer.Ordinal));
            observed.Add(string.Join(
                " | ",
                keys,
                $"bounds {V(model.Mins + o)} {V(model.Maxs + o)}",
                Faces(bsp, model)));
        }

        observed.Sort(StringComparer.Ordinal);
        return observed;

        static string V(Vec3 v) => string.Create(CultureInfo.InvariantCulture, $"{v.X:0.##} {v.Y:0.##} {v.Z:0.##}");
    }

    /// <summary>A model's faces as area per material, sorted.</summary>
    public static string Faces(BspData bsp, DModel model)
    {
        DFace[] faces = BspStructView.As<DFace>(bsp[BspLump.Faces]).ToArray();
        SortedDictionary<string, float> areas = new(StringComparer.Ordinal);
        for (int f = model.FirstFace; f < model.FirstFace + model.NumFaces; f++)
        {
            string material = Material(bsp, faces[f].TexInfo);
            areas[material] = areas.GetValueOrDefault(material) + faces[f].Area;
        }

        return string.Join(",", areas.Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.Key}:{a.Value:0}")));
    }

    /// <summary>A texinfo's material name.</summary>
    public static string Material(BspData bsp, int texInfo)
    {
        if (texInfo < 0)
        {
            return "<none>";
        }

        TexInfo info = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo])[texInfo];
        DTexData data = BspStructView.As<DTexData>(bsp[BspLump.TexData])[info.TexData];
        int at = BspStructView.As<int>(bsp[BspLump.TexDataStringTable])[data.NameStringTableId];
        ReadOnlySpan<byte> strings = bsp[BspLump.TexDataStringData].Data.Span[at..];
        return Encoding.ASCII.GetString(strings[..strings.IndexOf((byte)0)]);
    }

    /// <summary>The brushes a model's tree lists in its leaves.</summary>
    public static HashSet<int> ModelBrushes(BspData bsp, int model)
    {
        DNode[] nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]).ToArray();
        DLeaf[] leafs = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
        ushort[] leafBrushes = BspStructView.As<ushort>(bsp[BspLump.LeafBrushes]).ToArray();
        HashSet<int> brushes = [];
        Stack<int> pending = new([BspStructView.As<DModel>(bsp[BspLump.Models])[model].HeadNode]);
        while (pending.Count > 0)
        {
            int at = pending.Pop();
            if (at < 0)
            {
                DLeaf leaf = leafs[~at];
                for (int b = 0; b < leaf.NumLeafBrushes; b++)
                {
                    brushes.Add(leafBrushes[leaf.FirstLeafBrush + b]);
                }

                continue;
            }

            pending.Push(nodes[at].Children[0]);
            pending.Push(nodes[at].Children[1]);
        }

        return brushes;
    }

    /// <summary>
    /// Rays through a brush entity's model, in world coordinates, as a game
    /// traces against it at its origin: a grid of rays along each axis over
    /// its world bounds and a margin, each hit as its fraction and the
    /// plane's normal, rounded.
    /// </summary>
    public static List<string> Traces(BspData bsp, Placed placed)
    {
        DModel model = BspStructView.As<DModel>(bsp[BspLump.Models])[placed.Model];
        Vec3 o = placed.Origin;
        Box world = new(model.Mins + o, model.Maxs + o);
        WorldBrushTrace trace = new(bsp, ModelBrushes(bsp, placed.Model));
        List<string> hits = [];
        for (int axis = 0; axis < 3; axis++)
        {
            for (float u = 0.25f; u < 1f; u += 0.25f)
            {
                for (float v = 0.25f; v < 1f; v += 0.25f)
                {
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        Vec3 start = Point(world, axis, sign < 0 ? -16f : 16f, u, v);
                        Vec3 end = Point(world, axis, sign < 0 ? 16f : -16f, u, v);
                        WorldHit hit = trace.Trace(start - o, end - o, Vec3.Zero, mask: -1);
                        hits.Add(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{hit.Fraction:0.000} {(hit.Plane is { } p ? $"{p.Normal.X + 0f:0} {p.Normal.Y + 0f:0} {p.Normal.Z + 0f:0}" : "-")}"));
                    }
                }
            }
        }

        return hits;

        // A point on the far side of the box along one axis (outside by
        // `outside` past its low face when negative, past its high face when
        // positive), at fractions u and v across the other two.
        static Vec3 Point(Box box, int axis, float outside, float u, float v)
        {
            float[] lo = [box.Mins.X, box.Mins.Y, box.Mins.Z];
            float[] hi = [box.Maxs.X, box.Maxs.Y, box.Maxs.Z];
            float[] p = new float[3];
            int a = (axis + 1) % 3, b = (axis + 2) % 3;
            p[axis] = outside < 0 ? lo[axis] + outside : hi[axis] + outside;
            p[a] = lo[a] + ((hi[a] - lo[a]) * u);
            p[b] = lo[b] + ((hi[b] - lo[b]) * v);
            return new Vec3(p[0], p[1], p[2]);
        }
    }

    /// <summary>
    /// A brush entity's collision as its convexes' boxes in world
    /// coordinates (the record's points put at the entity's origin), sorted:
    /// what the collision merge must carry, compared within a tolerance.
    /// </summary>
    public static List<Box> Convexes(BspData bsp, Placed placed)
    {
        List<Box> boxes = [];
        foreach (PhysCollideModel record in PhysCollideLump.Read(bsp[BspLump.PhysCollide].Data.Span))
        {
            if (record.ModelIndex != placed.Model)
            {
                continue;
            }

            foreach (byte[] blob in record.Solids)
            {
                foreach (IvpCompactLedge ledge in IvpCollideQueries.Leaves(IvpCollideQueries.Surface(blob)))
                {
                    Box box = LinkedBrushProbe.LedgeBox(ledge);
                    boxes.Add(new Box(box.Mins + placed.Origin, box.Maxs + placed.Origin));
                }
            }
        }

        return [.. boxes.OrderBy(b => b.Mins.X).ThenBy(b => b.Mins.Y).ThenBy(b => b.Mins.Z)];
    }

    /// <summary>Three numbers, as a key writes them.</summary>
    public static Vec3 Vec(string text)
    {
        float[] parts = [.. text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => float.Parse(p, CultureInfo.InvariantCulture))];
        return new Vec3(parts[0], parts[1], parts[2]);
    }
}
