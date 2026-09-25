using System.Collections.Immutable;

namespace SourceSharp.MapTools.Diagnostics;

/// <summary>
/// The map is not sealed: an entity can see the void.
/// </summary>
/// <param name="EntityId">The VMF id of the entity that reached outside.</param>
/// <param name="ClassName">That entity's classname.</param>
/// <param name="Path">
/// The leak path, from the entity outwards, as a polyline in world space. This
/// is the same data stock writes into a <c>.lin</c> file for Hammer to draw.
/// </param>
/// <remarks>
/// A leak is DATA, not an exception, and the path is the report rather than the
/// file being the report. Stock only ever emits the <c>.lin</c>, so a host that
/// wants to point at the leak has to parse a file the compiler just wrote; here
/// the <c>.lin</c> is one rendering of this object, and writing it is optional.
/// </remarks>
public sealed record LeakReport(
    int EntityId,
    string ClassName,
    ImmutableArray<(float X, float Y, float Z)> Path);

/// <summary>
/// A compile could not continue, for a reason that is not the map's fault.
/// </summary>
/// <remarks>
/// Reserved for the genuinely unrecoverable: a corrupt input the readers cannot
/// frame, a native library that will not load, an internal invariant that has
/// been broken. Anything wrong with the MAP is a
/// <see cref="CompileDiagnostic"/>, because a user can act on that and because
/// reporting twenty of them beats reporting the first one twenty times.
/// Nothing in these libraries ever calls <c>Environment.Exit</c>.
/// </remarks>
public sealed class MapCompileException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public MapCompileException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public MapCompileException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The failure that led here.</param>
    public MapCompileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with a stable diagnostic code.</summary>
    /// <param name="code">
    /// A stable identifier in the <see cref="CompileDiagnostic.Code"/> style,
    /// such as <c>VBSP0101</c>. A host keys on it; the message may be reworded.
    /// </param>
    /// <param name="message">What went wrong.</param>
    /// <exception cref="ArgumentException"><paramref name="code"/> is null, empty or blank.</exception>
    public MapCompileException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>Creates the exception with a stable diagnostic code and a cause.</summary>
    /// <param name="code">A stable identifier; see <see cref="MapCompileException(string, string)"/>.</param>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    /// <exception cref="ArgumentException"><paramref name="code"/> is null, empty or blank.</exception>
    public MapCompileException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>
    /// The stable diagnostic code, or <see langword="null"/> when the thrower
    /// gave none. The message-only constructors predate the code and are kept
    /// so no existing throw site has to change at once.
    /// </summary>
    public string? Code { get; }
}
