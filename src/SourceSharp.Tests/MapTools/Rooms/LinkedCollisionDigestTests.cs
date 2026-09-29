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
    public async Task ALevelLinksToItsPinnedBytes(string name, string bspDigest, string collisionDigest)
    {
        Rooms3x3Case found = Rooms3x3Fixture.Cases.Single(c => c.Name == name);
        LevelGrid level = LevelYaml.Parse(found.Arrangement.LevelYaml(name, Rooms3x3Sample.LibraryFromLevels), name);
        LevelLayout layout = level.ToLayout(n => fixture.Library.Find(n)?.Definition, fixture.Library.CellSize, fixture.Library.Kit);
        LinkedLevel linked = await LevelLinker.LinkAsync(layout, fixture.Library, fixture.Context(name));
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
    /// numbering: with the plugs put back as the empty brushes they were
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
        LinkedLevel linked = await LevelLinker.LinkAsync(layout, fixture.Library, fixture.Context(name));

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
