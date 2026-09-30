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
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Rooms with displacements: patches of terrain on the harness rooms'
/// floors, and the room compile (with the managed cooker, so the rooms carry
/// displacement collision), link and flattened compile the displacement
/// facts run.
/// </summary>
/// <remarks>
/// The harness rooms are <see cref="RoomPropHarness.Hub"/> and
/// <see cref="RoomPropHarness.Other"/>, on the walkable kit: a 256-unit
/// cell whose floor's top is at z = 16. A patch is a thin brush standing on
/// the floor whose top side is a displacement.
/// </remarks>
internal static class RoomDisplacementHarness
{
    /// <summary>A patch's brush id: its sides are this times 8.</summary>
    public const int PatchBrush = 6000;

    /// <summary>
    /// A brush on the floor whose top side is a displacement of the given
    /// power: every vertex raised along +z by a height that varies over the
    /// grid, and shifted a little by an offset, so neither the distances nor
    /// the offsets are uniform; its start the top face's corner at the box's
    /// low x and y.
    /// </summary>
    /// <param name="id">The brush's id; its sides take <c>id x 8</c>.</param>
    /// <param name="box">The brush, room-local.</param>
    /// <param name="power">The displacement's power, 2 to 4.</param>
    /// <param name="seed">Varies the heights between patches.</param>
    /// <param name="offsets">
    /// Whether some vertices are shifted sideways too; off for a patch whose
    /// edge must stay on its brush's box (a shift can carry an edge vertex
    /// half a unit past it).
    /// </param>
    /// <param name="heights">
    /// The distance at each (row, column) in place of the varying few units,
    /// for a patch with steep parts (a ridge a player cannot stand on).
    /// </param>
    public static VmfChunk Patch(int id, Box box, int power = 2, int seed = 0, bool offsets = true, Func<int, int, float>? heights = null)
    {
        VmfChunk solid = RoomModel.Slab(RoomHarness.Plain, box.Mins, box.Maxs, id);
        VmfChunk top = solid.Chunks.First();
        int n = (1 << power) + 1;
        VmfChunk disp = top.AddChunk("dispinfo");
        disp.AddKey("power", power.ToString(CultureInfo.InvariantCulture));
        disp.AddKey("startposition", $"[{VmfPlacement.Format(new Vec3(box.Mins.X, box.Mins.Y, box.Maxs.Z))}]");
        disp.AddKey("flags", "0");
        disp.AddKey("elevation", "0");
        disp.AddKey("subdiv", "0");
        VmfChunk normals = disp.AddChunk("normals");
        VmfChunk distances = disp.AddChunk("distances");
        VmfChunk offsetRows = disp.AddChunk("offsets");
        VmfChunk offsetNormals = disp.AddChunk("offset_normals");
        VmfChunk alphas = disp.AddChunk("alphas");
        VmfChunk tags = disp.AddChunk("triangle_tags");
        for (int row = 0; row < n; row++)
        {
            string key = "row" + row.ToString(CultureInfo.InvariantCulture);
            normals.AddKey(key, Repeat("0 0 1", n));
            offsetNormals.AddKey(key, Repeat("0 0 1", n));
            distances.AddKey(key, string.Join(' ', Enumerable.Range(0, n).Select(c => F(heights?.Invoke(row, c) ?? Height(row, c, seed)))));
            offsetRows.AddKey(key, string.Join(' ', Enumerable.Range(0, n).Select(c => offsets && (row + c + seed) % 3 == 0 ? "0.5 -0.25 0" : "0 0 0")));
            alphas.AddKey(key, string.Join(' ', Enumerable.Range(0, n).Select(c => F(((row * 37) + (c * 11)) % 256))));
            if (row < n - 1)
            {
                tags.AddKey(key, Repeat("9", 2 * (n - 1)));
            }
        }

        disp.AddChunk("allowed_verts").AddKey("10", Repeat("-1", 10));
        return solid;

        static string Repeat(string text, int count) => string.Join(' ', Enumerable.Repeat(text, count));

        static string F(float value) => VmfPlacement.Format(value);
    }

    /// <summary>A patch's height at a vertex: a few units, never the same twice in a row.</summary>
    private static float Height(int row, int column, int seed) => 1 + ((((row * 3) + (column * 5) + seed) % 7) * 1.25f);

    /// <summary>The hub's two patches, side by side so they share an edge (vbsp makes them neighbours).</summary>
    public static Box HubWest { get; } = new(new Vec3(64, 64, 16), new Vec3(128, 176, 24));

    /// <summary>The hub's second patch, east of the first.</summary>
    public static Box HubEast { get; } = new(new Vec3(128, 64, 16), new Vec3(192, 176, 24));

    /// <summary>The other room's patch, off the cell's centre.</summary>
    public static Box OtherPatch { get; } = new(new Vec3(40, 120, 16), new Vec3(136, 200, 20));

