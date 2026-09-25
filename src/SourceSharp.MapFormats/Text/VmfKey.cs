namespace SourceSharp.MapFormats.Text;

/// <summary>
/// A <c>"key" "value"</c> pair inside a chunk.
/// </summary>
/// <remarks>
/// The value is always a string. The chunk format has no types: every numeric
/// VMF value is text that a handler runs <c>atof</c> or <c>sscanf</c> over
/// (<c>src/public/chunkfile.cpp:636-774</c>), which is why a float survives a
/// round trip only to <c>%g</c>'s six significant digits. See
/// <see cref="VmfValue"/> for those conversions.
/// </remarks>
public sealed class VmfKey : VmfNode
{
    /// <summary>Creates a key/value pair.</summary>
    /// <param name="name">The key name.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="value"/> is null.
    /// </exception>
    public VmfKey(string name, string value)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>The value, verbatim as the tokenizer decoded it.</summary>
    public string Value { get; set; }
}
