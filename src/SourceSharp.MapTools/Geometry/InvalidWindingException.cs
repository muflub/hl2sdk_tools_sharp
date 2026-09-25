namespace SourceSharp.MapTools.Geometry;

/// <summary>
/// A winding is not something the compilers can work with.
/// </summary>
/// <remarks>
/// Every one of these replaces a call to <c>Error()</c> in
/// Which prints and calls <c>exit</c>. A
/// library cannot exit the host's process, and a map compile that fails should
/// say which map and which brush rather than terminating a service, so the
/// same conditions raise this instead. The conditions themselves are unchanged:
/// this is not a place where the port is more or less tolerant than stock.
/// </remarks>
public sealed class InvalidWindingException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What was wrong.</param>
    public InvalidWindingException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">What was wrong.</param>
    /// <param name="innerException">The underlying failure.</param>
    public InvalidWindingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with no message.</summary>
    public InvalidWindingException()
    {
    }
}
