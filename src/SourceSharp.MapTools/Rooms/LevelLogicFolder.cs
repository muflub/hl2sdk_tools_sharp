//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;
using System.Text;

using SourceSharp.MapTools.Bsp.Write;
using SourceSharp.RoomContracts;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// Who names whom in a level's entities: the outputs that target each name
/// exactly, and every other way a name is reached, which keeps an entity
/// from being folded or merged.
/// </summary>
/// <remarks>
/// <para>
/// An entity can be folded away only if the linker sees every way to reach
/// it. So besides the exact output targets (the callers the fold rewrites),
/// every other reference blocks it: a key whose value is its name (a
/// parent, a template, a listener), an output parameter that holds its name
/// anywhere (an <c>AddOutput</c> that wires a new output to it at runtime),
/// a wildcard target or key whose prefix matches its name, an output that
/// targets its class, and a second entity of the same name. Names are
/// compared ignoring case, as the engine matches them. The one exception is
/// <c>filtername</c> and <c>damagefilter</c>, kept apart so identical
/// filters can be deduplicated through them.
/// </para>
/// <para>
/// A name is found inside a longer value by its resolved prefix: every
/// place a <c>c&lt;column&gt;r&lt;row&gt;_</c> starts, the token that runs to
/// the next separator (<c>,</c>, <c>:</c>, a space, a tab or ESC) is a name
/// the value reaches. Every entity the fold considers has such a name, so
/// nothing else needs looking for.
/// </para>
/// </remarks>
internal sealed class LevelReferences
{
    private readonly Dictionary<string, List<(LevelEntity Owner, LevelPair Pair)>> _callers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<LevelPair>> _filterKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _named = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _classes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _wildcards = [];

    /// <summary>The references among a level's live entities.</summary>
    public static LevelReferences Of(IEnumerable<LevelEntity> entities)
    {
        LevelReferences references = new();
        foreach (LevelEntity entity in entities)
        {
            if (entity.Removed)
            {
                continue;
            }

            int? own = entity.Find("targetname");
            if (own is int at && !entity.Pairs[at].Deleted && entity.Pairs[at].Value is { Length: > 0 } name)
            {
                references._named[name] = references._named.GetValueOrDefault(name) + 1;
            }

            for (int p = 0; p < entity.Pairs.Count; p++)
            {
                LevelPair pair = entity.Pairs[p];
                if (pair.Deleted || pair.Value is not { } value || p == own)
                {
                    continue;
                }

                references.Add(entity, pair);
            }
        }

        return references;
    }

    /// <summary>The output keys whose target is the entity's name exactly, with their owners.</summary>
    public IReadOnlyList<(LevelEntity Owner, LevelPair Pair)> CallersOf(LevelEntity entity) =>
        entity.TargetName is { } name && _callers.TryGetValue(name, out List<(LevelEntity, LevelPair)>? callers)
            ? [.. callers.Where(c => !c.Item2.Deleted && !c.Item1.Removed)]
            : [];

    /// <summary>The output keys whose target is the entity's name exactly.</summary>
    public IEnumerable<LevelPair> Callers(LevelEntity entity) => CallersOf(entity).Select(c => c.Pair);

    /// <summary>The <c>filtername</c> and <c>damagefilter</c> keys naming the entity.</summary>
    public IReadOnlyList<LevelPair> FilterKeys(LevelEntity entity) =>
        entity.TargetName is { } name && _filterKeys.TryGetValue(name, out List<LevelPair>? keys) ? keys : [];

