using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Disp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// The VMF <c>dispinfo</c> chunk: <c>LoadDispInfoCallback</c> and its row
/// callbacks.
/// </summary>
public sealed class VmfDisplacementReaderTests
{
    private static async Task<MapDisplacement> ReadAsync(string body)
    {
        VmfDocument doc = await VmfDocument.ParseAsync("dispinfo\n{\n" + body + "\n}\n");
        return VmfDisplacementReader.Read(doc.Chunks[0]);
    }

    /// <summary>The scalar keys land on the displacement: <c>LoadDispInfoKeyCallback</c>.</summary>
    [Fact]
    public async Task TheScalarKeysAreRead()
    {
        MapDisplacement d = await ReadAsync(
            "\"power\" \"3\"\n\"startposition\" \"[1 2 3]\"\n\"flags\" \"4\"\n\"mintess\" \"-1\"\n\"smooth\" \"45\"");

        Assert.Equal(3, d.Power);
        Assert.Equal(new Vec3(1, 2, 3), d.StartPosition);
        Assert.Equal(4, d.Flags);
        Assert.Equal(-1, d.MinTess);
        Assert.Equal(45.0f, d.SmoothingAngle);
    }

    /// <summary>A power outside 2..4 is refused: <c>MIN/MAX_MAP_DISP_POWER</c>.</summary>
    [Fact]
    public async Task APowerOutsideTwoToFourIsRefused()
    {
        await Assert.ThrowsAsync<MapCompileException>(() => ReadAsync("\"power\" \"5\""));
    }

    /// <summary>
    /// A normals row fills <c>row * (2^power + 1)</c> onward, three numbers per
    /// vertex: <c>LoadDispNormalsKeyCallback</c>.
    /// </summary>
    [Fact]
    public async Task ANormalsRowFillsItsRow()
    {
        MapDisplacement d = await ReadAsync(
            "\"power\" \"2\"\nnormals\n{\n\"row1\" \"1 0 0 0 1 0\"\n}");

        Assert.Equal(new Vec3(1, 0, 0), d.FieldVectors[5]);
        Assert.Equal(new Vec3(0, 1, 0), d.FieldVectors[6]);
    }

    /// <summary>
    /// A trailing incomplete triple is dropped, because stock's loop needs all
    /// three <c>strtok</c> results.
    /// </summary>
    [Fact]
    public async Task AnIncompleteNormalTripleIsDropped()
    {
        MapDisplacement d = await ReadAsync(
            "\"power\" \"2\"\nnormals\n{\n\"row0\" \"0 0 1 7 8\"\n}");

        Assert.Equal(new Vec3(0, 0, 1), d.FieldVectors[0]);
        Assert.Equal(Vec3.Zero, d.FieldVectors[1]);
    }

    /// <summary>A distances row fills its row: <c>LoadDispDistancesKeyCallback</c>.</summary>
    [Fact]
    public async Task ADistancesRowFillsItsRow()
    {
        MapDisplacement d = await ReadAsync("\"power\" \"2\"\ndistances\n{\n\"row4\" \"1 2 3 4 5\"\n}");

        Assert.Equal(5.0f, d.FieldDistances[24]);
    }

    /// <summary>An alphas row fills its row: <c>LoadDispAlphasKeyCallback</c>.</summary>
    [Fact]
    public async Task AnAlphasRowFillsItsRow()
    {
        MapDisplacement d = await ReadAsync("\"power\" \"2\"\nalphas\n{\n\"row2\" \"0 255\"\n}");

        Assert.Equal(255.0f, d.AlphaValues[11]);
    }

    /// <summary>An offsets row fills its row: <c>LoadDispOffsetsKeyCallback</c>.</summary>
    [Fact]
    public async Task AnOffsetsRowFillsItsRow()
    {
        MapDisplacement d = await ReadAsync("\"power\" \"2\"\noffsets\n{\n\"row0\" \"0 0 0 1 2 3\"\n}");

        Assert.Equal(new Vec3(1, 2, 3), d.VectorOffsets[1]);
    }

    /// <summary>
    /// A triangle-tag row is <c>2 * 2^power</c> triangles wide, not a vertex
    /// row: <c>LoadDispTriangleTagsKeyCallback</c>.
    /// </summary>
    [Fact]
    public async Task ATriangleTagRowIsTwiceThePowerWide()
    {
        MapDisplacement d = await ReadAsync(
            "\"power\" \"2\"\ntriangle_tags\n{\n\"row1\" \"1 0\"\n}");

        Assert.Equal((ushort)DispTriTags.Walkable, d.TriangleTags[8]);
    }

    /// <summary>A row that starts past the end is refused rather than overrun.</summary>
    [Fact]
    public async Task ARowPastTheEndIsRefused()
    {
        await Assert.ThrowsAsync<MapCompileException>(
            () => ReadAsync("\"power\" \"2\"\ndistances\n{\n\"row5\" \"1\"\n}"));
    }

    /// <summary>The walkable bit collapses to DISPTRI_TAG_WALKABLE:.</summary>
    [Fact]
    public void TheWalkableBitCollapsesToWalkable()
    {
        Assert.Equal((ushort)DispTriTags.Walkable, VmfDisplacementReader.CollapseTriangleTags(1));
    }

    /// <summary>
    /// A forced value overrides the computed one: <c>FORCE_WALKABLE_BIT</c>
    /// with a clear <c>FORCE_WALKABLE_VAL</c> makes it not walkable.
    /// </summary>
    [Fact]
    public void AForcedFalseOverridesWalkable()
    {
        Assert.Equal(0, VmfDisplacementReader.CollapseTriangleTags(1 | 2));
    }

    /// <summary>A forced true makes a non-walkable triangle walkable.</summary>
    [Fact]
    public void AForcedTrueMakesWalkable()
    {
        Assert.Equal((ushort)DispTriTags.Walkable, VmfDisplacementReader.CollapseTriangleTags(2 | 4));
    }

    /// <summary>The buildable bits collapse the same way:.</summary>
    [Fact]
    public void TheBuildableBitCollapsesToBuildable()
    {
        Assert.Equal((ushort)DispTriTags.Buildable, VmfDisplacementReader.CollapseTriangleTags(8));
        Assert.Equal(0, VmfDisplacementReader.CollapseTriangleTags(8 | 16));
    }

    /// <summary>Unknown keys are ignored, as the stock key callback ignores them.</summary>
    [Fact]
    public async Task UnknownKeysAreIgnored()
    {
        MapDisplacement d = await ReadAsync("\"power\" \"2\"\n\"elevation\" \"7\"");

        Assert.Equal(2, d.Power);
    }
}
