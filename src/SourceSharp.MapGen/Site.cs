namespace SourceSharp.MapGen;

/// <summary>
/// The small fixed material palette the test-map catalog builds its rooms from.
///
/// The names are ordinary game-content textures; the catalog pins them so a
/// room compiled from <see cref="SourceSharp.MapGen.Catalog.TestMapCatalog"/>
/// is reproducible byte for byte.
/// </summary>
public static class Site
{
    /// <summary>The shell's floor.</summary>
    public const string Floor = "concrete/concretefloor001a";

    /// <summary>The shell's ceiling.</summary>
    public const string Overhead = "plaster/plasterceiling003a";

    /// <summary>The shell's walls.</summary>
    public const string Hall = "plaster/plasterwall021a";

    /// <summary>What a pillar, a divider or a detail block is made of.</summary>
    public const string Wall = "wood/woodwall014a";
}