    /// <summary>
    /// Whether anything but exact output targets (and filter keys) reaches
    /// the entity, or another entity shares its name, or it has no name.
    /// </summary>
    public bool IsBlocked(LevelEntity entity)
    {
        if (entity.TargetName is not { Length: > 0 } name)
        {
            return true;
        }

        return _blocked.Contains(name)
            || _named.GetValueOrDefault(name) != 1
            || _classes.Contains(entity.ClassName)
            || _wildcards.Any(w => name.StartsWith(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether an output targets the entity's class, or a bare wildcard reaches everything: what keeps unnamed entities apart.</summary>
    public bool IsBlockedClass(LevelEntity entity) =>
        _classes.Contains(entity.ClassName) || _wildcards.Any(w => w.Length == 0);

    /// <summary>Records a key: an output's target as a caller, anything else by what it reaches.</summary>
    public void Add(LevelEntity owner, LevelPair pair)
    {
        string value = pair.Value!;
        if (RoomOutput.TryParse(value, out RoomOutput output))
        {
            string target = output.Target;
            int star = target.IndexOf('*', StringComparison.Ordinal);
            if (star >= 0)
            {
                _wildcards.Add(target[..star]);
            }
            else if (!target.StartsWith('!') && target.Length > 0)
            {
                if (!_callers.TryGetValue(target, out List<(LevelEntity, LevelPair)>? callers))
                {
                    _callers[target] = callers = [];
                }

                callers.Add((owner, pair));
                _classes.Add(target);
            }

            Block(output.Parameter);
            return;
        }

        if (value.EndsWith('*'))
        {
            _wildcards.Add(value[..^1]);
        }

        if (string.Equals(pair.Key, "filtername", StringComparison.OrdinalIgnoreCase)
            || string.Equals(pair.Key, "damagefilter", StringComparison.OrdinalIgnoreCase))
        {
            if (!_filterKeys.TryGetValue(value, out List<LevelPair>? keys))
            {
                _filterKeys[value] = keys = [];
            }

            keys.Add(pair);
            return;
        }

        Block(value);
    }

    /// <summary>Every name a value reaches (<see cref="LevelReferences"/> remarks), blocked.</summary>
    private void Block(string value)
    {
        foreach (string token in ResolvedTokens(value))
        {
            _blocked.Add(token);
        }
    }

    /// <summary>The tokens of a value that start with a resolved prefix, each to the next separator.</summary>
    internal static IEnumerable<string> ResolvedTokens(string value)
    {
        for (int i = value.IndexOfAny(['c', 'C']); i >= 0 && i < value.Length; i = value.IndexOfAny(['c', 'C'], i + 1))
        {
            if (i > 0 && !IsSeparator(value[i - 1]))
            {
                continue;
            }

            int end = i;
            while (end < value.Length && !IsSeparator(value[end]))
            {
                end++;
            }

            string token = value[i..end];
            if (RoomNameGrammar.IsResolvedForm(token))
            {
                yield return token;
            }
        }
    }

    private static bool IsSeparator(char c) => c is ',' or ':' or ' ' or '\t' or RoomOutput.Escape;
}

/// <summary>
/// The rules that say whether one entity may be folded or merged, from its
/// own keys: the level-independent half of the fold analysis, which the
/// room compile stores as candidates and the link checks again once names
/// are resolved.
/// </summary>
internal static class RoomLogicRules
{
    /// <summary>The keys a relay may carry and still fold: nothing that runs code or changes its behaviour.</summary>
    private static readonly ImmutableHashSet<string> RelayKeys =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "classname", "targetname", "origin", "angles", "hammerid", "id", "spawnflags", "StartDisabled");

    /// <summary>The keys a branch may carry and still fold.</summary>
    private static readonly ImmutableHashSet<string> BranchKeys =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "classname", "targetname", "origin", "angles", "hammerid", "id", "InitialValue");

    /// <summary>The keys a <c>logic_room</c> may carry and still fold (its channel flags must be absent).</summary>
    private static readonly ImmutableHashSet<string> HubKeys =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "classname", "targetname", "origin", "angles", "hammerid", "id",
            LogicRoom.NeighboursKey, LogicRoom.JoinedKey, LogicRoom.RotationKey, LogicRoom.ColumnKey, LogicRoom.RowKey, LogicRoom.RoomKey);

