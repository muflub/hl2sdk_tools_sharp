//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Security.Cryptography;

using SourceSharp.MapFormats.Bsp;

using SourceSharp.MapGen.Rooms;

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The 3×3 sample's levels link to pinned bytes: the whole linked BSP and,
/// on its own, its world collision.
/// </summary>
/// <remarks>
/// <para>
/// The collision path is the one a change to the surface compile reaches
/// twice: each room's compile cooks its world collision into one compact
/// surface, and the link rebuilds every room's convexes into one surface for
/// the level. Both end in the surface builder's tree and mass properties, so
/// a speed-up there that moved a bit would move these digests. They were
/// captured before the rotation inertia's three passes became one, and
/// holding them afterwards is what shows that change moved nothing.
/// </para>
/// <para>
/// The whole-BSP digests were recaptured when the link began sharing
/// planes, texdata, texinfo and strings across rooms: that renumbers the
/// tables and every reference into them, so the file's bytes move while the
/// collision digests, which the sharing does not reach, stay as they were.
/// The new values are the same with the three-pass inertia and with the
/// one-pass, so they still pin both changes.
/// </para>
/// <para>
/// Both were recaptured again when the link stopped writing the stripped
/// plug brushes: the brush lump loses them, and every later brush, leaf
/// brush entry and ledge client data (which is inside the collision lump's
/// compact surfaces) is renumbered. The earlier digests are kept
/// (<see cref="PlugsKeptDigests"/>) and still pinned: putting the plugs back
/// and the client data back to the old numbering gives those exact bytes,
/// so the numbering is all that moved.
/// </para>
/// <para>
/// The brush fold (<see cref="LevelLinkOptions.FoldBrushes"/>, on by
/// default) moved them a third time: it merges brushes, so the brush, side
/// and leaf brush lumps and the ledges' client data change. The digests
/// linked with the fold off (<see cref="UnfoldedDigests"/>) are the ones the
/// fold started from, still pinned, and <c>LevelLinkerFoldTests</c> checks
/// that the folded map is exactly the unfolded one with the fold applied.
/// </para>
/// <para>
/// Door visibility (<see cref="LevelLinkOptions.DoorVisibility"/>, on by
/// default) moved the whole-BSP digests a fourth time, and only them: the
/// visibility lump is composed through the doorways instead of closed over
/// the door graph, so it shrinks, and nothing else in the file changes
/// (<see cref="DoorVisibilityMovesOnlyTheVisibilityLump"/>). The digests the
/// link had before (<see cref="DoorGraphDigests"/>, and the unfolded and
/// plugs-kept sets) are linked with door visibility off and still pinned;
/// the collision digests did not move.
/// </para>
/// <para>
/// The compile runs under <c>ComplianceOptions.Correct</c>, whose collision
/// arithmetic is double and takes no CPU estimate, so each digest is one
/// string on every runner. Any intended change to the link's or the room
/// compile's output moves them; recapture them then from the failure
/// message, which gives both, and say in the commit what moved.
/// </para>
/// </remarks>
public sealed class LinkedCollisionDigestTests(Rooms3x3Fixture fixture) : IClassFixture<Rooms3x3Fixture>
{
    /// <summary>Each case with the SHA-256 of its linked BSP (canonical) and of its <c>PhysCollide</c> lump.</summary>
    public static TheoryData<string, string, string> Digests => new()
    {
        { "rooms3x3", "25B57102F2EEB3A6ADAB7E156A0DA8B151F40231412022D2ED15F791FAF525A6", "E5E490681C195955931C17387D353C5D35B45E36712DF928E7E20531F62EE657" },
        { "rooms3x3_turn1", "E762E79A3943B7087FBDDD3EF5C908B4FA18745C26AB6E564728FFE721B66A26", "847261BD770EB47EDC3ED46DA4DF3A0259CF56DC3E27EE2FB74511B9C04E3320" },
        { "seed_9", "B2AB5B920DE8E3BD806563B360A3F8EBCCD2996129B99DB845CDBFD6A74D7B94", "0822156B74F8B7D77894120ABB40965E7A8B01568CB64C5CD7F2776F3E10E6AF" },
    };

    /// <summary>
    /// Each case's digests linked with door visibility off
    /// (<c>-nodoorvis</c>): the door graph's closure, which is what the link
    /// wrote before door visibility.
    /// </summary>
    public static TheoryData<string, string, string> DoorGraphDigests => new()
    {
        { "rooms3x3", "F604D78D1636D15C85C0C63D86F9099E686E63E82C2247EA12241141F2CA6B0F", "E5E490681C195955931C17387D353C5D35B45E36712DF928E7E20531F62EE657" },
        { "rooms3x3_turn1", "CE52A608B5B136EF203EC52F4C8F754DF8690185680BBDEA22DCEC4A809B03AA", "847261BD770EB47EDC3ED46DA4DF3A0259CF56DC3E27EE2FB74511B9C04E3320" },
        { "seed_9", "C7FDB2492DF6141AAF8D6446602FFE5FE4B89B9583340F5679B592F2A8491E8B", "0822156B74F8B7D77894120ABB40965E7A8B01568CB64C5CD7F2776F3E10E6AF" },
    };

