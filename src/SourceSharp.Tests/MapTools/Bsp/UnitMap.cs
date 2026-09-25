using System.Globalization;

using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// A compile with no disk in it: a few VMTs in memory and a VMF built in code.
/// </summary>
/// <remarks>
/// <para>
/// The unit tier the plan asks for. A fact that needs an installed game,
/// Wine and a Steam depot can only run on one machine; a fact built here runs
/// everywhere and in milliseconds, so the ordering rules that decide the file
/// format are checked on every <c>make test</c> and not only where the stock
/// reference lives.
/// </para>
/// <para>
/// The materials here are deliberately thin — a shader name and a compile var
/// is all <c>FindMiptex</c> reads — because the material READER is gated
/// elsewhere against 483 real materials. What is being checked here is what
/// this lane does with the answer.
/// </para>
/// </remarks>
internal static class UnitMap
{
    /// <summary>A material with no special compile vars.</summary>
    public const string Plain = "unit/plain";

    /// <summary>A <c>%compileNoDraw</c> material.</summary>
    public const string NoDraw = "unit/nodraw";

    /// <summary>A <c>%compileHint</c> material.</summary>
    public const string Hint = "unit/hint";

    /// <summary>A <c>%compileWater</c> material.</summary>
    public const string Water = "unit/water";

    /// <summary>A <c>%compileOrigin</c> material.</summary>
    public const string Origin = "unit/origin";

    /// <summary>A <c>%compileClip</c> material.</summary>
    public const string Clip = "unit/clip";

    /// <summary>
    /// A <c>%compileBlockLOS</c> material: <c>CONTENTS_BLOCKLOS</c> alone.
    /// </summary>
    /// <remarks>
    /// The one contents bit inside stock's <c>ALL_VISIBLE_CONTENTS</c> that no
    /// NAMED flag in the port's spelling of that mask covered, so it is the
    /// only material that can show whether the mask is the contiguous
    /// <c>0xFF</c> stock writes or an OR of names.
    /// </remarks>
    public const string BlockLos = "unit/blocklos";

    /// <summary>Builds a context over the in-memory materials.</summary>
    /// <param name="options">The compile's switches, or null for the default.</param>
    /// <returns>The context.</returns>
    public static async Task<VbspContext> ContextAsync(VbspOptions? options = null)
    {
        InMemoryFileSystem files = new();

        Add(files, Plain, "LightmappedGeneric", null);
        Add(files, NoDraw, "LightmappedGeneric", "%compileNoDraw");
        Add(files, Hint, "LightmappedGeneric", "%compileHint");
        Add(files, Water, "Water", "%compileWater");
        Add(files, Origin, "LightmappedGeneric", "%compileOrigin");
        Add(files, Clip, "LightmappedGeneric", "%compileClip");
        Add(files, BlockLos, "LightmappedGeneric", "%compileBlockLOS");

        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(files, VPath.Empty);
        ContentFileSystem content = new([mount]);

        return new VbspContext(options ?? VbspOptions.Default, content) { MapBase = "unit" };
    }

    /// <summary>
    /// A VMF with one worldspawn holding one axis-aligned box brush.
    /// </summary>
    /// <param name="material">The material every side uses.</param>
    /// <param name="mins">The box minimum.</param>
    /// <param name="maxs">The box maximum.</param>
    /// <param name="className">The entity classname, defaulting to worldspawn.</param>
    /// <returns>The document.</returns>
    public static VmfDocument BoxMap(
        string material,
        (float X, float Y, float Z) mins,
        (float X, float Y, float Z) maxs,
        string className = "worldspawn")
    {
        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", className);
        world.Children.Add(Box(material, mins, maxs, 1));
        document.Chunks.Add(world);
        return document;
    }

    /// <summary>
    /// One <c>solid</c> chunk: an axis-aligned box with six sides.
    /// </summary>
    /// <param name="material">The material every side uses.</param>
    /// <param name="mins">The box minimum.</param>
    /// <param name="maxs">The box maximum.</param>
    /// <param name="id">The brush's VMF id.</param>
    /// <returns>The chunk.</returns>
    /// <remarks>
    /// The three plane points of each face are given in the winding order
    /// Hammer writes, so the normals face OUT of the box. Getting that
    /// backwards inverts the brush, which is exactly what
    /// <c>PlaneFromPoints</c>'s operand order decides.
    /// </remarks>
    public static VmfChunk Box(
        string material,
        (float X, float Y, float Z) mins,
        (float X, float Y, float Z) maxs,
        int id)
    {
        VmfChunk solid = new(MapFileLoader.SolidChunk);
        solid.AddKey("id", id.ToString(CultureInfo.InvariantCulture));

        (float x0, float y0, float z0) = mins;
        (float x1, float y1, float z1) = maxs;

        // +Z, -Z, +X, -X, +Y, -Y, each as three points wound so the normal
        // points away from the box's interior.
        Add(solid, material, (x0, y1, z1), (x1, y1, z1), (x1, y0, z1));
        Add(solid, material, (x0, y0, z0), (x1, y0, z0), (x1, y1, z0));
        Add(solid, material, (x1, y1, z1), (x1, y1, z0), (x1, y0, z0));
        Add(solid, material, (x0, y1, z0), (x0, y1, z1), (x0, y0, z1));
        Add(solid, material, (x1, y1, z1), (x0, y1, z1), (x0, y1, z0));
        Add(solid, material, (x0, y0, z1), (x1, y0, z1), (x1, y0, z0));

        return solid;
    }

    /// <summary>Adds one <c>side</c> chunk to a solid.</summary>
    /// <param name="solid">The solid.</param>
    /// <param name="material">The side's material.</param>
    /// <param name="p0">The first plane point.</param>
    /// <param name="p1">The second plane point.</param>
    /// <param name="p2">The third plane point.</param>
    /// <returns>The side chunk.</returns>
    public static VmfChunk Add(
        VmfChunk solid,
        string material,
        (float X, float Y, float Z) p0,
        (float X, float Y, float Z) p1,
        (float X, float Y, float Z) p2)
    {
        VmfChunk side = solid.AddChunk(MapFileLoader.SideChunk);
        side.AddKey("plane", $"({F(p0)}) ({F(p1)}) ({F(p2)})");
        side.AddKey("material", material);
        side.AddKey("uaxis", "[1 0 0 0] 0.25");
        side.AddKey("vaxis", "[0 -1 0 0] 0.25");
        side.AddKey("rotation", "0");
        side.AddKey("lightmapscale", "16");
        side.AddKey("smoothing_groups", "0");
        return side;
    }

    private static string F((float X, float Y, float Z) p) =>
        string.Create(CultureInfo.InvariantCulture, $"{p.X} {p.Y} {p.Z}");

    private static void Add(InMemoryFileSystem files, string name, string shader, string? compileVar)
    {
        string body = compileVar is null ? string.Empty : $"\t\"{compileVar}\" \"1\"\n";

        files.AddText(
            $"materials/{name}.vmt",
            $"\"{shader}\"\n{{\n\t\"$basetexture\" \"unit/missing\"\n{body}}}\n");
    }
}
