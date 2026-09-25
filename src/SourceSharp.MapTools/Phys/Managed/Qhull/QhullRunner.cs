namespace SourceSharp.MapTools.Phys.Managed.Qhull;

/// <summary>
/// <see cref="IQhullRunner"/> over the qhull 2.6 port: one <see cref="QhullSession"/> per runner,
/// so storage is reused across the cooks of one thread (a runner, like a session, is not
/// thread-safe; the cooker keeps one per thread).
/// </summary>
internal sealed class QhullRunner : IQhullRunner
{
    private readonly QhullSession session = new();
    private QhullResult? last;

    /// <inheritdoc/>
    public int FacetCount => last?.Facets.Count ?? 0;

    /// <inheritdoc/>
    public int Run(ReadOnlySpan<double> coords, int pointCount, string options)
    {
        last = session.Build(coords[..(3 * pointCount)], options);
        return last.ExitCode;
    }

    /// <inheritdoc/>
    public (double X, double Y, double Z) Normal(int facet)
    {
        QhullFacet f = Last.Facets[facet];
        return (f.NormalX, f.NormalY, f.NormalZ);
    }

    /// <inheritdoc/>
    public ReadOnlySpan<int> Vertices(int facet) => Last.Facets[facet].PointIds;

    private QhullResult Last => last ?? throw new InvalidOperationException("no qhull run yet");
}