    /// <summary>
    /// Each case's digests linked with the brush fold and door visibility off
    /// (<c>-nofold -nodoorvis</c>): the rooms' brushes as compiled, less the
    /// jointed plugs, which is what the link wrote before the fold existed.
    /// </summary>
    public static TheoryData<string, string, string> UnfoldedDigests => new()
    {
        { "rooms3x3", "E9792887B107E6571B0E979F437A36E34D3AF88070BE302E50B2C3F0407E41C4", "E8BB66F0933CA892516E37CA733FF7AA32C0D7BE7FDFD68C8F97617DC7B0B651" },
        { "rooms3x3_turn1", "C7D73ABAF865F4F68AA3DCF508CB423719B1BC5C15FD4ABFC5993D7C148746E8", "2A1061EE2607434274B2D24A3FAAC4AE5C00B903C6927C1F34E793C9D4C2191E" },
        { "seed_9", "1B36DFC36F39B7D742DCAA4F5F3AB286CA882B2138FC3A9B0EEADE9E22A6B7B9", "1E9868FEF2947E81B61A2F80003BC7E3D7CC2C8BAAFC116F5F8E343729C7CD6E" },
    };

    /// <summary>A case links to its pinned bytes.</summary>
    /// <param name="name">The case.</param>
    /// <param name="bspDigest">The linked BSP's digest.</param>
    /// <param name="collisionDigest">The linked world collision's digest.</param>
    [Theory]
    [MemberData(nameof(Digests))]
    public Task ALevelLinksToItsPinnedBytes(string name, string bspDigest, string collisionDigest) =>
        AssertDigestsAsync(name, LevelLinkOptions.Default, bspDigest, collisionDigest);

    /// <summary>
    /// A case linked with the brush fold off links to its pinned bytes: the
    /// digests the link had before the fold, so <c>-nofold</c> writes what it
    /// always wrote.
    /// </summary>
    /// <param name="name">The case.</param>
    /// <param name="bspDigest">The linked BSP's digest.</param>
    /// <param name="collisionDigest">The linked world collision's digest.</param>
    [Theory]
    [MemberData(nameof(UnfoldedDigests))]
    public Task WithoutTheFoldALevelLinksToThePinnedUnfoldedBytes(string name, string bspDigest, string collisionDigest) =>
        AssertDigestsAsync(name, new LevelLinkOptions { FoldBrushes = false, DoorVisibility = false }, bspDigest, collisionDigest);

    /// <summary>
    /// A case linked with door visibility off links to its pinned bytes: the
    /// digests the link had before door visibility, so <c>-nodoorvis</c>
    /// writes what it always wrote.
    /// </summary>
    /// <param name="name">The case.</param>
    /// <param name="bspDigest">The linked BSP's digest.</param>
    /// <param name="collisionDigest">The linked world collision's digest.</param>
    [Theory]
    [MemberData(nameof(DoorGraphDigests))]
    public Task WithoutDoorVisibilityALevelLinksToThePinnedDoorGraphBytes(string name, string bspDigest, string collisionDigest) =>
        AssertDigestsAsync(name, new LevelLinkOptions { DoorVisibility = false }, bspDigest, collisionDigest);

    /// <summary>
    /// Door visibility changes the visibility lump and nothing else: every
    /// other lump, and every game lump, is byte for byte what the link writes
    /// with it off. (On levels this small the lump need not shrink: a row of
    /// 32 clusters is four bytes all ones, and the run-length code spends two
    /// bytes on each zero byte it gains; on large levels it shrinks several
    /// times over.)
    /// </summary>
    /// <param name="name">The case.</param>
    [Theory]
    [InlineData("rooms3x3")]
    [InlineData("rooms3x3_turn1")]
    [InlineData("seed_9")]
    public async Task DoorVisibilityMovesOnlyTheVisibilityLump(string name)
    {
        Rooms3x3Case found = Rooms3x3Fixture.Cases.Single(c => c.Name == name);
        LevelGrid level = LevelYaml.Parse(found.Arrangement.LevelYaml(name, Rooms3x3Sample.LibraryFromLevels), name);
        LevelLayout layout = level.ToLayout(n => fixture.Library.Find(n)?.Definition, fixture.Library.CellSize, fixture.Library.Kit);
        LinkedLevel doors = await LevelLinker.LinkAsync(layout, fixture.Library, fixture.Context(name));
        LinkedLevel graph = await LevelLinker.LinkAsync(layout, fixture.Library, fixture.Context(name), new LevelLinkOptions { DoorVisibility = false });
        for (int lump = 0; lump < BspData.HeaderLumps; lump++)
        {
            if (lump == (int)BspLump.Visibility)
            {
                Assert.False(doors.Bsp[lump].Data.Span.SequenceEqual(graph.Bsp[lump].Data.Span), $"{name}: the visibility lump did not change");
                continue;
            }

            Assert.True(doors.Bsp[lump].Data.Span.SequenceEqual(graph.Bsp[lump].Data.Span), $"{name}: lump {(BspLump)lump} differs");
        }

        Assert.Equal(graph.Bsp.GameLumps.Count, doors.Bsp.GameLumps.Count);
        for (int g = 0; g < graph.Bsp.GameLumps.Count; g++)
        {
            Assert.True(doors.Bsp.GameLumps[g].Data.Span.SequenceEqual(graph.Bsp.GameLumps[g].Data.Span), $"{name}: game lump {g} differs");
        }
    }

