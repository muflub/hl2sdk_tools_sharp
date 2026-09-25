using System.Runtime.CompilerServices;

// The managed collision cooker's building blocks (qhull, IVP's builders) are internal: they are an
// implementation of ICollisionCooker, not API. The unit tier tests them directly, bit for bit
// against goldens cut from the native library.
[assembly: InternalsVisibleTo("SourceSharp.Tests")]
