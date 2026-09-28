//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapTools.Rooms;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomNamingFacts;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Link-time folding (the design's section 6.5): what folds, how callers are
/// rewritten, everything that keeps an entity from folding, the
/// <c>logic_auto</c> merge and the filter dedupe; and that a fold never
/// changes the effective I/O, checked against the same level unfolded.
/// </summary>
public class LevelLogicFolderTests
{
    private static LevelResolution One(bool fold, params string[][] entities) =>
        Resolve(false, [new Placed("hub", 0, 0, 0, Room(entities))], fold: fold);

    /// <summary>
    /// A stateless local relay folds: each caller's output becomes the
    /// relay's outputs in order, delays added, the caller's fire count and
    /// key kept; relays of relays fold to a fixed point.
    /// </summary>
    [Fact]
    public void ARelayFoldsIntoItsCallers()
    {
        LevelResolution level = One(true,
            ["classname", "logic_relay", "targetname", "cxry_a", "spawnflags", "2",
                "OnTrigger", Out("door", "Open", "", "1"), "OnTrigger", Out("cxry_b", "Trigger", "", "0.25")],
            ["classname", "logic_relay", "targetname", "cxry_b", "spawnflags", "2", "OnTrigger", Out("lamp", "TurnOn", "x", "0.5")],
            ["classname", "func_button", "targetname", "button", "OnPressed", Out("cxry_a", "Trigger", "", "2", "1"), "OnDamaged", Out("cxry_b", "Trigger")]);

        Assert.Equal(["button"], level.Entities.Select(e => e.TargetName));
        LevelEntity button = level.Entities.Single();
        Assert.Equal(
            [("OnPressed", "door", "Open", "", "3", "1"), ("OnPressed", "lamp", "TurnOn", "x", "2.75", "1"), ("OnDamaged", "lamp", "TurnOn", "x", "0.5", "-1")],
            button.Pairs.Where(p => RoomOutput.TryParse(p.Value, out _)).Select(p =>
            {
                RoomOutput.TryParse(p.Value, out RoomOutput o);
                return (p.Key, o.Target, o.Input, o.Parameter, o.Delay, o.Times);
            }));
    }

    /// <summary>Every rule that keeps a relay: each case leaves the relay and its caller as they were.</summary>
    /// <remarks>An extra value with <c>|</c> in it is an output, its fields split there.</remarks>
    [Theory]
    [InlineData("0", "note", "no fast retrigger")]
    [InlineData("3", "note", "fire once")]
    [InlineData("2", "StartDisabled", "1")]
    [InlineData("2", "parentname", "train")]
    [InlineData("2", "vscripts", "a.nut")]
    [InlineData("2", "OnSpawn", "x|Open")]
    [InlineData("2", "OnTrigger", "!caller|Kill")]
    [InlineData("2", "OnTrigger", "x|SetParent|!self")]
    [InlineData("2", "OnTrigger", "x|Open||0|1")]
    public void ARelayThatCannotFoldStays(string spawnflags, string key, string value)
    {
        string[] fields = value.Split('|');
        string extra = fields.Length == 1 ? value
            : Out(fields[0], fields[1], fields.Length > 2 ? fields[2] : "", fields.Length > 3 ? fields[3] : "0", fields.Length > 4 ? fields[4] : "-1");
        string[] relay = key == "note"
            ? ["classname", "logic_relay", "targetname", "cxry_r", "spawnflags", spawnflags, "OnTrigger", Out("x", "Open")]
            : ["classname", "logic_relay", "targetname", "cxry_r", "spawnflags", spawnflags, key, extra];
        LevelResolution level = One(true, relay, ["classname", "func_button", "OnPressed", Out("cxry_r", "Trigger")]);
        Assert.NotNull(Named(level, "c0r0_r"));
        Assert.Equal("c0r0_r", Outputs(level.Entities.Single(e => e.ClassName == "func_button"), "OnPressed").Single().Target);
    }

    /// <summary>Every way of reaching a relay other than an exact <c>Trigger</c> output keeps it.</summary>
    [Theory]
    [InlineData("OnPressed", "cxry_r|Enable|")]
    [InlineData("OnPressed", "cxry_*|Trigger|")]
    [InlineData("OnPressed", "logic_relay|Trigger|")]
    [InlineData("OnPressed", "*|Trigger|")]
    [InlineData("OnPressed", "!self|AddOutput|OnUser1 c0r0_r:Trigger::0:-1")]
    [InlineData("OnPressed", "x|SetParent|cxry_r")]
    [InlineData("parentname", "cxry_r")]
    [InlineData("Template01", "cxry_r")]
    [InlineData("target", "cxry_r*")]
    public void AnyOtherReferenceKeepsTheRelay(string key, string reference)
    {
        string[] parts = reference.Split('|');
        string value = parts.Length == 3 ? Out(parts[0], parts[1], parts[2]) : parts[0];
        LevelResolution level = One(true,
            ["classname", "logic_relay", "targetname", "cxry_r", "spawnflags", "2", "OnTrigger", Out("x", "Open")],
            ["classname", "func_button", "OnPressed", Out("cxry_r", "Trigger"), key, value]);
        Assert.NotNull(Named(level, "c0r0_r"));
    }

