using SourceSharp.MapGen;
using System.Globalization;

using SourceSharp.MapFormats.Geometry;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Light;

/// <summary>One worldlight record exactly as stock vrad wrote it.</summary>
/// <param name="Type"><c>emittype_t</c> as an integer.</param>
/// <param name="Style">The lightstyle.</param>
/// <param name="Cluster">The vis cluster.</param>
/// <param name="Origin">The light's position.</param>
/// <param name="Intensity">Its colour, already divided by 255.</param>
/// <param name="Normal">Its direction.</param>
/// <param name="StopDot">The inner cone cosine.</param>
/// <param name="StopDot2">The outer cone cosine.</param>
/// <param name="Exponent">The spot falloff exponent.</param>
/// <param name="Radius">The <c>_distance</c> cutoff.</param>
/// <param name="ConstantAttn">The constant attenuation term.</param>
/// <param name="LinearAttn">The linear term.</param>
/// <param name="QuadraticAttn">The quadratic term.</param>
/// <param name="Flags">Always zero out of vrad.</param>
/// <param name="TexInfo">Never assigned by vrad; see the remarks.</param>
/// <param name="Owner">Never assigned by vrad.</param>
/// <remarks>
/// Every field is recorded to full round-trip precision, so the comparison is
/// on EXACT floats and not on a tolerance. That is deliberate: these numbers
/// come out of a chain of cosines, powers and divisions, and a tolerance wide
/// enough to absorb a wrong formula is wider than the one needed to absorb
/// rounding.
/// </remarks>
internal readonly record struct StockWorldLight(
    int Type,
    int Style,
    int Cluster,
    Vec3 Origin,
    Vec3 Intensity,
    Vec3 Normal,
    float StopDot,
    float StopDot2,
    float Exponent,
    float Radius,
    float ConstantAttn,
    float LinearAttn,
    float QuadraticAttn,
    int Flags,
    int TexInfo,
    int Owner);

/// <summary>What stock vrad reported and produced for one catalogue map.</summary>
/// <param name="Name">The map, without extension.</param>
/// <param name="Faces">The <c>%i faces</c> line.</param>
/// <param name="PatchesBefore">The <c>%i patches before subdivision</c> line.</param>
/// <param name="PatchesAfter">The <c>%i patches after subdivision</c> line.</param>
/// <param name="DirectLights">The <c>%i direct lights</c> line.</param>
/// <param name="DegenerateFaces">The <c>%d degenerate faces</c> line, or zero.</param>
/// <param name="AreaSquareInches">
/// The square-inch half of the <c>%i square feet [%.2f square inches]</c> line,
/// kept as the STRING stock printed.
/// </param>
/// <param name="WorldLights">LUMP_WORLDLIGHTS, in file order.</param>
internal sealed record StockVradMap(
    string Name,
    int Faces,
    int PatchesBefore,
    int PatchesAfter,
    int DirectLights,
    int DegenerateFaces,
    string AreaSquareInches,
    IReadOnlyList<StockWorldLight> WorldLights,
    IReadOnlyList<StockWorldLight> WorldLightsHdr);

/// <summary>
/// The committed record of what stock vrad did to the catalogue.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a committed fixture rather than running stock in the test.</b> Stock
/// vrad is a Windows DLL driven under Wine against a Steam depot; no CI that
/// runs <c>dotnet test</c> has any of that. The numbers are recorded once and
/// reviewed in the diff, and the INPUT maps are located at run time -- so the
/// gate is red when the port disagrees with a stock answer a human can read,
/// rather than green because a tool was missing.
/// </para>
/// <para>
/// <b>What the oracle actually responds to, verified rather than assumed.</b>
/// Phase 4b found a gate that did not move when its subject changed by 4x, so
/// each of these five numbers was checked against a deliberate perturbation of
/// the thing it claims to measure:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>patches after subdivision</c> moves with <c>-chop</c>: stock on
/// <c>l1_two_rooms_and_a_door</c> gives 2,632 at the default and a different
/// count at <c>-chop 16</c>. It is the number the subdivision rules decide and
/// nothing else feeds it.
/// </description></item>
/// <item><description>
/// <c>square inches</c> is a float accumulator over every root patch's winding
/// area, so it moves if any face's winding differs by a sliver -- which is why
/// it is compared as the exact string stock printed rather than as a number
/// with a tolerance.
/// </description></item>
/// <item><description>
/// The worldlights lump is written from <c>activelights</c> in LIST order, so
/// it fails on a wrong ORDER as well as on wrong values -- the one thing a
/// count can never catch.
/// </description></item>
/// </list>
/// <para>
/// The generator scripts sit beside the fixture in <c>Fixtures/</c>:
/// <c>stockvrad.sh</c> runs one map, <c>mkcorpus.sh</c> runs the catalogue and
/// <c>mkfixture.py</c> turns the logs and lit maps into this file.
/// </para>
/// </remarks>
internal static class StockVradReference
{
    /// <summary>The environment variable naming the input maps' directory.</summary>
    internal const string DirectoryVariable = "VVIS_STOCK_DIR";

