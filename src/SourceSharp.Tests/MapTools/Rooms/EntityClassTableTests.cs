//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The entity class table: the compile-only rows it ships, all certain;
/// every other class an edict; exact class matching; and further rows that
/// override without touching the shipped table.
/// </summary>
public sealed class EntityClassTableTests
{
    /// <summary>The classes the tools consume, each shipped as compile-only and certain.</summary>
    public static TheoryData<string> CompileOnlyClasses() => [.. CompileOnly];

    private static string[] CompileOnly =>
    [
        "info_room", "func_detail", "env_cubemap", "info_overlay", "info_overlay_transition",
        "info_no_dynamic_shadow", "func_instance_parms", "func_instance", "prop_static",
        "info_lighting", "prop_detail", "prop_detail_sprite", "info_poi",
    ];

    /// <summary>Each class the tools consume is compile-only, certain, and says where it is consumed.</summary>
    [Theory]
    [MemberData(nameof(CompileOnlyClasses))]
    public void TheToolsCompileOnlyClassesShipAsCertain(string className)
    {
        EntityClassTable table = EntityClassTable.Default;
        Assert.Equal(EntityCost.CompileOnly, table.Classify(className));
        EntityClassRow row = table.Rows[className];
        Assert.Equal(EntityClassCertainty.Certain, row.Certainty);
        Assert.False(string.IsNullOrWhiteSpace(row.Source));
    }

    /// <summary>
    /// The shipped table is exactly the compile-only rows: no server-only or
    /// spawn-transient row is believed on the game's behalf, so the default
    /// over-counts rather than under-counts.
    /// </summary>
    [Fact]
    public void OnlyTheCompileOnlyRowsShip()
    {
        EntityClassTable table = EntityClassTable.Default;
        string[] contract = [.. SourceSharp.RoomContracts.ModEntityContract.Classes.Select(c => c.ClassName)];
        Assert.Equal(
            CompileOnly.Concat(contract).Order(StringComparer.Ordinal),
            table.Rows.Keys.Order(StringComparer.Ordinal));
        Assert.All(table.Rows.Values.Where(r => !contract.Contains(r.ClassName)), row => Assert.Equal(EntityCost.CompileOnly, row.Cost));
    }

    /// <summary>
    /// The mod entity contract's classes ship with the cost the contract
    /// declares: <c>logic_room</c> is server-only, so it takes no edict, and
    /// its row says where it comes from.
    /// </summary>
    [Fact]
    public void TheModContractsClassesShipWithTheirDeclaredCost()
    {
        EntityClassTable table = EntityClassTable.Default;
        Assert.Equal(EntityCost.ServerOnly, table.Classify(SourceSharp.RoomContracts.LogicRoom.ClassName));
        EntityClassRow row = table.Rows[SourceSharp.RoomContracts.LogicRoom.ClassName];
        Assert.Equal(EntityClassCertainty.OwnerSupplied, row.Certainty);
        Assert.Contains("SourceSharp.RoomContracts", row.Source, StringComparison.Ordinal);
        Assert.All(SourceSharp.RoomContracts.ModEntityContract.Classes, c =>
            Assert.Equal(c.Networked ? EntityCost.Edict : EntityCost.ServerOnly, table.Classify(c.ClassName)));
    }

    /// <summary>
    /// A class the table does not name counts as an edict: the logic
    /// classes believed server-only, a named overlay's accessor, a light, an
    /// entity with no class, and a compile-only class spelt in another case
    /// (vbsp matches classes exactly, so it would survive the compile).
    /// </summary>
    [Theory]
    [InlineData("logic_relay")]
    [InlineData("filter_activator_name")]
    [InlineData("info_overlay_accessor")]
    [InlineData("light")]
    [InlineData("func_viscluster")]
    [InlineData("")]
    [InlineData("Func_Detail")]
    public void AnyOtherClassIsAnEdict(string className) =>
        Assert.Equal(EntityCost.Edict, EntityClassTable.Default.Classify(className));

    /// <summary>
    /// Rows added with <see cref="EntityClassTable.With"/> classify their
    /// classes and override a shipped row of the same class, and the table
    /// they were added to is unchanged.
    /// </summary>
    [Fact]
    public void AddedRowsOverrideWithoutChangingTheOriginal()
    {
        EntityClassTable shipped = EntityClassTable.Default;
        EntityClassTable mod = shipped.With(
        [
            new EntityClassRow("logic_relay", EntityCost.ServerOnly, EntityClassCertainty.OwnerSupplied, "the mod's contract"),
            new EntityClassRow("infodecal", EntityCost.SpawnTransient, EntityClassCertainty.Believed, "removes itself once applied"),
            new EntityClassRow("func_detail", EntityCost.Edict, EntityClassCertainty.OwnerSupplied, "a mod that keeps it"),
        ]);

        Assert.Equal(EntityCost.ServerOnly, mod.Classify("logic_relay"));
        Assert.Equal(EntityCost.SpawnTransient, mod.Classify("infodecal"));
        Assert.Equal(EntityCost.Edict, mod.Classify("func_detail"));
        Assert.Equal(EntityCost.CompileOnly, mod.Classify("prop_static"));

        Assert.Equal(EntityCost.Edict, shipped.Classify("logic_relay"));
        Assert.Equal(EntityCost.CompileOnly, shipped.Classify("func_detail"));
        Assert.Equal(EntityCost.CompileOnly, EntityClassTable.Default.Classify("func_detail"));
    }

    /// <summary>Null arguments are refused.</summary>
    [Fact]
    public void NullArgumentsAreRefused()
    {
        EntityClassTable table = EntityClassTable.Default;
        Assert.Throws<ArgumentNullException>(() => table.Classify(null!));
        Assert.Throws<ArgumentNullException>(() => table.With(null!));
        Assert.Throws<ArgumentNullException>(() => table.With([null!]));
        Assert.Throws<ArgumentNullException>(() => table.With([new EntityClassRow(null!, EntityCost.Edict, EntityClassCertainty.Believed, "x")]));
    }

    /// <summary>The cap, the default reserve and the believed handle count are the design's numbers.</summary>
    [Fact]
    public void TheLimitsAreTheDesignsNumbers()
    {
        Assert.Equal(2048, EntityClassTable.EdictCap);
        Assert.Equal(512, EntityClassTable.DefaultReserve);
        Assert.Equal(4096, EntityClassTable.EntityHandles);
    }
}