    /// <summary>The hub's patches and the other room's, as library brushes of rooms 0 and 1.</summary>
    public static (int Room, VmfChunk Solid)[] Patches =>
    [
        (0, Patch(PatchBrush, HubWest)),
        (0, Patch(PatchBrush + 1, HubEast, seed: 2)),
        (1, Patch(PatchBrush + 2, OtherPatch, power: 3, seed: 5)),
    ];

    /// <summary>
    /// The harness library of <see cref="RoomPropHarness.Hub"/> and
    /// <see cref="RoomPropHarness.Other"/> with each extra brush added to
    /// room 0 (hub) or 1 (other)'s world at its room-local place, and each
    /// extra entity as <see cref="RoomPropHarness.Library"/> adds it.
    /// </summary>
    public static VmfDocument Library((int Room, VmfChunk Solid)[] solids, params (int Room, VmfChunk Entity)[] entities)
    {
        VmfDocument library = RoomPropHarness.Library(entities);
        VmfChunk world = library.GetChunk(MapFileLoader.WorldChunk)!;
        foreach ((int room, VmfChunk solid) in solids)
        {
            Vec3 corner = new(room * (RoomHarness.Cell + RoomHarness.LibraryGap), 0, 0);
            world.Children.Add(VmfPlacement.MoveSolid(solid, QuarterTurn.Translation(corner)));
        }

        return library;
    }

    /// <summary>A compile context whose content holds the harness materials, with the managed cooker.</summary>
    public static async Task<VbspContext> ContextAsync(ManagedCollisionCooker? cooker, string mapBase = "roomtest", int degree = 1)
    {
        VbspContext context = await RoomHarness.ContextAsync();
        context.MapBase = mapBase;
        context.Parallelism = new CompileParallelism { MaxDegree = degree };
        context.CollisionCooker = cooker;
        return context;
    }

    /// <summary>The library's rooms compiled as <c>ssmap room</c> compiles them, cooked unless told otherwise.</summary>
    public static async Task<RoomLibrary> CompileAsync(VmfDocument library, int degree = 1, bool cook = true)
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        RoomLibrary compiled = new(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize)
        {
            LibraryEntities = split.LibraryEntities,
            Options = split.Options,
        };
        await using ManagedCollisionCooker? cooker = cook ? ManagedCollisionCooker.Create(ComplianceOptions.Correct) : null;
        foreach (LibraryRoom room in split.Rooms)
        {
            compiled.Add(await RoomCompiler.CompileAsync(room.Document, room.Definition, await ContextAsync(cooker, room.Definition.Name, degree)));
        }