    private const string FixtureName = "stock-vrad-catalogue.txt";

    /// <summary>Every map in the committed fixture, keyed by name.</summary>
    /// <returns>The recorded stock results.</returns>
    /// <exception cref="InvalidOperationException">
    /// The fixture is missing or malformed. Thrown rather than skipped: it is
    /// a committed file, so its absence means the layout moved.
    /// </exception>
    internal static IReadOnlyDictionary<string, StockVradMap> Load() =>
        Parse(File.ReadAllLines(FixturePath()));

    /// <summary>Where the committed fixture sits in THIS worktree.</summary>
    /// <returns>An absolute path.</returns>
    /// <exception cref="InvalidOperationException">It is not there.</exception>
    internal static string FixturePath()
    {
        string? root = RepoTree.FindRoot(AppContext.BaseDirectory);
        if (root is null)
        {
            throw new InvalidOperationException(
                $"no checkout root at or above \"{AppContext.BaseDirectory}\". The stock vrad "
                + "reference is located relative to the tree this binary was built from, so that "
                + "a worktree compares against ITS OWN fixture and not an enclosing checkout's.");
        }

        string path = Path.Combine(
            root, "src", "SourceSharp.Tests",
            "MapTools", "Rad", "Light", "Fixtures", FixtureName);

        return File.Exists(path)
            ? path
            : throw new InvalidOperationException(
                $"{path} is missing. It is a COMMITTED file, not build output.");
    }

    /// <summary>
    /// The unlit input map stock vrad was given, or null when this tree has no
    /// stock-compiled catalogue.
    /// </summary>
    /// <param name="name">The map name.</param>
    /// <returns>A path, or null.</returns>
    /// <remarks>
    /// <para>
    /// The fixture was made from the catalogue's <c>*.stockvis.bsp</c> files.
    /// vbsp output that stock vvis has already processed -- because vrad needs
    /// vis data and the plain <c>.bsp</c> has none.
    /// </para>
    /// <para>
    /// A map stock vvis could not process -- a leak, or an L0 map with no
    /// portals -- has no <c>*.stockvis.bsp</c>, and stock vrad was run on the
    /// plain vbsp output instead. That <c>.bsp</c> is taken as the input only
    /// when a <c>*.stockrad.bsp</c> exists beside it, so a catalogue that never
    /// ran vrad on the map is still reported as missing rather than silently
    /// compared against something stock was never given.
    /// </remarks>
    internal static string? InputFor(string name) =>
        Existing(Catalogue(name + ".stockvis.bsp"))
        ?? (Existing(Catalogue(name + ".stockrad.bsp")) is null ? null : Existing(Catalogue(name + ".bsp")))
        ?? Existing(Private("in", name + ".bsp"));

    /// <summary>
    /// The map's own <c>.rad</c> (stock's <c>level_lights</c>), when p4c's
    /// private corpus has one.
    /// </summary>
    internal static string? RadFileFor(string name) => Existing(Private("in", name + ".rad"));

    /// <summary>Stock's default-switch output for the map.</summary>
    internal static string? StockOutputFor(string name) =>
        Existing(Catalogue(name + ".stockrad.bsp")) ?? Existing(Private("rad", name + ".bsp"));

    /// <summary>Stock's <c>-both</c> output, for the HDR lumps.</summary>
    internal static string? BothOutputFor(string name) => Existing(Private("both", name + ".bsp"));

    /// <summary>Stock's <c>-bounce 0</c> output: direct light only.</summary>
    internal static string? DirectOnlyOutputFor(string name) => Existing(Private("b0", name + ".bsp"));

    /// <summary>
    /// p4c's private stock corpus: <c>in/</c> (vbsp + vvis inputs and their
    ///.rad), <c>rad/</c>, <c>both/</c> and <c>b0/</c> (stock vrad, default.
    /// <c>-both</c> and <c>-bounce 0</c>). Built by the scripts beside the
    /// fixture; see <c>Fixtures/README</c> in the findings.
    /// </summary>
    internal const string PrivateDirectoryVariable = "P4C_STOCK_DIR";