    /// <summary>A global relay, a relay that calls itself, and two entities of one name never fold.</summary>
    [Fact]
    public void GlobalSelfCallingAndSharedNamesStay()
    {
        Assert.NotNull(Named(One(true,
            ["classname", "logic_relay", "targetname", "global", "spawnflags", "2", "OnTrigger", Out("x", "Open")],
            ["classname", "func_button", "OnPressed", Out("global", "Trigger")]), "global"));
        Assert.NotNull(Named(One(true,
            ["classname", "logic_relay", "targetname", "cxry_loop", "spawnflags", "2", "OnTrigger", Out("cxry_loop", "Trigger", "", "1")]), "c0r0_loop"));
        Assert.Equal(2, One(true,
            ["classname", "logic_relay", "targetname", "cxry_r", "spawnflags", "2"],
            ["classname", "info_target", "targetname", "cxry_r"],
            ["classname", "func_button", "OnPressed", Out("cxry_r", "Trigger")]).Entities.Count(e => e.TargetName == "c0r0_r"));
    }

    /// <summary>A branch whose only inputs are <c>Test</c> folds to the outputs its initial value picks; a <c>SetValue</c> keeps it.</summary>
    [Theory]
    [InlineData("1", "Test", true)]
    [InlineData("0", "Test", true)]
    [InlineData("1", "SetValue", false)]
    [InlineData("1", "Toggle", false)]
    public void AConstantBranchFolds(string initial, string input, bool folds)
    {
        LevelResolution level = One(true,
            ["classname", "logic_branch", "targetname", "cxry_b", "InitialValue", initial, "OnTrue", Out("yes", "Use"), "OnFalse", Out("no", "Use")],
            ["classname", "func_button", "targetname", "button", "OnPressed", Out("cxry_b", input)]);
        Assert.Equal(!folds, Named(level, "c0r0_b") is not null);
        if (folds)
        {
            Assert.Equal(initial == "1" ? "yes" : "no", Outputs(Named(level, "button")!, "OnPressed").Single().Target);
        }
    }

    /// <summary>
    /// Every unnamed stateless <c>logic_auto</c> of the same spawn flags
    /// merges into the first, across placements, outputs in level order; a
    /// named one, one with a global state, another flag set, or an output that
    /// targets the class keeps them apart.
    /// </summary>
    [Fact]
    public void LogicAutosMerge()
    {
        Func<int, List<LevelEntity>> room = Room(
            ["classname", "logic_auto", "spawnflags", "1", "OnMapSpawn", Out("a", "Use")],
            ["classname", "logic_auto", "spawnflags", "0", "OnMapSpawn", Out("b", "Use")],
            ["classname", "logic_auto", "globalstate", "g", "spawnflags", "1", "OnMapSpawn", Out("c", "Use")],
            ["classname", "logic_auto", "targetname", "named", "spawnflags", "1", "OnMapSpawn", Out("d", "Use")]);
        LevelResolution level = Resolve(false, [new Placed("x", 0, 0, 0, room), new Placed("x", 1, 0, 0, room)]);
        List<LevelEntity> autos = [.. level.Entities.Where(e => e.ClassName == "logic_auto")];
        Assert.Equal(6, autos.Count);
        Assert.Equal(["a", "a"], Outputs(autos[0], "OnMapSpawn").Select(o => o.Target));
        Assert.Equal(["b", "b"], Outputs(autos[1], "OnMapSpawn").Select(o => o.Target));
        Assert.Equal(0, autos[0].Placement);

        Func<int, List<LevelEntity>> targeted = Room(
            ["classname", "logic_auto", "OnMapSpawn", Out("a", "Use")],
            ["classname", "func_button", "OnPressed", Out("logic_auto", "Kill")]);
        Assert.Equal(2, Resolve(false, [new Placed("x", 0, 0, 0, targeted), new Placed("x", 1, 0, 0, targeted)])
            .Entities.Count(e => e.ClassName == "logic_auto"));
        Assert.Equal(2, Resolve(false, [new Placed("x", 0, 0, 0, room)], fold: false).Entities.Count(e => e.ClassName == "logic_auto" && e.TargetName is null && e.Get("globalstate") is null));
    }

