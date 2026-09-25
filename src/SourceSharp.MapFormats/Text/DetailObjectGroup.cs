namespace SourceSharp.MapFormats.Text;

/// <summary>
/// One group inside a detail type: a set of models sharing a blend alpha.
/// </summary>
/// <remarks>
/// The group's KEY NAME is never read. The reference detail loader only checks
/// that it has subkeys and then reads its <c>alpha</c>; groups are identified
/// thereafter by position in the alpha-sorted list, the order
/// <c>SelectGroup</c> walks.
/// </remarks>
public sealed class DetailObjectGroup
{
    /// <summary>Creates a group.</summary>
    /// <param name="alpha">The blend alpha.</param>
    public DetailObjectGroup(float alpha)
    {
        Alpha = alpha;
    }

    /// <summary>
    /// <c>alpha</c>, defaulting to 1 when the key is absent.
    /// </summary>
    public float Alpha { get; }

    /// <summary>The models, in file order.</summary>
    public IList<DetailObjectModel> Models { get; } = [];
}
