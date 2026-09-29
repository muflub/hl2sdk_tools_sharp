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
        { "rooms3x3", "174B22147BF2F84B784C369D79AC4152BB5807D4E931B08EAFCA97D958EFF447", "4A83B0D1DD9F1EDA93533C46AA1F4A9B9B58B1032F451618C883353679F8E1EA" },
        { "rooms3x3_turn1", "3D6975B2AE93DF7E2A480B62699906F434D07F9B606C931D7A886F2899412F09", "C9E5B9A4AD15C92CDCBCE1F6E68E9CAC7BD1490707CE6AC9FB961EDFC56F14D4" },
        { "seed_9", "DB77DEF5CD71BD6D4EE9E6B9BF6432AB1FAECB761045EA2004370AF89934011D", "27515AC3F80C818514521B8ACA31FFC64C144EF9A74B32BFD330ACDB9EA73F85" },
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
}
