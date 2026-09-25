using Xunit;
namespace SourceSharp.Tests.MapTools.ToolsPlusPlus;

/// <summary>
/// Where the cross-tool ++ gates find their material: the <c>make-catmaps++</c>
/// corpus (T0's harness, <c>~/.cache/maptools/bin/make-catmaps++</c>) and, for
/// the bspzip++ / stability tiers, the directory holding the ++ executables and
/// the six compatibility DLLs they <c>LoadLibrary</c> from their own working
/// directory.
/// </summary>
/// <remarks>
/// <para>
/// Two environment variables mount the two tiers, and the gates are silent
/// (skipped, not failed) without them, exactly like the stock-reference and
/// <see cref="MapFormats.PpOracle"/> tiers:
/// </para>
/// <list type="bullet">
///   <item><description><c>PP_CATMAPS_DIR</c> — the corpus directory
///   (<c>*.ppvis.bsp</c>, <c>*.pprad.bsp</c> and the per-stage logs). Gates on
///   final outputs only: the corpus <c>*.pprad*</c>/<c>*.ppvis*</c> intermediates
///   are self-inconsistent (declared lumps physically overlap:
///   <c>default/sdk_ctf_2fort.pprad-both.bsp</c> declares 38.4 MB of lumps
///   inside 26.8 MB of file), so nothing here replays or re-parses a lump
///   table from them beyond what <c>BspFile.LoadAsync</c> accepts.</description></item>
///   <item><description><c>PP_TOOLS</c> — the directory with
///   <c>bspzipplusplus.exe</c> and its DLLs (<c>~/.cache/maptools/bin/toolspp</c>
///   in the standing setup). The bspzip++ and stability tiers are driven OUT of
///   band by <c>tools/t6-bspzip-interop.sh</c> and <c>tools/t6-stability.sh</c>
///   (both observe the DLL-in-CWD rule), which write their products under
///   <c>&lt;PP_CATMAPS_DIR&gt;-t6/t6bz/</c> and <c>t6stab/</c>; the facts consume
///   those products and name the regeneration command when they are
///   absent.</description></item>
/// </list>
/// </remarks>
internal static class PpCrossToolHarness
{
    /// <summary>Corpus directory; the standing one is <c>~/.cache/maptools/ref/catmaps-pp</c>.</summary>
    public const string CorpusVariable = "PP_CATMAPS_DIR";

    /// <summary>++ binary directory; the standing one is <c>~/.cache/maptools/bin/toolspp</c>.</summary>
    public const string ToolsVariable = "PP_TOOLS";

    /// <summary>The corpus root, or null when unmounted.</summary>
    public static string? Corpus =>
        Environment.GetEnvironmentVariable(CorpusVariable) is { Length: > 0 } s ? s : null;

    /// <summary>The ++ binary directory, or null when unmounted.</summary>
    public static string? Tools =>
        Environment.GetEnvironmentVariable(ToolsVariable) is { Length: > 0 } s ? s : null;

    /// <summary>Why the corpus tier cannot run, or null.</summary>
    public static string? CorpusSkipReason()
    {
        string? root = Corpus;
        if (root is null)
        {
            return $"no {CorpusVariable}: no tools++ cross-tool corpus (make-catmaps++ output, "
                + "standing corpus ~/.cache/maptools/ref/catmaps-pp).";
        }

        if (!Directory.Exists(root))
        {
            return $"{CorpusVariable}={root} does not exist.";
        }

        return null;
    }

    /// <summary>Why the bspzip++ tier cannot run, or null.</summary>
    public static string? BspZipSkipReason()
    {
        string? why = CorpusSkipReason();
        if (why is not null)
        {
            return why;
        }

        if (Tools is null)
        {
            return $"no {ToolsVariable}: bspzip++ interop needs the ++ binary directory "
                + "(standing one ~/.cache/maptools/bin/toolspp; the exe LoadLibrary's its "
                + "compat DLLs from its own CWD, which tools/t6-bspzip-interop.sh handles).";
        }

        if (!File.Exists(Path.Combine(Tools, "bspzipplusplus.exe")))
        {
            return $"{ToolsVariable}={Tools} has no bspzipplusplus.exe.";
        }

        string dir = ZipDir;
        if (!File.Exists(Path.Combine(dir, "comp.bsp"))
            || !File.Exists(Path.Combine(dir, "roundtrip.bsp"))
            || !File.Exists(Path.Combine(dir, "list-input.txt")))
        {
            return $"{dir} is not produced yet: run tools/t6-bspzip-interop.sh from the "
                + $"worktree root with {CorpusVariable} and {ToolsVariable} exported.";
        }

        return null;
    }

    /// <summary>Why the ++-side stability tier cannot run, or null.</summary>
    public static string? StabilitySkipReason()
    {
        string? why = CorpusSkipReason();
        if (why is not null)
        {
            return why;
        }

        if (Tools is null)
        {
            return $"no {ToolsVariable}: the ++-side stability runs need the ++ binary directory.";
        }

        string dir = StabDir;
        if (!File.Exists(Path.Combine(dir, "vis-compare.txt"))
            || !File.Exists(Path.Combine(dir, "rad-compare.txt")))
        {
            return $"{dir} is not produced yet: run tools/t6-stability.sh from the "
                + $"worktree root with {CorpusVariable} and {ToolsVariable} exported.";
        }

        return null;
    }

    /// <summary>Products root: <c>&lt;PP_CATMAPS_DIR&gt;-t6</c>, deliberately OUTSIDE
    /// the corpus tree — T0's <c>PpOracle.VbspWrittenMaps()</c> globs every
    /// <c>*.bsp</c> under the corpus recursively, and harness products (our
    /// writer's bytes, ++'s repacked copies) must never join that golden
    /// set.</summary>
    public static string ProductsRoot => Corpus!.TrimEnd('/') + "-t6";

    /// <summary>Where <c>tools/t6-bspzip-interop.sh</c> writes its products.</summary>
    public static string ZipDir => Path.Combine(ProductsRoot, "t6bz");

    /// <summary>Where <c>tools/t6-stability.sh</c> writes its products.</summary>
    public static string StabDir => Path.Combine(ProductsRoot, "t6stab");
}

/// <summary>Fact attribute that skips the corpus tier when it is not mounted.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PpCrossToolFactAttribute : FactAttribute
{
    /// <summary>Names the skip reason.</summary>
    public PpCrossToolFactAttribute() => Skip = PpCrossToolHarness.CorpusSkipReason();
}

/// <summary>Theory attribute that skips the corpus tier when it is not mounted.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PpCrossToolTheoryAttribute : TheoryAttribute
{
    /// <summary>Names the skip reason.</summary>
    public PpCrossToolTheoryAttribute() => Skip = PpCrossToolHarness.CorpusSkipReason();
}

/// <summary>Fact attribute that skips the bspzip++ tier when it is not mounted.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PpBspZipFactAttribute : FactAttribute
{
    /// <summary>Names the skip reason.</summary>
    public PpBspZipFactAttribute() => Skip = PpCrossToolHarness.BspZipSkipReason();
}
