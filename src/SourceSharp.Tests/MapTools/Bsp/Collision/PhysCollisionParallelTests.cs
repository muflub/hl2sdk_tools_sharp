//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys.Managed;
using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Collision;

/// <summary>
/// <c>EmitPhysCollision</c>'s model loop with the
/// cooks started together above one thread (plan 3p): the lump must be the
/// one-at-a-time loop's, byte for byte.
/// </summary>
public class PhysCollisionParallelTests
{
    [Fact]
    public async Task TheLumpIsTheSameAtEveryDegree()
    {
        PhysCollisionResult serial = await EmitAsync(1);
        PhysCollisionResult parallel = await EmitAsync(8);

        Assert.Equal(serial.PhysCollide, parallel.PhysCollide);
    }

    [Fact]
    public async Task TheRecordsStayInModelOrderAtEveryDegree()
    {
        PhysCollisionResult parallel = await EmitAsync(8);

        Assert.Equal(Enumerable.Range(0, 6), parallel.Models.Select(m => m.ModelIndex));
    }

    [Fact]
    public async Task TheWorldsMaterialTableIsTheSameAtEveryDegree()
    {
        PhysCollisionResult serial = await EmitAsync(1);
        PhysCollisionResult parallel = await EmitAsync(8);

        Assert.Equal(serial.WorldMaterials, parallel.WorldMaterials);
        Assert.Equal(serial.Models[0].KeyText, parallel.Models[0].KeyText);
    }

    // Six models, each a different box with its own material, cooked by the
    // managed cooker, which really does run its work items at once.
    private static async Task<PhysCollisionResult> EmitAsync(int degree)
    {
        CollisionFixture f = new();
        string[] materials = ["metal", "wood", "concrete", "glass", "dirt", "tile"];
        for (int m = 0; m < 6; m++)
        {
            float h = 16f + (8f * m);
            f.Box(m, new Vec3(-h, -h, -h + m), new Vec3(h, h * 0.5f, h), CollisionContents.Solid, f.TexInfoFor(materials[m]));
        }

        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        return await PhysCollisionEmitter.EmitAsync(f.Build(6) with { MaxDegree = degree }, cooker);
    }
}
