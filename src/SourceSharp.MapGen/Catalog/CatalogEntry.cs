using System.Text;

namespace SourceSharp.MapGen.Catalog;

/// <summary>
/// One test map: what it is, what it exercises, what it should produce, and the
/// code that builds it.
///
/// <para>
/// The declaration is the deliverable, not the .vmf. A generator that can be run
/// is a map; a generator with its level, its features, its expected observables
/// and its compile options attached is a test — one that says, when it fails,
/// WHICH feature broke, which is the entire reason §2a exists.
/// </para>
///
/// <para>
/// WHAT THIS DOES NOT HAVE, and why. §2a also wants each entry generated in
/// memory as a parsed map model (`MapSource.FromModel`) so the unit tier never
/// touches the disk. That model arrives with vbsp in Phase 3. Adding one here
/// would mean inventing a second map model that the real one then has to be
/// reconciled with, so entries emit VMF TEXT for now and gain the in-memory path
/// later without any declaration below changing.
/// </para>
/// </summary>
public sealed class CatalogEntry
{
    /// <summary>
    /// The entry's name. Lower case with underscores, prefixed with its level,
    /// because it is also the .vmf's filename and a compile log is read by eye.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>What kind of test map this is.</summary>
    public required MapLevel Level { get; init; }

    /// <summary>
    /// The features it exercises.
    ///
    /// <para>
    /// An L1 entry declares EXACTLY ONE. That is §2a's definition of the level —
    /// "a sealed room plus exactly one feature" — and it is checked, because an
    /// L1 entry with two features has quietly become an L2 entry that nobody
    /// will read as one.
    /// </para>
    /// </summary>
    public required IReadOnlyList<MapFeature> Features { get; init; }

    /// <summary>One line saying what a reader is looking at.</summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Builds the map, given a random source seeded from this entry's name.
    ///
    /// <para>
    /// The generator takes the source rather than making one so that it cannot
    /// reach for a shared or time-based one: there is one in scope, it belongs
    /// to this entry, and it starts in the same place every time.
    /// </para>
    /// </summary>
    public required Func<CatalogRandom, VmfMap> Generator { get; init; }

    /// <summary>What the entry is expected to produce.</summary>
    public MapObservables Observables { get; init; } = MapObservables.None;

    /// <summary>The switches it should be compiled with.</summary>
    public CatalogCompileOptions Options { get; init; } = CatalogCompileOptions.Default;

    /// <summary>Which of §2's acceptance instruments apply.</summary>
    public CompileInstrument Instruments { get; init; } = CompileInstrument.Standard;

    /// <summary>What the emitted file is called.</summary>
    public string FileName => $"{Name}.vmf";

    /// <summary>Builds the map model, from a fresh seed.</summary>
    public VmfMap Build() => Generator(new CatalogRandom(Name));

    /// <summary>
    /// Builds the map and writes it out as VMF text.
    ///
    /// <para>
    /// The rule check runs HERE rather than at the call sites, so an entry
    /// cannot get a raw newline into a quoted value by any route — see
    /// <see cref="VmfTextRules"/> for what that shape does to stock's own
    /// tokenizer.
    /// </para>
    /// </summary>
    public string WriteVmf()
    {
        string text = Build().Write();
        VmfTextRules.RequireNoNewlineInsideAQuotedValue(text, Name);

        return text;
    }

    /// <summary>
    /// The same text as bytes.
    ///
    /// <para>
    /// Latin-1, because that is the encoding the rest of this tree reads a VMF
    /// with — a .vmf has no declared encoding and stock treats it as bytes, so
    /// a round trip through UTF-8 would invent a BOM question nobody asked.
    /// </para>
    /// </summary>
    public byte[] WriteVmfBytes() => Encoding.Latin1.GetBytes(WriteVmf());

    /// <summary>
    /// Writes the entry into a directory and returns the path.
    ///
    /// <para>
    /// The caller picks the directory, and every caller in this tree picks a
    /// temporary one. Generated maps are not committed: they are a pure function
    /// of the generator, so a copy in the repo is a second source of truth that
    /// can only ever be stale.
    /// </para>
    /// </summary>
    /// <param name="directory">Where to write. Created if it is not there.</param>
    public string EmitTo(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, FileName);
        File.WriteAllBytes(path, WriteVmfBytes());

        return path;
    }

    /// <summary>How the coverage report spells this entry.</summary>
    public override string ToString()
        => $"{Level} {Name} [{string.Join(", ", Features)}]";
}
