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
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Rooms with overlays: an overlay material, <c>info_overlay</c>s on the
/// harness rooms' floors and on a detail mat, and the room compile, link and
/// flattened compile the overlay facts run.
/// </summary>
/// <remarks>
/// The harness rooms are <see cref="RoomPropHarness.Hub"/> and
/// <see cref="RoomPropHarness.Other"/>, on the walkable kit. A room model's
/// brush writes one id on all six of its sides and its top side first, so
/// an overlay naming a brush's side id lands on its top (vbsp takes the first
/// side with the id), which is what every overlay here wants.
/// </remarks>
internal static class RoomOverlayHarness
{
    /// <summary>The overlay material.</summary>
    public const string OverlayMaterial = "unit/overlay";

    /// <summary>The side id of every room's floor top: the room model's first brush is the floor (id 1,000,000, sides id x 8).</summary>
    public const int FloorSide = 8_000_000;

    /// <summary>A mat's brush id: its sides are this times 8.</summary>
    public const int MatBrush = 5000;

    /// <summary>The mat's side id.</summary>
    public const int MatSide = MatBrush * 8;

    /// <summary>Where the mat stands in its room: a 64 x 48 box on the floor, off the cell's centre.</summary>
    public static Box Mat { get; } = new(new Vec3(40, 120, 16), new Vec3(104, 168, 24));

