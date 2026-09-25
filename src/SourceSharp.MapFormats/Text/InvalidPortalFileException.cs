namespace SourceSharp.MapFormats.Text;

/// <summary>
/// A <c>.prt</c> file is malformed.
/// </summary>
/// <remarks>
/// One of these per <c>Error()</c> in <c>LoadPortals</c>
/// (<c>src/utils/vvis/vvis.cpp:462,465,467,474,504,506,509,524</c>). Stock
/// calls <c>Error</c>, which aborts the process; a library cannot, so the
/// message text is preserved and thrown instead. Note three of those sites
/// share the text <c>"LoadPortals: reading portal %i"</c> for three different
/// causes -- a bad header line, an out-of-range leaf number, and a bad point --
/// so the message alone does not identify what went wrong, in stock or here.
/// </remarks>
public sealed class InvalidPortalFileException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public InvalidPortalFileException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What is wrong with the file.</param>
    public InvalidPortalFileException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">What is wrong with the file.</param>
    /// <param name="innerException">The failure that led here.</param>
    public InvalidPortalFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
