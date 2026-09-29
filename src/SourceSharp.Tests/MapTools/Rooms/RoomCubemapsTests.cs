//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Zip;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Cubemaps;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// What a room's compile stores about its <c>env_cubemap</c> samples
/// (<see cref="RoomCubemaps"/>): which of its names are a sample's, the
/// samples' turned origins and how the link places them, the <c>CUBE</c>
/// section and its refusals, and the one-pass rename of a patched
/// material's text.
/// </summary>
public sealed class RoomCubemapsTests
{
    /// <summary>The two samples every fact's compile has: one off the whole units, one negative.</summary>
    private static readonly Vec3[] Origins = [new(10.5f, 20.25f, 30f), new(-5.5f, 0f, 7.75f)];

    // ---- which names are a sample's ------------------------------------------------------

    /// <summary>A compile with no sample has no cubemap data, and no section.</summary>
    [Fact]
    public async Task ARoomWithoutSamplesHasNoData() =>
        Assert.Null(await RoomCubemaps.BuildAsync(Compile(0, [], []), [], "r"));

    /// <summary>
    /// The compile's samples must be the ones its lump holds: another count,
    /// or an origin that does not truncate to the lump's, is a caller's
    /// mistake and refused.
    /// </summary>
    [Fact]
    public async Task SamplesThatAreNotTheLumpsAreRefused()
    {
        BspData bsp = Compile(2, [], []);
        ArgumentException count = await Assert.ThrowsAsync<ArgumentException>(() => RoomCubemaps.BuildAsync(bsp, [Sample(0)], "r"));
        Assert.StartsWith("the compile has 1 cubemap samples, and its lump 2.", count.Message, StringComparison.Ordinal);

        ArgumentException moved = await Assert.ThrowsAsync<ArgumentException>(
            () => RoomCubemaps.BuildAsync(bsp, [Sample(0), new CubemapSample(new Vec3(-6.5f, 0, 7), 0, string.Empty)], "r"));
        Assert.StartsWith("cubemap sample 1 at ", moved.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A texdata string is a sample's patch when it is the compile's map
    /// name, a material and a sample's position, and the room packs its
    /// patch file; a packed file is a sample's when it is a patch file (a
    /// dependent's too, which has no string) or the sample's LDR or HDR
    /// texture. Everything else is left alone: another map's name, a patch
    /// without its file, a name with nothing before the position, the
    /// default cubemap, a WVT patch, another extension. The map name is
    /// matched lower-cased, as vbsp writes it.
    /// </summary>
    [Fact]
    public async Task NamesAreSortedToTheirSamples()
    {
        string[] strings =
        [
            "unit/plain", "maps/r/unit/a_10_20_30", "maps/r/unit/c_10_20_30", "maps/other/unit/a_10_20_30",
            "maps/r/unit/b2_-5_0_7", "maps/r/_10_20_30",
        ];
        string[] files =
        [
            "materials/maps/r/unit/a_10_20_30.vmt", "materials/maps/r/unit/dep_-5_0_7.vmt", "materials/maps/r/unit/b2_-5_0_7.vmt",
            "materials/maps/r/c10_20_30.vtf", "materials/maps/r/c10_20_30.hdr.vtf", "materials/maps/r/c-5_0_7.vtf",
            "materials/maps/r/cubemapdefault.vtf", "materials/maps/r/unit/a_wvt_patch.vmt", "materials/maps/r/unit/a_10_20_30.txt",
            "materials/maps/other/c10_20_30.vtf", "sound/x.wav",
        ];

        RoomCubemaps cubemaps = (await RoomCubemaps.BuildAsync(Compile(2, strings, files), [Sample(0), Sample(1)], "R"))!;
        Assert.Equal("R", cubemaps.MapBase);
        Assert.Equal(2, cubemaps.SampleCount);
        Assert.Equal([new CubemapString(1, 0, "unit/a"), new CubemapString(4, 1, "unit/b2")], cubemaps.Strings);
        Assert.Equal(
            [
                new CubemapFile(files[0], CubemapFileKind.Material, 0, "unit/a"),
                new CubemapFile(files[1], CubemapFileKind.Material, 1, "unit/dep"),
                new CubemapFile(files[2], CubemapFileKind.Material, 1, "unit/b2"),
                new CubemapFile(files[3], CubemapFileKind.Texture, 0, string.Empty),
                new CubemapFile(files[4], CubemapFileKind.HdrTexture, 0, string.Empty),
                new CubemapFile(files[5], CubemapFileKind.Texture, 1, string.Empty),
            ],
            cubemaps.Files);
    }

    /// <summary>
    /// Two samples at one truncated position share their names, as vbsp's
    /// names do: the first takes them. A room with an empty pak has samples
    /// and no names.
    /// </summary>
    [Fact]
    public async Task TheFirstOfTwoSamplesAtOnePositionTakesTheNames()
    {
        CubemapSample twin = new(new Vec3(10.75f, 20.5f, 30.25f), 0, string.Empty);
        BspData bsp = Compile([Origins[0], twin.Origin], ["maps/r/unit/a_10_20_30"], ["materials/maps/r/unit/a_10_20_30.vmt"]);
        RoomCubemaps cubemaps = (await RoomCubemaps.BuildAsync(bsp, [Sample(0), twin], "r"))!;
        Assert.Equal(0, Assert.Single(cubemaps.Strings).Sample);
        Assert.Equal(0, Assert.Single(cubemaps.Files).Sample);

        RoomCubemaps bare = (await RoomCubemaps.BuildAsync(Compile(2, ["maps/r/unit/a_10_20_30"], null), [Sample(0), Sample(1)], "r"))!;
        Assert.Empty(bare.Strings);
        Assert.Empty(bare.Files);
    }

    // ---- where the link puts them ---------------------------------------------------------------

    /// <summary>
    /// The samples are stored as the loader read them, turned four ways, and
    /// the link truncates each turned origin after adding the placement's
    /// offset, which is what the flattened compile truncates: at every turn
    /// and cell, including where truncating the room's own integer and then
    /// turning it would be a unit off (10.5 turned twice is -10.5, which
    /// truncates to -10, not to the -11 the turned 10 would give after the
    /// offset).
    /// </summary>
    [Fact]
    public async Task TheLinkedPositionsAreTheFlattenedCompiles()
    {
        RoomCubemaps cubemaps = (await RoomCubemaps.BuildAsync(Compile(2, [], []), [Sample(0), Sample(1)], "r"))!;
        for (int rotation = 0; rotation < 4; rotation++)
        {
            RoomTransform transform = new(new RoomPlacement("r", 3, -2, rotation), 256f);
            QuarterTurn turn = QuarterTurn.Of(transform);
            (int X, int Y, int Z)[] world = cubemaps.WorldOrigins(transform);
            for (int i = 0; i < Origins.Length; i++)
            {
                Assert.Equal(turn.Rotate(Origins[i]), cubemaps.Turned(rotation)[i]);
                Assert.Equal(CubemapFixups.SampleOrigin(turn.Apply(Origins[i])), world[i]);
            }
        }

        RoomTransform half = new(new RoomPlacement("r", 0, 0, 2), 256f);
        Assert.Equal(245, cubemaps.WorldOrigins(half)[0].X);
    }

    // ---- the section --------------------------------------------------------------------------------

    /// <summary>
    /// The section reads back to the same data, bound to the compile it is
    /// read with; data bound to no compile, or another, is for none; a
    /// missing section and one of another revision read as none.
    /// </summary>
    [Fact]
    public async Task TheSectionRoundTrips()
    {
        string[] files = ["materials/maps/r/unit/a_10_20_30.vmt", "materials/maps/r/c-5_0_7.hdr.vtf"];
        BspData bsp = Compile(2, ["maps/r/unit/a_10_20_30"], files);
        RoomCubemaps cubemaps = (await RoomCubemaps.BuildAsync(bsp, [Sample(0), Sample(1)], "r"))!;
        RoomPackSectionData section = cubemaps.ToSection();
        Assert.Equal(RoomCubemaps.SectionTag, section.Tag);

        RoomCubemaps read = RoomCubemaps.Read(section.Bytes.ToArray(), "r", bsp)!;
        Assert.Equal(cubemaps.MapBase, read.MapBase);
        Assert.Equal(cubemaps.Strings, read.Strings);
        Assert.Equal(cubemaps.Files, read.Files);
        for (int t = 0; t < 4; t++)
        {
            Assert.True(cubemaps.Turned(t).SequenceEqual(read.Turned(t)));
        }

        Assert.Equal(section.Bytes.ToArray(), read.ToSection().Bytes.ToArray());

        RoomObject room = RoomOf(bsp);
        Assert.True(read.IsFor(room));
        Assert.False(read.IsFor(RoomOf(Compile(2, [], []))));
        Assert.True(read.For(Compile(2, [], [])).IsFor(RoomOf(bsp)) is false);

        Assert.Null(RoomCubemaps.Read(null, "r", bsp));
        byte[] other = section.Bytes.ToArray();
        other[9 + 3] = 2; // the revision's low byte
        Assert.Null(RoomCubemaps.Read(other, "r", bsp));
    }

    /// <summary>A damaged section, or one that is not this compile's, is refused naming the room and what is wrong.</summary>
    /// <param name="damage">Which field is wrong.</param>
    /// <param name="expected">The end of the message.</param>
    [Theory]
    [InlineData("samples", "3 cubemap samples; the room has 2")]
    [InlineData("rotations", "1 turns of its samples; it stores 4")]
    [InlineData("origin", "cubemap sample 0 at (11.5 20.25 30), not where the room's lump has it")]
    [InlineData("turn", "cubemap sample 1 at turn 2 not turned from turn 0")]
    [InlineData("string", "a cubemap string naming table entry 7; the room has 1")]
    [InlineData("sample", "a cubemap name of sample 2; the room has 2")]
    [InlineData("kind", "a cubemap file kind of 3")]
    [InlineData("trailing", "1 bytes after its end")]
    public void DamagedSectionsAreRefused(string damage, string expected)
    {
        BspData bsp = Compile(2, ["maps/r/unit/a_10_20_30"], null);
        RoomLinkSections.Writer w = new();
        w.Int(RoomCubemaps.Revision);
        w.String("r");
        w.Int(damage == "samples" ? 3 : 2);
        w.Int(damage == "rotations" ? 1 : 4);
        for (int t = 0; t < (damage == "rotations" ? 1 : 4); t++)
        {
            Vec3[] turned = [.. Origins.Select(o => new QuarterTurn(t, Vec3.Zero).Rotate(o))];
            if (damage == "origin" && t == 0)
            {
                turned[0] = new Vec3(11.5f, 20.25f, 30f);
            }

            if (damage == "turn" && t == 2)
            {
                turned[1] = turned[1] + new Vec3(1, 0, 0);
            }

            w.Structs<Vec3>(turned, counted: false);
        }

        w.Int(1);
        w.Int(damage == "string" ? 7 : 0);
        w.Int(damage == "sample" ? 2 : 0);
        w.String("unit/a");
        w.Int(1);
        w.String("materials/maps/r/c10_20_30.vtf");
        w.Byte(damage == "kind" ? (byte)3 : (byte)1);
        w.Int(0);
        w.String(string.Empty);
        if (damage == "trailing")
        {
            w.Byte(0);
        }

        byte[] section = RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None);
        LinkException refused = Assert.Throws<LinkException>(() => RoomCubemaps.Read(section, "attic", bsp));
        Assert.Equal($"room pack entry \"attic\": its \"CUBE\" section holds {expected}.", refused.Message);
    }

    // ---- the patched material's text ------------------------------------------------------------------

    /// <summary>
    /// The text rename replaces whole quoted values only, in one pass (a new
    /// name that is another old name is not renamed again), keeps an escaped
    /// quote inside a value, and copies an unterminated quote and everything
    /// outside quotes as it is.
    /// </summary>
    [Fact]
    public void TheTextRenameIsOnePassOverQuotedValues()
    {
        Dictionary<string, string> renames = new(StringComparer.Ordinal) { ["a"] = "b", ["b"] = "c", ["x\\\"y"] = "z" };
        string Rename(string text) => Encoding.Latin1.GetString(PlacementCubemaps.Rename(Encoding.Latin1.GetBytes(text), renames));

        Assert.Equal("\"b\"\t\t\"c\"\r\n", Rename("\"a\"\t\t\"b\"\r\n"));
        Assert.Equal("\"ab\" a \"z\"", Rename("\"ab\" a \"x\\\"y\""));
        Assert.Equal("\"b\" \"a", Rename("\"a\" \"a"));
        Assert.Equal("no quotes", Rename("no quotes"));
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static CubemapSample Sample(int i) => new(Origins[i], 0, string.Empty);

    private static BspData Compile(int samples, string[] strings, string[]? files) => Compile(Origins[..samples], strings, files);

    /// <summary>A compile with the given samples (truncated into its lump), texdata strings and packed files (null: no pak).</summary>
    private static BspData Compile(Vec3[] origins, string[] strings, string[]? files)
    {
        BspData bsp = new();
        DCubemapSample[] records = new DCubemapSample[origins.Length];
        for (int i = 0; i < origins.Length; i++)
        {
            (int x, int y, int z) = CubemapFixups.SampleOrigin(origins[i]);
            records[i].Origin[0] = x;
            records[i].Origin[1] = y;
            records[i].Origin[2] = z;
        }

        bsp.SetLump(BspLump.Cubemaps, MemoryMarshal.AsBytes(records.AsSpan()).ToArray());
        List<int> table = [];
        List<byte> data = [];
        foreach (string s in strings)
        {
            table.Add(data.Count);
            data.AddRange(Encoding.Latin1.GetBytes(s));
            data.Add(0);
        }

        bsp.SetLump(BspLump.TexDataStringTable, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(table)).ToArray());
        bsp.SetLump(BspLump.TexDataStringData, data.ToArray());
        if (files is not null)
        {
            ZipArchiveWriter writer = new();
            foreach (string file in files)
            {
                writer.Add(file, [1, 2, 3]);
            }

            bsp.SetLump(BspLump.PakFile, writer.ToBytes());
        }

        return bsp;
    }

    private static RoomObject RoomOf(BspData bsp) =>
        new(RoomHarness.Hub(), bsp, new SourceSharp.MapTools.Vis.VisResult(
            0, 0, 0, [], [], 0, 0, 0, 0, false, 0, 0, SourceSharp.MapTools.Vis.VisWorkCounters.Zero, null),
            new RoomLintReport([], [], [], []), []);
}