    private async Task AssertDigestsAsync(string name, LevelLinkOptions options, string bspDigest, string collisionDigest)
    {
        Rooms3x3Case found = Rooms3x3Fixture.Cases.Single(c => c.Name == name);
        LevelGrid level = LevelYaml.Parse(found.Arrangement.LevelYaml(name, Rooms3x3Sample.LibraryFromLevels), name);
        LevelLayout layout = level.ToLayout(n => fixture.Library.Find(n)?.Definition, fixture.Library.CellSize, fixture.Library.Kit);
        LinkedLevel linked = await LevelLinker.LinkAsync(layout, fixture.Library, fixture.Context(name), options);
        using MemoryStream bytes = new();
        await BspFile.SaveAsync(linked.Bsp, bytes, BspWriteMode.Canonical);

        string bsp = Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
        string collision = Convert.ToHexString(SHA256.HashData(linked.Bsp[BspLump.PhysCollide].Data.Span));
        Assert.True(
            linked.Bsp[BspLump.PhysCollide].Length > 0,
            $"{name}: the linked level has no world collision, so this fact pins nothing of it");
        Assert.True(
            bsp == bspDigest && collision == collisionDigest,
            $"{name}: linked BSP {bsp}, collision {collision}; pinned {bspDigest}, {collisionDigest}");
    }

    /// <summary>
    /// Each case's digests from before the link dropped the stripped plug
    /// brushes: the whole BSP and the world collision as the link wrote them
    /// while it kept every plug as an empty brush.
    /// </summary>
    public static TheoryData<string, string, string> PlugsKeptDigests => new()
    {
        { "rooms3x3", "DC40723B4CBE9780F2BE605FEA5363E64A6904F94D0BA96ECB0ACF82CE0AFD91", "4A83B0D1DD9F1EDA93533C46AA1F4A9B9B58B1032F451618C883353679F8E1EA" },
        { "rooms3x3_turn1", "79C79B641F9C09F9803E322C821CD0B65CDFC6A4D0CBFF9AA44EE8A868B30B71", "C9E5B9A4AD15C92CDCBCE1F6E68E9CAC7BD1490707CE6AC9FB961EDFC56F14D4" },
        { "seed_9", "2A4B73C07F88FE83D4494B7A77E477D317FD7DA2D6BE884D272C28DE22EA2108", "27515AC3F80C818514521B8ACA31FFC64C144EF9A74B32BFD330ACDB9EA73F85" },
    };

    /// <summary>
    /// Dropping the stripped plug brushes moved nothing but the brush
    /// numbering: linked without the fold or door visibility, with the plugs put back as the empty brushes they were
    /// (from the rooms, <see cref="LinkedBrushProbe.WithPlugsKept"/>) and
    /// every ledge's client data put back to that numbering, the linked BSP
    /// and its world collision are the bytes they were before, to the digest.
    /// </summary>
    /// <param name="name">The case.</param>
    /// <param name="bspDigest">The BSP's digest with the plugs kept.</param>
    /// <param name="collisionDigest">The collision's digest with the plugs kept.</param>
    [Theory]
    [MemberData(nameof(PlugsKeptDigests))]
    public async Task WithThePlugsPutBackALevelIsTheBytesItWas(string name, string bspDigest, string collisionDigest)
    {
        Rooms3x3Case found = Rooms3x3Fixture.Cases.Single(c => c.Name == name);
        LevelGrid level = LevelYaml.Parse(found.Arrangement.LevelYaml(name, Rooms3x3Sample.LibraryFromLevels), name);
        LevelLayout layout = level.ToLayout(n => fixture.Library.Find(n)?.Definition, fixture.Library.CellSize, fixture.Library.Kit);
        LinkedLevel linked = await LevelLinker.LinkAsync(
            layout, fixture.Library, fixture.Context(name), new LevelLinkOptions { FoldBrushes = false, DoorVisibility = false });

        BspData old = LinkedBrushProbe.WithPlugsKept(linked, fixture.Library);
        old.SetLump(BspLump.PhysCollide, LinkedBrushProbe.CollisionWithNumbering(
            linked.Bsp, LinkedBrushProbe.PlugsKeptNumbering(linked, fixture.Library)));
        using MemoryStream bytes = new();
        await BspFile.SaveAsync(old, bytes, BspWriteMode.Canonical);

        string bsp = Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
        string collision = Convert.ToHexString(SHA256.HashData(old[BspLump.PhysCollide].Data.Span));
        Assert.Equal(collisionDigest, collision);
        Assert.Equal(bspDigest, bsp);
    }
}
