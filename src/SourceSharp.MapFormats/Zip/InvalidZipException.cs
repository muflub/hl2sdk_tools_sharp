namespace SourceSharp.MapFormats.Zip;

/// <summary>
/// A pakfile is malformed, or uses something this port cannot read.
/// </summary>
/// <remarks>
/// Stock's equivalents are an <c>Assert</c> that vanishes in release
/// (<c>src/public/zip_utils.cpp:677,699</c>), a <c>Warning</c> that carries on
/// with bad data (<c>:703-704</c>), a bare <c>return NULL</c>
/// (<c>:789-798,835-844,860-871</c>) and a fatal <c>Error</c> that aborts the
/// process (<c>:1194,1202</c>). None of those is available to a library, so
/// every one of them becomes this, carrying the reason.
/// </remarks>
public sealed class InvalidZipException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public InvalidZipException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What is wrong with the pak.</param>
    public InvalidZipException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">What is wrong with the pak.</param>
    /// <param name="innerException">The failure that led here.</param>
    public InvalidZipException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
