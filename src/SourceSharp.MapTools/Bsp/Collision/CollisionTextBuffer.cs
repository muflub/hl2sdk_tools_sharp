using System.Text;

using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapTools.Bsp.Collision;

/// <summary>
/// <c>CTextBuffer</c>, <c>ivp.cpp:51-139</c>: the key/value text that follows
/// each model's solids in <c>LUMP_PHYSCOLLIDE</c>.
/// </summary>
/// <remarks>
/// Byte for byte stock's <c>sprintf</c> shapes: <c>"%s" "%d"\n</c>,
/// <c>"%s" "%f"\n</c>, and the float array's <c>"%f "</c> per element with its
/// trailing space inside the quotes. A key longer than 1000 characters is
/// dropped by the three numeric writers (stock prints "Error writing
/// collision data" and returns, <c>:69</c>) but not by
/// <see cref="WriteStringKey"/>, which has no such check.
/// </remarks>
public sealed class CollisionTextBuffer
{
    private const int MaxKeyLength = 1000;

    private readonly List<byte> _bytes = [];

    /// <summary>The bytes so far: <c>GetData</c>/<c>GetSize</c>.</summary>
    public IReadOnlyList<byte> Bytes => _bytes;

    /// <summary>The size so far, including the terminator once written.</summary>
    public int Size => _bytes.Count;

    /// <summary><c>WriteText</c>.</summary>
    /// <param name="text">Copied as is.</param>
    public void WriteText(string text) => Copy(text);

    /// <summary><c>WriteIntKey</c>, <c>ivp.cpp:64</c>.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    public void WriteIntKey(string key, int value)
    {
        if (key.Length > MaxKeyLength)
        {
            return;
        }

        Copy("\"" + key + "\" \"" + CText.D(value) + "\"\n");
    }

    /// <summary><c>WriteStringKey</c>, <c>ivp.cpp:78</c>.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    public void WriteStringKey(string key, string value) => Copy("\"" + key + "\" \"" + value + "\"\n");

    /// <summary><c>WriteFloatKey</c>, <c>ivp.cpp:86</c>.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value, printed <c>%f</c>.</param>
    public void WriteFloatKey(string key, float value)
    {
        if (key.Length > MaxKeyLength)
        {
            return;
        }

        Copy("\"" + key + "\" \"" + CText.F6(value) + "\"\n");
    }

    /// <summary><c>WriteFloatArrayKey</c>, <c>ivp.cpp:100</c>.</summary>
    /// <param name="key">The key.</param>
    /// <param name="values">The values, each printed <c>"%f "</c>.</param>
    public void WriteFloatArrayKey(string key, ReadOnlySpan<float> values)
    {
        if (key.Length > MaxKeyLength)
        {
            return;
        }

        StringBuilder line = new();
        line.Append('"').Append(key).Append("\" \"");
        foreach (float value in values)
        {
            line.Append(CText.F6(value)).Append(' ');
        }

        line.Append("\"\n");
        Copy(line.ToString());
    }

    /// <summary><c>Terminate</c>, <c>ivp.cpp:130</c>: one NUL.</summary>
    public void Terminate() => _bytes.Add(0);

    /// <summary>The bytes as an array.</summary>
    /// <returns>A copy.</returns>
    public byte[] ToArray() => [.. _bytes];

    private void Copy(string text) => _bytes.AddRange(Encoding.Latin1.GetBytes(text));
}