    /// <summary>The overlay material's file, beside the harness's own.</summary>
    public static IReadOnlyDictionary<string, byte[]> Files() => new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        [$"materials/{OverlayMaterial}.vmt"] = Encoding.ASCII.GetBytes("\"UnlitGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"$decal\" \"1\"\n}\n"),
    };

    /// <summary>A compile context whose content holds the harness materials and the overlay's.</summary>
    public static async Task<VbspContext> ContextAsync(string mapBase = "roomtest", int degree = 1)
    {
        VbspContext context = await RoomHarness.ContextAsync(extraFiles: Files());
        context.MapBase = mapBase;
        context.Parallelism = new CompileParallelism { MaxDegree = degree };
        return context;
    }

    /// <summary>
    /// An <c>info_overlay</c> lying flat, facing up, at a room-local point:
    /// a 32 x 32 square centred on it, on the sides named.
    /// </summary>
    public static VmfChunk Overlay(int id, Vec3 at, string sides, params (string Key, string Value)[] keys) =>
        OverlayWithBasis(id, at, sides, "1 0 0", "0 1 0", "0 0 1", keys);

    /// <summary>An <c>info_overlay</c> with its basis written out: the square of <see cref="Overlay"/> in that basis.</summary>
    public static VmfChunk OverlayWithBasis(
        int id, Vec3 at, string sides, string u, string v, string normal, params (string Key, string Value)[] keys)
    {
        VmfChunk entity = RoomPropHarness.Entity("info_overlay", id, at);
        entity.AddKey("material", OverlayMaterial);
        entity.AddKey("sides", sides);
        entity.AddKey("RenderOrder", "0");
        entity.AddKey("StartU", "0");
        entity.AddKey("EndU", "1");
        entity.AddKey("StartV", "0");
        entity.AddKey("EndV", "1");
        entity.AddKey("BasisOrigin", VmfPlacement.Format(at));
        entity.AddKey("BasisU", u);
        entity.AddKey("BasisV", v);
        entity.AddKey("BasisNormal", normal);
        entity.AddKey("uv0", "-16 -16 0");
        entity.AddKey("uv1", "-16 16 0");
        entity.AddKey("uv2", "16 16 0");
        entity.AddKey("uv3", "16 -16 0");
        entity.AddKey("angles", "0 0 0");
        foreach ((string key, string value) in keys)
        {
            entity.AddKey(key, value);
        }

        return entity;
    }

    /// <summary>A <c>func_detail</c> mat (<see cref="Mat"/>): world geometry whose top side is <see cref="MatSide"/>.</summary>
    public static VmfChunk MatEntity()
    {
        VmfChunk entity = new(MapFileLoader.EntityChunk);
        entity.AddKey("id", "4999");
        entity.AddKey("classname", "func_detail");
        entity.Children.Add(RoomModel.Slab(RoomHarness.Plain, Mat.Mins, Mat.Maxs, MatBrush));
        return entity;
    }

    /// <summary>
    /// The harness library with the extra entities (room 0 is the hub, 1 the
    /// other) at room-local coordinates, every brush's sides numbered apart
    /// (<see cref="NumberSides"/>).
    /// </summary>
    public static VmfDocument Library(params (int Room, VmfChunk Entity)[] extra)
    {
        VmfDocument library = RoomPropHarness.Library(extra);
        foreach (VmfChunk chunk in library.Chunks)
        {
            NumberSides(chunk);
        }

        return library;
    }

    /// <summary>
    /// Gives a brush's six sides six ids, the first (its top) keeping the one
    /// the room model wrote on all of them and the rest the next five. vbsp
    /// finds a side by the first side with its id once the loader has sorted
    /// each brush's sides, so an id shared by a brush's sides names whichever
    /// side sorts first, not the top.
    /// </summary>
    private static void NumberSides(VmfChunk chunk)
    {
        foreach (VmfChunk child in chunk.Chunks)
        {
            if (string.Equals(child.Name, MapFileLoader.SolidChunk, StringComparison.OrdinalIgnoreCase))
            {
                int k = 0;
                foreach (VmfChunk side in child.GetChunks(MapFileLoader.SideChunk))
                {
                    VmfKey id = side.Keys.First(key => key.Name == "id");
                    id.Value = (int.Parse(id.Value, CultureInfo.InvariantCulture) + k++).ToString(CultureInfo.InvariantCulture);
                }
            }
            else
            {
                NumberSides(child);
            }
        }
    }

    /// <summary>The library's rooms compiled as <c>ssmap room</c> compiles them, each on <paramref name="degree"/> threads.</summary>
    public static async Task<RoomLibrary> CompileAsync(VmfDocument library, int degree = 1)
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        RoomLibrary compiled = new(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize)
        {
            LibraryEntities = split.LibraryEntities,
            Options = split.Options,
        };
        foreach (LibraryRoom room in split.Rooms)
        {
            compiled.Add(await RoomCompiler.CompileAsync(room.Document, room.Definition, await ContextAsync(room.Definition.Name, degree)));
        }

        return compiled;
    }

    /// <summary>A level linked from compiled rooms, with a context that has no game files.</summary>
    public static Task<LinkedLevel> LinkAsync(RoomLibrary library, LevelGrid level, int degree = 1) =>
        RoomPropHarness.LinkAsync(library, level, degree);

    /// <summary>The level flattened and compiled whole.</summary>
    public static async Task<BspData> CompileFlatAsync(VmfDocument library, LevelGrid level)
    {
        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, library), await ContextAsync("flat"));
        Assert.NotNull(whole.Bsp);
        return whole.Bsp!;
    }

    /// <summary>A map's overlay records.</summary>
    public static DOverlay[] Overlays(BspData bsp) => BspStructView.As<DOverlay>(bsp[BspLump.Overlays]).ToArray();

    /// <summary>A map's overlay fade records.</summary>
    public static DOverlayFade[] Fades(BspData bsp) => BspStructView.As<DOverlayFade>(bsp[BspLump.OverlayFades]).ToArray();

    /// <summary>
    /// A map's overlays in what the engine draws of each, but the face
    /// list (the rooms design, 4.9): id, material, origin, basis normal,
    /// the four UV points (with <c>BasisU</c> and the handedness flag), the
    /// extents, render order and fades, bit for bit; and the area of the
    /// overlay's square its faces cover, to the hundredth.
    /// </summary>
    public static List<string> Observed(BspData bsp)
    {
        DOverlay[] overlays = Overlays(bsp);
        DOverlayFade[] fades = Fades(bsp);
        List<string> observed = [];
        for (int i = 0; i < overlays.Length; i++)
        {
            DOverlay o = overlays[i];
            StringBuilder text = new();
            text.Append(CultureInfo.InvariantCulture, $"{o.Id} {RoomBrushHarness.Material(bsp, o.TexInfo)} at {V(o.Origin)} n {V(o.BasisNormal)} uv");
            for (int p = 0; p < 4; p++)
            {
                text.Append(' ').Append(V(o.UvPoints[p]));
            }

            text.Append(CultureInfo.InvariantCulture, $" u {F(o.U[0])} {F(o.U[1])} v {F(o.V[0])} {F(o.V[1])} order {o.GetRenderOrder()}");
            text.Append(CultureInfo.InvariantCulture, $" fade {F(fades[i].FadeDistMinSq)} {F(fades[i].FadeDistMaxSq)}");
            text.Append(CultureInfo.InvariantCulture, $" covers {Covered(bsp, o):0.00}");
            observed.Add(text.ToString());
        }

        return observed;

        static string V(Vec3 v) => $"{F(v.X)} {F(v.Y)} {F(v.Z)}";

        static string F(float f) => BitConverter.SingleToInt32Bits(f).ToString("X8", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The area of an overlay's square (its four UV points in its basis)
    /// that its faces cover: each face's polygon, taken into the overlay's
    /// basis and clipped to the square, summed. A map's faces do not overlap,
    /// so this is the area of the union, whatever faces it was cut into.
    /// </summary>
    public static double Covered(BspData bsp, DOverlay overlay)
    {
        Vec3 n = overlay.BasisNormal;
        Vec3 u = new(overlay.UvPoints[0].Z, overlay.UvPoints[1].Z, overlay.UvPoints[2].Z);
        Vec3 v = Vec3.Cross(n, u) * (overlay.UvPoints[3].Z == 1f ? -1f : 1f);
        List<(double X, double Y)> square = [.. Enumerable.Range(0, 4).Select(p => ((double)overlay.UvPoints[p].X, (double)overlay.UvPoints[p].Y))];
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        double total = 0;
        for (int f = 0; f < overlay.GetFaceCount(); f++)
        {
            List<(double X, double Y)> polygon = [.. RoomHarness.FaceVertices(bsp, faces[overlay.Faces[f]])
                .Select(p => p - overlay.Origin)
                .Select(d => ((double)Vec3.Dot(d, u), (double)Vec3.Dot(d, v)))];
            total += Math.Abs(Area(Clip(polygon, square)));
        }

        return total;
    }

    /// <summary>A polygon clipped to a convex one (Sutherland-Hodgman), either winding.</summary>
    private static List<(double X, double Y)> Clip(List<(double X, double Y)> polygon, List<(double X, double Y)> convex)
    {
        double sign = Math.Sign(Area(convex));
        List<(double X, double Y)> output = polygon;
        for (int e = 0; e < convex.Count && output.Count > 0; e++)
        {
            (double X, double Y) a = convex[e];
            (double X, double Y) b = convex[(e + 1) % convex.Count];
            double Side((double X, double Y) p) => sign * (((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X)));
            List<(double X, double Y)> input = output;
            output = [];
            for (int i = 0; i < input.Count; i++)
            {
                (double X, double Y) p = input[i];
                (double X, double Y) q = input[(i + 1) % input.Count];
                double sp = Side(p), sq = Side(q);
                if (sp >= 0)
                {
                    output.Add(p);
                }

                if ((sp >= 0) != (sq >= 0))
                {
                    double t = sp / (sp - sq);
                    output.Add((p.X + (t * (q.X - p.X)), p.Y + (t * (q.Y - p.Y))));
                }
            }
        }

        return output;
    }

    private static double Area(List<(double X, double Y)> polygon)
    {
        double twice = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            (double X, double Y) p = polygon[i];
            (double X, double Y) q = polygon[(i + 1) % polygon.Count];
            twice += (p.X * q.Y) - (q.X * p.Y);
        }

        return twice / 2;
    }

    /// <summary>A map's entities of one class, in lump order.</summary>
    public static List<BspEntity> OfClass(BspData bsp, string classname) =>
        [.. EntityLump.Parse(bsp[BspLump.Entities]).Where(e => e.ClassName == classname)];

    /// <summary>The bytes <c>ssmap link</c> writes for a level.</summary>
    public static async Task<byte[]> BytesAsync(LinkedLevel linked)
    {
        using MemoryStream bytes = new();
        await BspFile.SaveAsync(linked.Bsp, bytes, BspWriteMode.Canonical);
        return bytes.ToArray();
    }
}