        return compiled;
    }

    /// <summary>The level flattened and compiled whole, cooked unless told otherwise.</summary>
    public static async Task<BspData> CompileFlatAsync(VmfDocument library, LevelGrid level, bool cook = true)
    {
        await using ManagedCollisionCooker? cooker = cook ? ManagedCollisionCooker.Create(ComplianceOptions.Correct) : null;
        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, library), await ContextAsync(cooker, "flat"));
        Assert.NotNull(whole.Bsp);
        return whole.Bsp!;
    }

    /// <summary>A map's displacement records.</summary>
    public static DispInfo[] Infos(BspData bsp) => BspStructView.As<DispInfo>(bsp[BspLump.DispInfo]).ToArray();

    /// <summary>A map's displacement surfaces as vrad and the engine build them from its lumps.</summary>
    public static CoreDispInfo[] Surfaces(BspData bsp) => DispLightingLoader.Load(bsp, ComplianceOptions.Correct);

    /// <summary>
    /// A map's displacements in what does not depend on where the map's
    /// compile put them, bit for bit: power, flags, smoothing angle,
    /// contents, allowed vertices, the neighbours (by index, orientation and
    /// span), each vertex's distance and alpha, the triangle tags, the
    /// sample positions, the collision hull, the base face's material and
    /// lightmap size; one line each, in lump order.
    /// </summary>
    public static List<string> Observed(BspData bsp)
    {
        DispInfo[] infos = Infos(bsp);
        DispVert[] verts = BspStructView.As<DispVert>(bsp[BspLump.DispVerts]).ToArray();
        DispTri[] tris = BspStructView.As<DispTri>(bsp[BspLump.DispTris]).ToArray();
        byte[] samples = bsp[BspLump.DispLightmapSamplePositions].Data.ToArray();
        DFace[] faces = BspStructView.As<DFace>(bsp[BspLump.Faces]).ToArray();
        IReadOnlyList<byte[]?> blobs = RoomDisplacements.CollisionBlobs(bsp);
        List<string> observed = [];
        for (int i = 0; i < infos.Length; i++)
        {
            DispInfo d = infos[i];
            StringBuilder text = new();
            text.Append(CultureInfo.InvariantCulture, $"power {d.Power} flags {d.MinTess:X8} smooth {d.SmoothingAngle} contents {d.Contents}");
            text.Append(" allowed");
            for (int w = 0; w < 10; w++)
            {
                text.Append(CultureInfo.InvariantCulture, $" {d.AllowedVerts[w]:X8}");
            }

            for (int e = 0; e < 4; e++)
            {
                for (int s = 0; s < 2; s++)
                {
                    DispSubNeighbor sub = d.EdgeNeighbors[e].SubNeighbors[s];
                    text.Append(CultureInfo.InvariantCulture, $" e{e}.{s} {sub.Neighbor} {sub.NeighborOrientation} {sub.Span} {sub.NeighborSpan}");
                }

                DispCornerNeighbors corner = d.CornerNeighbors[e];
                text.Append(CultureInfo.InvariantCulture, $" c{e}");
                for (int k = 0; k < corner.NumNeighbors; k++)
                {
                    text.Append(CultureInfo.InvariantCulture, $" {corner.Neighbors[k]}");
                }
            }

            text.Append(" dist");
            for (int v = 0; v < d.NumVerts(); v++)
            {
                DispVert vert = verts[d.DispVertStart + v];
                text.Append(CultureInfo.InvariantCulture, $" {vert.Dist}/{vert.Alpha}");
            }

            text.Append(" tags");
            for (int t = 0; t < d.NumTris(); t++)
            {
                text.Append(CultureInfo.InvariantCulture, $" {tris[d.DispTriStart + t].Tags}");
            }

            DFace face = faces[d.MapFace];
            text.Append(CultureInfo.InvariantCulture, $" face {face.DispInfo} {RoomBrushHarness.Material(bsp, face.TexInfo)} {face.LightmapTextureSizeInLuxels[0]}x{face.LightmapTextureSizeInLuxels[1]}");
            int sampleEnd = i + 1 < infos.Length ? infos[i + 1].LightmapSamplePositionStart : samples.Length;
            text.Append(" samples ").Append(Convert.ToHexString(samples, d.LightmapSamplePositionStart, sampleEnd - d.LightmapSamplePositionStart));
            text.Append(CultureInfo.InvariantCulture, $" hull {(i < blobs.Count ? HullCorners(blobs[i]) : "absent")}");
            observed.Add(text.ToString());
        }

        return observed;
    }

    /// <summary>
    /// The vertices a packed collision hull is built on (<c>PhysDisp</c>'s
    /// entry for one displacement): per hull, the displacement's vertex
    /// indices its edges name, sorted. Two triangulations of one convex hull
    /// have the same corners, so this is the hull whatever triangles a
    /// compile cut it into.
    /// </summary>
    public static string HullCorners(byte[]? blob)
    {
        if (blob is null)
        {
            return "none";
        }

        int hulls = blob[0];
        int at = 4 + (5 * hulls);
        List<string> parts = [];
        for (int h = 0; h < hulls; h++)
        {
            int header = 4 + (5 * h);
            int tris = blob[header], edges = blob[header + 3], minRef = blob[header + 4];
            at += 4 * tris;
            SortedSet<int> corners = [];
            for (int e = 0; e < edges; e++)
            {
                corners.Add(minRef + blob[at + (2 * e)]);
                corners.Add(minRef + blob[at + (2 * e) + 1]);
            }

            at += 2 * edges;
            parts.Add(string.Join(',', corners));
        }

        return string.Join(" | ", parts);
    }

    /// <summary>The largest distance between the two maps' displacement vertices, one to one in lump order.</summary>
    public static float VertexGap(BspData a, BspData b)
    {
        CoreDispInfo[] left = Surfaces(a), right = Surfaces(b);
        Assert.Equal(left.Length, right.Length);
        float gap = 0;
        for (int i = 0; i < left.Length; i++)
        {
            Assert.Equal(left[i].Size, right[i].Size);
            for (int v = 0; v < left[i].Size; v++)
            {
                gap = Math.Max(gap, (left[i].Vert(v) - right[i].Vert(v)).Length());
            }
        }

        return gap;
    }

    /// <summary>The largest angle-free difference between the two maps' vertex normals (the length of their difference).</summary>
    public static float NormalGap(BspData a, BspData b)
    {
        CoreDispInfo[] left = Surfaces(a), right = Surfaces(b);
        float gap = 0;
        for (int i = 0; i < left.Length; i++)
        {
            for (int v = 0; v < left[i].Size; v++)
            {
                gap = Math.Max(gap, (left[i].Normal(v) - right[i].Normal(v)).Length());
            }
        }

        return gap;
    }

    /// <summary>The bytes <c>ssmap link</c> writes for a level.</summary>
    public static Task<byte[]> BytesAsync(LinkedLevel linked) => RoomOverlayHarness.BytesAsync(linked);
}
