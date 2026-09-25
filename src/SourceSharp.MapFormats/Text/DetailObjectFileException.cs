namespace SourceSharp.MapFormats.Text;

/// <summary>
/// A <c>detail.vbsp</c> file has a fatally malformed entry.
/// </summary>
/// <remarks>
/// There is exactly one condition that aborts stock vbsp while parsing one: a
/// <c>sprite</c> value that does not carry five numbers, or whose texture size
/// is zero. Everything else is silent -- a missing file, a malformed KeyValues
/// tree and an unknown key all produce no diagnostic at all.
/// </remarks>
public sealed class DetailObjectFileException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public DetailObjectFileException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What is wrong with the file.</param>
    public DetailObjectFileException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">What is wrong with the file.</param>
    /// <param name="innerException">The failure that led here.</param>
    public DetailObjectFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