    /// <summary>
    /// Local filters with the same keys, reached only through filter keys,
    /// dedupe to the first, the other's references rewritten; a filter an
    /// output reaches, or one that differs, stays.
    /// </summary>
    [Fact]
    public void IdenticalFiltersDedupe()
    {
        Func<int, List<LevelEntity>> room = Room(
            ["classname", "filter_activator_name", "targetname", "cxry_f", "hammerid", "3", "origin", "1 2 3", "filtername", "player"],
            ["classname", "trigger_once", "filtername", "cxry_f"]);
        LevelResolution level = Resolve(false, [new Placed("x", 0, 0, 0, room), new Placed("x", 1, 0, 0, room)]);
        Assert.Single(level.Entities, e => e.ClassName == "filter_activator_name");
        Assert.All(level.Entities.Where(e => e.ClassName == "trigger_once"), t => Assert.Equal("c0r0_f", t.Get("filtername")));

        Func<int, List<LevelEntity>> called = Room(
            ["classname", "filter_activator_name", "targetname", "cxry_f", "filtername", "player"],
            ["classname", "trigger_once", "filtername", "cxry_f", "OnTrigger", Out("cxry_f", "SetFilter")]);
        Assert.Equal(2, Resolve(false, [new Placed("x", 0, 0, 0, called), new Placed("x", 1, 0, 0, called)]).Entities.Count(e => e.ClassName == "filter_activator_name"));

        Func<int, List<LevelEntity>> other = Room(["classname", "filter_activator_name", "targetname", "cxry_f", "filtername", "npc"]);
        Assert.Equal(2, Resolve(false, [new Placed("x", 0, 0, 0, room), new Placed("y", 1, 0, 0, other)]).Entities.Count(e => e.ClassName == "filter_activator_name"));
    }

    /// <summary>
    /// The fold never changes what a level does: for a level of chained
    /// relays and constant branches, every chain from a trigger expanded to
    /// its final target, input, parameter and total delay is the same with
    /// the fold on as with it off (the unfolded level is the reference that
    /// does not fold).
    /// </summary>
    [Fact]
    public void FoldingKeepsTheEffectiveIo()
    {
        Func<int, List<LevelEntity>> room = Room(
            ["classname", "logic_relay", "targetname", "cxry_a", "spawnflags", "2",
                "OnTrigger", Out("cxry_b", "Trigger", "", "0.5"), "OnTrigger", Out("cxry_has_east", "Test", "", "1"), "OnTrigger", Out("door", "Open", "p")],
            ["classname", "logic_relay", "targetname", "cxry_b", "spawnflags", "2", "OnTrigger", Out("lamp", "TurnOn", "", "0.25")],
            ["classname", "logic_branch", "targetname", "cxry_has_east", "OnTrue", Out("sign", "Show"), "OnFalse", Out("sign", "Hide", "", "2")],
            ["classname", "func_button", "targetname", "cxry_button", "OnPressed", Out("cxry_a", "Trigger", "", "1"), "OnPressed", Out("cx+1ry_a", "Trigger")]);
        List<Placed> placements = [new("r", 0, 0, 0, room), new("r", 1, 0, 1, room)];

        LevelResolution unfolded = Resolve(false, placements, fold: false);
        LevelResolution folded = Resolve(false, placements);
        Assert.True(folded.Entities.Count < unfolded.Entities.Count);
        Assert.Equal(EffectiveIo(unfolded), EffectiveIo(folded));
        Assert.Equal(2, folded.Entities.Count);
    }

    /// <summary>
    /// Every chain from an entity that is not relay or branch logic, expanded
    /// through relays (<c>Trigger</c>) and branches (<c>Test</c>, by initial
    /// value) to what finally happens: (source, key, target, input,
    /// parameter, total delay), sorted.
    /// </summary>
    internal static List<string> EffectiveIo(LevelResolution level)
    {
        Dictionary<string, LevelEntity> byName = level.Entities.Where(e => e.TargetName is not null).ToDictionary(e => e.TargetName!, StringComparer.OrdinalIgnoreCase);
        List<string> effects = [];
        void Expand(string source, string key, RoomOutput output, float delay, int depth)
        {
            float total = delay + output.DelaySeconds;
            if (depth < 16 && byName.TryGetValue(output.Target, out LevelEntity? callee)
                && ((callee.ClassName == "logic_relay" && output.Input == "Trigger") || (callee.ClassName == "logic_branch" && output.Input == "Test")))
            {
                string answer = callee.ClassName == "logic_relay" ? "OnTrigger"
                    : EntityStageAtoi(callee.Get("InitialValue")) != 0 ? "OnTrue" : "OnFalse";
                foreach (RoomOutput next in Outputs(callee, answer))
                {
                    Expand(source, key, next, total, depth + 1);
                }

                return;
            }

            effects.Add(string.Create(CultureInfo.InvariantCulture, $"{source}|{key}|{output.Target}|{output.Input}|{output.Parameter}|{total:R}"));
        }

        foreach (LevelEntity entity in level.Entities.Where(e => e.ClassName is not ("logic_relay" or "logic_branch")))
        {
            foreach (LevelPair pair in entity.Pairs)
            {
                if (RoomOutput.TryParse(pair.Value, out RoomOutput output))
                {
                    Expand(entity.TargetName ?? entity.ClassName, pair.Key, output, 0, 0);
                }
            }
        }

        effects.Sort(StringComparer.Ordinal);
        return effects;
    }

    private static int EntityStageAtoi(string? text) => int.TryParse(text, out int value) ? value : 0;
}