    private static string? Catalogue(string file) =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } dir
            ? Path.Combine(dir, file)
            : null;

    private static string? Private(string sub, string file) =>
        Environment.GetEnvironmentVariable(PrivateDirectoryVariable) is { Length: > 0 } dir
            ? Path.Combine(dir, sub, file)
            : null;

    private static string? Existing(string? path) => path is not null && File.Exists(path) ? path : null;

    /// <summary>Why a fact needing the input maps cannot run, or null.</summary>
    /// <returns>A skip reason, or null.</returns>
    internal static string? SkipReason() =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 }
            ? null
            : $"no {DirectoryVariable}: this tree has no stock-compiled catalogue. "
                + "See StockVradReference for the recipe.";

    private static IReadOnlyDictionary<string, StockVradMap> Parse(string[] lines)
    {
        Dictionary<string, StockVradMap> maps = [];

        string? name = null;
        int faces = 0, before = 0, after = 0, lights = 0, degenerate = 0;
        string area = string.Empty;
        List<StockWorldLight> worldLights = [];
        List<StockWorldLight> worldLightsHdr = [];

        void Flush()
        {
            if (name is null)
            {
                return;
            }

            maps[name] = new StockVradMap(
                name, faces, before, after, lights, degenerate, area, worldLights, worldLightsHdr);
            worldLights = [];
            worldLightsHdr = [];
        }

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "map":
                    Flush();
                    name = parts[1];
                    break;

                case "counts":
                    faces = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    before = int.Parse(parts[2], CultureInfo.InvariantCulture);
                    after = int.Parse(parts[3], CultureInfo.InvariantCulture);
                    lights = int.Parse(parts[4], CultureInfo.InvariantCulture);
                    degenerate = int.Parse(parts[5], CultureInfo.InvariantCulture);
                    break;

                case "area":
                    area = parts[1];
                    break;

                case "wl":
                    worldLights.Add(ParseWorldLight(parts));
                    break;

                case "wlh":
                    worldLightsHdr.Add(ParseWorldLight(parts));
                    break;

                default:
                    throw new InvalidOperationException(
                        $"{FixtureName}: unknown record \"{parts[0]}\"");
            }
        }

        Flush();

        if (maps.Count == 0)
        {
            throw new InvalidOperationException($"{FixtureName} holds no maps.");
        }

        return maps;
    }

    private static StockWorldLight ParseWorldLight(string[] p)
    {
        // wl <i> <type> <style> <cluster> ox oy oz ir ig ib nx ny nz
        //    stopdot stopdot2 exponent radius constant linear quadratic
        //    flags texinfo owner
        float F(int i) => float.Parse(p[i], CultureInfo.InvariantCulture);
        int I(int i) => int.Parse(p[i], CultureInfo.InvariantCulture);

        return new StockWorldLight(
            I(2), I(3), I(4),
            new Vec3(F(5), F(6), F(7)),
            new Vec3(F(8), F(9), F(10)),
            new Vec3(F(11), F(12), F(13)),
            F(14), F(15), F(16), F(17), F(18), F(19), F(20),
            I(21), I(22), I(23));
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips, visibly, when this tree has no
/// stock-compiled catalogue to feed the port.
/// </summary>
/// <remarks>
/// An early <c>return</c> reports a PASS, and a tree that checked nothing then
/// looks exactly like a tree that checked everything and agreed.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class StockVradFactAttribute : FactAttribute
{
    /// <summary>Decides whether this fact can run.</summary>
    public StockVradFactAttribute() => Skip = StockVradReference.SkipReason();
}

/// <summary>A <see cref="TheoryAttribute"/> that skips when there is no stock catalogue.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class StockVradTheoryAttribute : TheoryAttribute
{
    /// <summary>Decides whether this theory can run.</summary>
    public StockVradTheoryAttribute() => Skip = StockVradReference.SkipReason();
}

/// <summary>
/// A <see cref="TheoryAttribute"/> that also needs p4c's private stock corpus
/// (<c>-both</c> and <c>-bounce 0</c> runs, the texlight fixture).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class StockVradPrivateTheoryAttribute : TheoryAttribute
{
    /// <summary>Decides whether this theory can run.</summary>
    public StockVradPrivateTheoryAttribute() =>
        Skip = StockVradReference.SkipReason()
            ?? (Environment.GetEnvironmentVariable(StockVradReference.PrivateDirectoryVariable) is { Length: > 0 }
                ? null
                : $"no {StockVradReference.PrivateDirectoryVariable}: p4c's private stock corpus is absent");
}
