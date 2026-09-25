namespace SourceSharp.MapTools.Geometry;

/// <summary>
/// Which implementation of a bit-vector operation to run.
/// </summary>
/// <remarks>
/// This exists so the equivalence gate can force each path explicitly. A test
/// that merely ran the dispatcher on whichever machine it happened to be on
/// would exercise ONE path and prove nothing about the other two, and the
/// dispatcher picks the widest the CPU supports — so on a box with AVX-512 the
/// scalar code would never run in a test at all until the day it shipped
/// somewhere without it.
/// </remarks>
public enum BitVectorPath
{
    /// <summary>The widest path this machine supports. The default.</summary>
    Auto = 0,

    /// <summary>One 64-bit word at a time.</summary>
    Scalar = 1,

    /// <summary>Four words at a time.</summary>
    Vector256 = 2,

    /// <summary>Eight words at a time.</summary>
    Vector512 = 3,
}
