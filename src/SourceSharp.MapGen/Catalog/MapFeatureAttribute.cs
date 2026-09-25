namespace SourceSharp.MapGen.Catalog;

/// <summary>
/// What a <see cref="MapFeature"/> is, and which phase of plan_maptools.md can
/// first assert anything about it.
///
/// <para>
/// Carried as an attribute rather than a table in another file so that adding a
/// feature to the vocabulary and stating its owning phase are the same edit. The
/// matrix fact reads it: a feature with no entry is a GAP, and a gap is only
/// acceptable when the phase that owns it has not arrived.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class MapFeatureAttribute : Attribute
{
    /// <summary>Records what the feature is and who owns it.</summary>
    /// <param name="tool">The stock compiler a failure in this feature lands in.</param>
    /// <param name="phase">
    /// The plan_maptools.md phase that ports that stage: 2 = vvis, 3 = vbsp,
    /// 4 = vrad. Below this phase there is no managed compiler to assert
    /// against, so an entry can be declared but not checked.
    /// </param>
    /// <param name="summary">One line, in the plan's own words where it has them.</param>
    public MapFeatureAttribute(CompileTool tool, int phase, string summary)
    {
        Tool = tool;
        Phase = phase;
        Summary = summary;
    }

    /// <summary>The stock compiler a failure in this feature lands in.</summary>
    public CompileTool Tool { get; }

    /// <summary>The plan phase that ports the stage this feature belongs to.</summary>
    public int Phase { get; }

    /// <summary>One line describing the feature.</summary>
    public string Summary { get; }
}