    /// <summary>
    /// A relay that could fold: fast retrigger set (a relay without it
    /// ignores a <c>Trigger</c> until its longest output has fired, which the
    /// fold would not reproduce; the conservative rule is to require the
    /// flag), not fire-once (its count is shared by every caller), not
    /// starting disabled, no parent, and only <c>OnTrigger</c> outputs that
    /// fire every time and name neither <c>!caller</c> nor <c>!self</c> (both
    /// mean the relay before the fold and something else after).
    /// </summary>
    public static bool RelayCanFold(LevelEntity relay)
    {
        int spawnflags = EntityStage.Atoi(relay.Get("spawnflags") ?? string.Empty);
        return (spawnflags & 1) == 0 && (spawnflags & 2) != 0
            && EntityStage.Atoi(relay.Get("StartDisabled") ?? string.Empty) == 0
            && OnlyKeys(relay, RelayKeys, IsRelayOutput, requireForever: true);
    }

    /// <summary>A branch whose outputs a constant value can stand in for: only <c>OnTrue</c>/<c>OnFalse</c>, fired every time, no <c>!caller</c>/<c>!self</c>.</summary>
    public static bool BranchCanFold(LevelEntity branch) => OnlyKeys(branch, BranchKeys, IsBranchOutput, requireForever: true);

    /// <summary>A <c>logic_room</c> that is only a set of constant tests: no channel, and its outputs as a folding branch's.</summary>
    public static bool HubCanFold(LevelEntity hub) =>
        OnlyKeys(hub, HubKeys, key => LogicRoom.TryParseOutput(key, out LogicRoomOutput o) && o.Kind != LogicRoomOutputKind.Trigger, requireForever: true);

    /// <summary>A flag branch that can go into a <c>logic_room</c>: as a folding branch, but its outputs may fire a limited number of times.</summary>
    public static bool FlagCanMerge(LevelEntity branch) => OnlyKeys(branch, BranchKeys, IsBranchOutput, requireForever: false);

    /// <summary>A relay that can become a <c>logic_room</c> channel: any flags (the channel keeps them), only <c>OnTrigger</c> outputs, no <c>!caller</c>/<c>!self</c>.</summary>
    public static bool RelayCanMerge(LevelEntity relay) => OnlyKeys(relay, RelayKeys, IsRelayOutput, requireForever: false);

