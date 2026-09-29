//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compile.Cache;

/// <summary>
/// <see cref="VvisStageCache.InputDigest"/>, one field at a time: each fact
/// changes exactly one thing vvis reads and nothing else, so leaving that
/// field out of the digest makes its fact fail. A stale vis replayed onto a
/// map whose portals or leaves moved is a wrong visibility lump, not a miss.
/// </summary>
public sealed class VvisStageCacheKeyTests
{
    private static readonly byte[] Portals = [1, 2, 3];

    // A map with every input lump present and distinct.
    private static BspData Map(string? entities = null)
    {
        BspData bsp = new();
        byte fill = 10;
        foreach (BspLump lump in (BspLump[])
        [
            BspLump.Nodes, BspLump.Faces, BspLump.Leafs, BspLump.LeafFaces,
            BspLump.Edges, BspLump.SurfEdges, BspLump.Vertexes, BspLump.TexInfo,
        ])
        {
            bsp.SetLump(lump, new byte[] { fill++, fill++, fill++, fill++ });
        }

        if (entities is not null)
        {
            bsp.SetLump(BspLump.Entities, Encoding.ASCII.GetBytes(entities + "\0"));
        }

        return bsp;
    }

    private static string Key(BspData bsp, VvisOptions? options = null) =>
        VvisStageCache.InputDigest(Portals, bsp, options ?? VvisOptions.Default);

    public static TheoryData<BspLump> InputLumps() =>
    [
        BspLump.Nodes, BspLump.Faces, BspLump.Leafs, BspLump.LeafFaces,
        BspLump.Edges, BspLump.SurfEdges, BspLump.Vertexes, BspLump.TexInfo,
    ];

    [Theory]
    [MemberData(nameof(InputLumps))]
    public void TheKeyFollowsAnInputLumpsBytes(BspLump lump)
    {
        BspData bsp = Map();
        string before = Key(bsp);
        byte[] bytes = bsp[lump].Data.ToArray();
        bytes[0] ^= 0xff;
        bsp.SetLump(lump, bytes);

        Assert.NotEqual(before, Key(bsp));
    }

    [Theory]
    [MemberData(nameof(InputLumps))]
    public void TheKeyFollowsAnInputLumpsVersion(BspLump lump)
    {
        BspData bsp = Map();
        string before = Key(bsp);
        BspLumpData data = bsp[lump];
        bsp[lump] = new BspLumpData(data.Data.ToArray(), data.Version + 1, 0);

        Assert.NotEqual(before, Key(bsp));
    }

    [Fact]
    public void TheKeyIgnoresALumpVvisDoesNotRead()
    {
        BspData bsp = Map();
        string before = Key(bsp);
        bsp.SetLump(BspLump.Lighting, new byte[] { 9, 9, 9 });

        Assert.Equal(before, Key(bsp));
    }

    [Fact]
    public void TheKeyFollowsNoSort() =>
        Assert.NotEqual(Key(Map()), Key(Map(), VvisOptions.Default with { NoSort = !VvisOptions.Default.NoSort }));

    [Fact]
    public void TheKeyFollowsTighten() =>
        Assert.NotEqual(Key(Map()), Key(Map(), VvisOptions.Default with { Tighten = !VvisOptions.Default.Tighten }));

    [Fact]
    public void TheKeyFollowsTheFastFlow() =>
        Assert.NotEqual(Key(Map()), Key(Map(), VvisOptions.Default with { FastFlow = true }));

    [Fact]
    public void TheFastFlowLeavesEveryOtherKeyAsItWas()
    {
        // The switch is appended only when on, so a cache written before it
        // existed still hits: this is the key main computed for this map.
        Assert.Equal(DefaultKeyBeforeTheFastFlow, Key(Map()));
    }

    /// <summary>The key of <see cref="Map"/> with the default options, captured before <c>-fastflow</c> existed.</summary>
    private const string DefaultKeyBeforeTheFastFlow = "3311c6d37e2ffa90f15823d288fa995e5ca241392e641dc6205d4f24349696e6";

    [Fact]
    public void TheKeyFollowsTheRadiusOverride() =>
        // Both overrides set, so the fog line folds the same null either way:
        // only the override's own field differs.
        Assert.NotEqual(
            Key(Map(), VvisOptions.Default with { RadiusOverride = 150f }),
            Key(Map(), VvisOptions.Default with { RadiusOverride = 200f }));

    [Fact]
    public void TheKeyFollowsTheComplianceOptions() =>
        Assert.NotEqual(
            Key(Map()),
            Key(Map(), VvisOptions.Default with { Compliance = VvisOptions.Default.Compliance.Flipping(StockQuirk.VisPlaneTestPhongNormal) }));

    [Fact]
    public void TheKeyFollowsTheFogControllersFarZ() =>
        Assert.NotEqual(
            Key(Map("{\n\"classname\" \"env_fog_controller\"\n\"farz\" \"2000\"\n}\n")),
            Key(Map("{\n\"classname\" \"env_fog_controller\"\n\"farz\" \"3000\"\n}\n")));

    [Fact]
    public void AnOverrideMakesTheFogIrrelevant() =>
        // With -radius_override the map's fog is never read, so it must not
        // split the key either.
        Assert.Equal(
            Key(Map("{\n\"classname\" \"env_fog_controller\"\n\"farz\" \"2000\"\n}\n"), VvisOptions.Default with { RadiusOverride = 150f }),
            Key(Map("{\n\"classname\" \"env_fog_controller\"\n\"farz\" \"3000\"\n}\n"), VvisOptions.Default with { RadiusOverride = 150f }));
}
