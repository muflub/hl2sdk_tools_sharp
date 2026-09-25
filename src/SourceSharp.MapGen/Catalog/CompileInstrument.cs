namespace SourceSharp.MapGen.Catalog;

/// <summary>
/// Which of plan_maptools.md §2's acceptance instruments apply to an entry.
///
/// <para>
/// Not every instrument applies to every map, and saying which is part of the
/// declaration rather than something a harness works out. The leak entry is the
/// clear case: it has no `.prt`, so stage isolation has nothing to feed the next
/// tool and a diff against a stock BSP is a diff of two failure outputs.
/// </para>
/// </summary>
[Flags]
public enum CompileInstrument
{
    /// <summary>None — the entry is declared but nothing is run on it yet.</summary>
    None = 0,

    /// <summary>I1 `ssmap check`: the engine-loader rule set.</summary>
    LoaderCheck = 1,

    /// <summary>I2 `ssmap diff`: semantic comparison against the stock output, per lump.</summary>
    SemanticDiff = 2,

    /// <summary>I3 stage isolation: each managed tool judged on the stock tool's input.</summary>
    StageIsolation = 4,

    /// <summary>I4 determinism: `-threads 1` vs `-threads 32`, and two consecutive runs.</summary>
    Determinism = 8,

    /// <summary>I5 cached == clean: an incremental build byte-identical to a clean one.</summary>
    Incremental = 16,

    /// <summary>What a map that compiles cleanly gets: everything but the incremental gate.</summary>
    Standard = LoaderCheck | SemanticDiff | StageIsolation | Determinism,
}