    /// <summary>Whether a key is a relay's trigger output.</summary>
    public static bool IsRelayOutput(string key) => string.Equals(key, "OnTrigger", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a key is a branch's output.</summary>
    public static bool IsBranchOutput(string key) =>
        string.Equals(key, "OnTrue", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "OnFalse", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a flag branch can merge into its placement's hub, given the level's references.</summary>
    public static bool CanMergeFlag(LevelEntity branch, LevelReferences references) =>
        string.Equals(branch.ClassName, RoomLinkerNames.FlagClass, StringComparison.Ordinal)
        && FlagCanMerge(branch)
        && !references.IsBlocked(branch) && references.FilterKeys(branch).Count == 0
        && references.CallersOf(branch).All(c => !ReferenceEquals(c.Owner, branch) && InputIs(c.Pair, "Test"));

    /// <summary>Whether a relay can merge into its placement's hub, given the level's references.</summary>
    public static bool CanMergeRelay(LevelEntity relay, LevelReferences references) =>
        RelayCanMerge(relay)
        && !references.IsBlocked(relay) && references.FilterKeys(relay).Count == 0
        && references.CallersOf(relay).All(c => !ReferenceEquals(c.Owner, relay)
            && RoomOutput.TryParse(c.Pair.Value, out RoomOutput o) && RelayInput(o.Input) != LogicRoomInputKind.Test);

    /// <summary>A relay input as a hub channel input, or <see cref="LogicRoomInputKind.Test"/> for one a channel does not take.</summary>
    public static LogicRoomInputKind RelayInput(string input) => input.ToUpperInvariant() switch
    {
        "TRIGGER" => LogicRoomInputKind.Trigger,
        "ENABLE" => LogicRoomInputKind.Enable,
        "DISABLE" => LogicRoomInputKind.Disable,
        "TOGGLE" => LogicRoomInputKind.Toggle,
        _ => LogicRoomInputKind.Test,
    };

    /// <summary>A relay's settings as a hub channel's flags.</summary>
    public static RelayFlags RelayFlagsOf(LevelEntity relay)
    {
        int spawnflags = EntityStage.Atoi(relay.Get("spawnflags") ?? string.Empty);
        RelayFlags flags = RelayFlags.None;
        if ((spawnflags & 1) != 0)
        {
            flags |= RelayFlags.FireOnce;
        }

        if ((spawnflags & 2) != 0)
        {
            flags |= RelayFlags.FastRetrigger;
        }

        if (EntityStage.Atoi(relay.Get("StartDisabled") ?? string.Empty) != 0)
        {
            flags |= RelayFlags.StartDisabled;
        }

        return flags;
    }

    /// <summary>The channels a hub already uses: its <c>OnTrigger&lt;k&gt;</c> outputs, its flag keys, and the channel inputs sent to it.</summary>
    public static SortedSet<int> UsedChannels(LevelEntity hub, LevelReferences references)
    {
        SortedSet<int> used = [];
        foreach (LevelPair pair in hub.Pairs)
        {
            if (pair.Deleted)
            {
                continue;
            }

            if (LogicRoom.TryParseOutput(pair.Key, out LogicRoomOutput output) && output.Kind == LogicRoomOutputKind.Trigger)
            {
                used.Add(output.Channel);
            }

            for (int k = 1; k <= LogicRoom.Channels; k++)
            {
                if (string.Equals(pair.Key, LogicRoom.RelayFlagsKey(k), StringComparison.OrdinalIgnoreCase))
                {
                    used.Add(k);
                }
            }
        }

        foreach (LevelPair caller in references.Callers(hub))
        {
            if (RoomOutput.TryParse(caller.Value, out RoomOutput o) && LogicRoom.TryParseInput(o.Input, out LogicRoomInput input)
                && input.Channel > 0)
            {
                used.Add(input.Channel);
            }
        }

        return used;
    }

    /// <summary>Whether every key is an allowed one or an output of the allowed kind (every time, when asked, and never <c>!caller</c>/<c>!self</c>).</summary>
    internal static bool OnlyKeys(LevelEntity entity, ImmutableHashSet<string> allowed, Func<string, bool> isOutput, bool requireForever)
    {
        if (!string.IsNullOrEmpty(entity.Get("parentname")))
        {
            return false;
        }

        foreach (LevelPair pair in entity.Pairs)
        {
            if (pair.Deleted)
            {
                continue;
            }

            if (pair.Value is { } value && RoomOutput.TryParse(value, out RoomOutput output))
            {
                if (!isOutput(pair.Key) || (requireForever && output.TimesToFire != -1)
                    || IsSelfish(output.Target) || IsSelfish(output.Parameter))
                {
                    return false;
                }

                continue;
            }

            if (!allowed.Contains(pair.Key))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether an output names its caller or itself: <c>!caller</c> or <c>!self</c>, which change meaning when the entity goes.</summary>
    private static bool IsSelfish(string text) =>
        string.Equals(text, "!caller", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "!self", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether an output key sends the given input.</summary>
    internal static bool InputIs(LevelPair pair, string input) =>
        RoomOutput.TryParse(pair.Value, out RoomOutput o) && string.Equals(o.Input, input, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Link-time folding: stateless logic the linker can see completely is
/// inlined into its callers and removed, at no entity and no game code.
/// </summary>
/// <remarks>
/// <para>
/// <b>What folds.</b> A relay (<see cref="RoomLogicRules.RelayCanFold"/>)
/// whose every reference is an output that sends it <c>Trigger</c>: each
/// such output <c>R,Trigger,p,d1,n</c> becomes, for each of the relay's
/// outputs <c>T,I,P,d2,-1</c>, the output <c>T,I,P,d1+d2,n</c>, in the
/// relay's output order. A constant branch
/// (<see cref="RoomLogicRules.BranchCanFold"/>: the neighbour flags, and any
/// branch that is only ever tested) likewise, each <c>Test</c> becoming the
/// branch's <c>OnTrue</c> or <c>OnFalse</c> outputs by its initial value.
/// With <c>-mod-entities</c>, a <c>logic_room</c> with no channels that is
/// only ever tested folds the same way, from its masks. Every unnamed
/// <c>logic_auto</c> without a global state and with the same spawn flags
/// merges into the first of them, outputs concatenated in level order.
/// Local filters with identical keys, reached only through
/// <c>filtername</c> and <c>damagefilter</c>, dedupe to the first.
/// </para>
/// <para>
/// <b>What never folds:</b> anything with a global name (something the
/// linker cannot see may fire it), anything reached any other way
/// (<see cref="LevelReferences"/>), anything parented, and anything
/// stateful. A relay or branch that calls itself stays.
/// </para>
/// <para>
/// <b>Order.</b> Candidates are taken in level order and the fold repeats
/// until nothing changes (relays of relays), so the result is a function of
/// the level. Delays add in real time; within one tick the order of events
/// relative to other entities' can change, which is why the library can
/// turn the fold off.
/// </para>
/// </remarks>
internal static class LevelLogicFolder
{
    /// <summary>Folds the level's entities to a fixed point.</summary>
    /// <param name="all">Every entity, in level order; removed ones are marked, and written ones may be added.</param>
    /// <param name="candidates">The entities the fold may consider, with their kind.</param>
    /// <param name="foldRooms">Whether to fold <c>logic_room</c>s too (the second pass, with <c>-mod-entities</c>).</param>
    public static void Fold(List<LevelEntity> all, IReadOnlyDictionary<LevelEntity, FoldKind> candidates, bool foldRooms)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            LevelReferences references = LevelReferences.Of(all);
            foreach (LevelEntity entity in all)
            {
                if (entity.Removed || !IsLocal(entity))
                {
                    continue;
                }

                bool folded = candidates.GetValueOrDefault(entity) switch
                {
                    FoldKind.Relay => TryFold(entity, references, RoomLogicRules.RelayCanFold, _ => "Trigger", (_, _) => Outputs(entity, RoomLogicRules.IsRelayOutput)),
                    FoldKind.Branch => TryFold(entity, references, RoomLogicRules.BranchCanFold, _ => "Test", (_, _) => BranchOutputs(entity)),
                    _ => foldRooms && string.Equals(entity.ClassName, LogicRoom.ClassName, StringComparison.Ordinal)
                        && TryFoldHub(entity, references),
                };
                changed |= folded;
            }

            if (!changed)
            {
                changed = MergeAutos(all, candidates) | DedupeFilters(all, candidates);
            }
        }
    }

    /// <summary>Whether an entity's name is one the resolver filled in: only those can fold.</summary>
    private static bool IsLocal(LevelEntity entity) => RoomNameGrammar.TryParseResolved(entity.TargetName, out _, out _, out _);

    /// <summary>
    /// Folds one relay or branch when every caller sends the one input it
    /// folds and nothing else reaches it: each caller's output replaced by
    /// the callee's outputs, delays added, the caller's fire count kept.
    /// </summary>
    private static bool TryFold(
        LevelEntity callee,
        LevelReferences references,
        Func<LevelEntity, bool> canFold,
        Func<LevelEntity, string> input,
        Func<LevelEntity, RoomOutput, List<RoomOutput>> outputs)
    {
        if (!canFold(callee) || references.IsBlocked(callee) || references.FilterKeys(callee).Count > 0)
        {
            return false;
        }

        IReadOnlyList<(LevelEntity Owner, LevelPair Pair)> callers = references.CallersOf(callee);
        if (callers.Any(c => ReferenceEquals(c.Owner, callee) || !RoomLogicRules.InputIs(c.Pair, input(callee))))
        {
            return false;
        }

        Inline(callee, callers, caller => outputs(callee, caller), references);
        return true;
    }

    /// <summary>A <c>logic_room</c> with no channels that is only tested: each test becomes the hub's answer's outputs.</summary>
    private static bool TryFoldHub(LevelEntity hub, LevelReferences references)
    {
        if (!RoomLogicRules.HubCanFold(hub) || references.IsBlocked(hub) || references.FilterKeys(hub).Count > 0)
        {
            return false;
        }

        IReadOnlyList<(LevelEntity Owner, LevelPair Pair)> callers = references.CallersOf(hub);
        foreach ((LevelEntity owner, LevelPair pair) in callers)
        {
            if (ReferenceEquals(owner, hub) || !RoomOutput.TryParse(pair.Value, out RoomOutput o)
                || !LogicRoom.TryParseInput(o.Input, out LogicRoomInput test)
                || test.Kind is not (LogicRoomInputKind.Test or LogicRoomInputKind.TestJoined))
            {
                return false;
            }
        }

        NeighbourMask neighbours = (NeighbourMask)EntityStage.Atoi(hub.Get(LogicRoom.NeighboursKey) ?? string.Empty);
        JoinedMask joined = (JoinedMask)EntityStage.Atoi(hub.Get(LogicRoom.JoinedKey) ?? string.Empty);
        Inline(hub, callers, caller =>
        {
            LogicRoom.TryParseInput(caller.Input, out LogicRoomInput test);
            string answer = test.Kind == LogicRoomInputKind.Test
                ? LogicRoom.NeighbourOutput(test.Direction, (neighbours & RoomDirections.Bit(test.Direction)) != 0)
                : LogicRoom.JoinedOutput(test.Direction, (joined & RoomDirections.JoinedBit(test.Direction)) != 0);
            return Outputs(hub, key => string.Equals(key, answer, StringComparison.OrdinalIgnoreCase));
        }, references);
        return true;
    }

    /// <summary>
    /// Replaces every caller's output by the callee's outputs for it, and
    /// removes the callee. The new outputs are recorded in the references at
    /// once, so a callee later in the same pass sees them as its callers.
    /// </summary>
    private static void Inline(
        LevelEntity callee,
        IReadOnlyList<(LevelEntity Owner, LevelPair Pair)> callers,
        Func<RoomOutput, List<RoomOutput>> outputs,
        LevelReferences references)
    {
        foreach ((LevelEntity owner, LevelPair pair) in callers)
        {
            RoomOutput.TryParse(pair.Value, out RoomOutput caller);
            int at = owner.Pairs.IndexOf(pair);
            pair.Deleted = true;
            owner.Pairs.RemoveAt(at);
            List<LevelPair> inlined = [.. outputs(caller).Select(o =>
            {
                char separator = caller.Separator == RoomOutput.Escape || o.Separator == RoomOutput.Escape ? RoomOutput.Escape : ',';
                RoomOutput composed = new(o.Target, o.Input, o.Parameter, RoomOutput.AddDelays(caller, o), caller.Times, separator);
                return new LevelPair(pair.Key, composed.Format(), pair.IsConnection);
            })];
            owner.Pairs.InsertRange(at, inlined);
            foreach (LevelPair added in inlined)
            {
                references.Add(owner, added);
            }
        }

        callee.Removed = true;
    }

    /// <summary>An entity's outputs of the keys asked for, in order.</summary>
    private static List<RoomOutput> Outputs(LevelEntity entity, Func<string, bool> key)
    {
        List<RoomOutput> outputs = [];
        foreach (LevelPair pair in entity.Pairs)
        {
            if (!pair.Deleted && key(pair.Key) && RoomOutput.TryParse(pair.Value, out RoomOutput output))
            {
                outputs.Add(output);
            }
        }

        return outputs;
    }

    /// <summary>A constant branch's answer: its <c>OnTrue</c> outputs when its initial value is not 0, else its <c>OnFalse</c>.</summary>
    private static List<RoomOutput> BranchOutputs(LevelEntity branch)
    {
        string answer = EntityStage.Atoi(branch.Get("InitialValue") ?? string.Empty) != 0 ? "OnTrue" : "OnFalse";
        return Outputs(branch, key => string.Equals(key, answer, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every unnamed, stateless <c>logic_auto</c> of the same spawn flags into the first: the others' outputs appended in level order.</summary>
    private static bool MergeAutos(List<LevelEntity> all, IReadOnlyDictionary<LevelEntity, FoldKind> candidates)
    {
        bool changed = false;
        Dictionary<int, LevelEntity> keepers = [];
        LevelReferences? references = null;
        foreach (LevelEntity entity in all)
        {
            if (entity.Removed || candidates.GetValueOrDefault(entity) != FoldKind.Auto
                || !string.Equals(entity.ClassName, "logic_auto", StringComparison.Ordinal)
                || !string.IsNullOrEmpty(entity.TargetName) || !string.IsNullOrEmpty(entity.Get("globalstate"))
                || !string.IsNullOrEmpty(entity.Get("parentname")))
            {
                continue;
            }

            // An output that targets the class reaches every logic_auto; merging would change how many.
            references ??= LevelReferences.Of(all);
            if (references.IsBlockedClass(entity))
            {
                continue;
            }

            int flags = EntityStage.Atoi(entity.Get("spawnflags") ?? string.Empty);
            if (!keepers.TryGetValue(flags, out LevelEntity? keeper))
            {
                keepers[flags] = entity;
                continue;
            }

            foreach (LevelPair pair in entity.Pairs)
            {
                if (!pair.Deleted && RoomOutput.TryParse(pair.Value, out _))
                {
                    keeper.Pairs.Add(new LevelPair(pair.Key, pair.Value!, pair.IsConnection));
                }
            }

            entity.Removed = true;
            changed = true;
        }

        return changed;
    }

    /// <summary>Local filters with the same keys (but their names and places), reached only through filter keys, into the first.</summary>
    private static bool DedupeFilters(List<LevelEntity> all, IReadOnlyDictionary<LevelEntity, FoldKind> candidates)
    {
        bool changed = false;
        LevelReferences references = LevelReferences.Of(all);
        Dictionary<string, LevelEntity> keepers = new(StringComparer.Ordinal);
        foreach (LevelEntity entity in all)
        {
            if (entity.Removed || candidates.GetValueOrDefault(entity) != FoldKind.Filter || !IsLocal(entity)
                || !string.IsNullOrEmpty(entity.Get("parentname"))
                || references.IsBlocked(entity) || references.CallersOf(entity).Count > 0)
            {
                continue;
            }

            string signature = Signature(entity);
            if (!keepers.TryGetValue(signature, out LevelEntity? keeper))
            {
                keepers[signature] = entity;
                continue;
            }

            foreach (LevelPair key in references.FilterKeys(entity))
            {
                key.Value = keeper.TargetName;
            }

            entity.Removed = true;
            changed = true;
        }

        return changed;
    }

    /// <summary>A filter's keys in order but its name, id and placement, for comparing two filters.</summary>
    private static string Signature(LevelEntity entity)
    {
        StringBuilder text = new();
        foreach (LevelPair pair in entity.Pairs)
        {
            if (pair.Deleted || pair.Key.ToUpperInvariant() is "TARGETNAME" or "HAMMERID" or "ID" or "ORIGIN" or "ANGLES")
            {
                continue;
            }

            text.Append(pair.Key.ToUpperInvariant()).Append('\0').Append(pair.Value).Append('\0');
        }

        return text.ToString();
    }
}
