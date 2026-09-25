namespace SourceSharp.MapFormats.Text;

/// <summary>
/// One detail type from a <c>detail.vbsp</c>, as the reference detail-object
/// loader reads them.
/// </summary>
public sealed class DetailObjectType
{
    /// <summary>Creates a type.</summary>
    /// <param name="name">The type name.</param>
    /// <param name="density">The density.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public DetailObjectType(string name, float density)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        Density = density;
    }

    /// <summary>
    /// The type name -- the key it was declared under, which is what a
    /// material's <c>%detailtype</c> variable names.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// <c>density</c>, read at the TYPE level and defaulting to zero.
    /// </summary>
    /// <remarks>
    /// Per type, not per group, which is easy to get wrong because
    /// <c>alpha</c> right beside it in the file IS per group. It turns into a
    /// sample count as
    /// <c>(int)(area * density * </c><see cref="DetailObjectFile.DensityToSamples"/><c>)</c>
    /// -- truncated, not rounded.
    /// </remarks>
    public float Density { get; }

    /// <summary>
    /// The groups, sorted by ascending <c>alpha</c>.
    /// </summary>
    public IList<DetailObjectGroup> Groups { get; } = [];

    /// <summary>
    /// How many detail props this type places on a surface of a given area.
    /// </summary>
    /// <param name="area">The surface area, in square map units.</param>
    /// <returns>The sample count.</returns>
    /// <remarks>
    /// A truncating conversion to <c>int</c>, so
    /// a face whose area times density falls short of one million places
    /// NOTHING -- which is why a low-density type looks absent on small faces.
    /// </remarks>
    public int SampleCount(double area) =>
        (int)(area * Density * DetailObjectFile.DensityToSamples);
}
