//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Props;
using SourceSharp.MapTools.Rad.Props;

namespace SourceSharp.MapTools.Rooms;

public static partial class LevelLinker
{
    /// <summary>
    /// A room's detail props for the link: its stored ones while they
    /// describe its compile, or null when its lump has none.
    /// </summary>
    /// <exception cref="LinkException">The lump has content and no detail prop data bound to the compile describes it.</exception>
    /// <remarks>
    /// The stored data is what says the room was held to the pack's detail
    /// prop rules when it was compiled (a detail entity with
    /// <c>room_needs</c> refused), so a room whose lump has props and none
    /// of it (a pack written before detail props were carried, or a room a
    /// host built without <c>ssmap room</c>) is refused, as a room with
    /// displacements and no displacement data is. Any non-zero byte counts,
    /// so a damaged lump is refused by the same text rather than read.
    /// </remarks>
    internal static RoomDetailProps? DetailPropsOf(RoomObject room)
    {
        if (room.DetailPropsOfCompile is { } details)
        {
            return details;
        }

        if (RoomDetailProps.HasContent(room.Bsp))
        {
            throw new LinkException(
                $"room {room.Definition.Name} has detail props but no detail prop data from its compile"
                + " (a pack written before the link carried detail props, or a room built without ssmap room);"
                + " recompile the library with ssmap room.");
        }

        return null;
    }

    /// <summary>
    /// Writes the level's detail prop lump into the linked map, in place of
    /// the first room's, and, in a lit level whose bakes lit them, the
    /// detail prop lighting lumps: every placement's props, moved, in the
    /// linked tree's leaves, sorted by leaf, their dictionaries merged.
    /// A level whose rooms have none is left as assembled.
    /// </summary>
    /// <param name="linked">The assembled map; its tree is final.</param>
    /// <param name="plans">The placements, in link order.</param>
    /// <param name="lit">What a lit level's rooms agree on, or null for an unlit level.</param>
    /// <param name="styles">The level's switchable styles, which renumber each style run's styles.</param>
    /// <param name="cancellationToken">Cancels the placement loop.</param>
    /// <exception cref="LinkException">
    /// The level outgrows a field (65,535 props, or a dictionary entry past
    /// a 16-bit index), or two rooms lit their detail props differently.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Order.</b> Placements in link order, each room's props in its
    /// lump's order (vbsp's sort by leaf); then a stable sort by linked leaf.
    /// vbsp sorts a map's props by leaf with the C runtime's unstable
    /// <c>qsort</c>, and the engine needs no more than each leaf's props
    /// together; a stable sort keeps each room's own order among a leaf's
    /// props, so a room placed alone links to its own compile's order, and
    /// the lump is a function of the layout alone.
    /// </para>
    /// <para>
    /// <b>Dictionaries.</b> The models (by exact name, as vbsp's dictionary
    /// dedupes) and the sprites (by their bytes, as vbsp compares them)
    /// merged in the order the placements bring them, each room's in its
    /// own dictionary's order, so a room alone keeps its dictionary as it
    /// is. vbsp adds an entry only for a prop it writes, so every entry of a
    /// room's dictionary is used, and so is every entry of the level's.
    /// </para>
    /// <para>
    /// <b>Leaves.</b> Each moved origin goes through the linked tree with
    /// vbsp's own descent (<see cref="BspTreeView.LeafOf"/>), which is what
    /// vbsp would give the point in the linked map: in a room's cell the
    /// linked tree below the top tree is the room's, so the leaf is the
    /// room's, rebased, except where a joint's carve changed it. The walk
    /// costs a few node visits a prop; a level holds at most 65,535.
    /// </para>
    /// <para>
    /// <b>Pose.</b> The stored turn's origin takes the placement's
    /// translation, the same float additions every moved point of the link
    /// takes (<see cref="RoomTransform.Translate"/>), and a zero loses its
    /// sign as the flatten writes it; the angles are final. Every other
    /// field is the room's record as compiled, the dictionary entry
    /// renumbered.
    /// </para>
    /// <para>
    /// <b>Lighting.</b> vrad lights a map's detail props once per range,
    /// LDR then HDR: each pass writes every prop's colour and style count
    /// and, for a prop with styles, the start of the run it appends to that
    /// range's style lump; a prop without styles keeps the start it had.
    /// The link replays those passes over the level's props in their linked
    /// order, with each placement's stored turn of its room's bake
    /// (<see cref="RoomDetailLight"/>) and each run's styles renumbered for
    /// the level (<see cref="LevelLightStyles"/>), so the lumps are those
    /// vrad of the linked map writes from the same colours, in vrad's
    /// directory order. A level whose bakes lit no detail prop
    /// (<c>-nodetaillight</c>) keeps the records' compiled lighting and gets
    /// no style lump, as vrad leaves such a map.
    /// </para>
    /// </remarks>
    internal static void WriteDetailProps(BspData linked, RoomPlan[] plans, LevelLight? lit, LevelLightStyles styles, CancellationToken cancellationToken = default)
    {
        if (!plans.Any(p => p.DetailProps is not null))
        {
            return;
        }

        bool lightDetails = DetailLightingOf(plans, lit);
        BspTreeView tree = BspTreeView.FromBsp(linked);
        DetailPropLump lump = new();
        Dictionary<string, int> models = new(StringComparer.Ordinal);
        Dictionary<SpriteKey, int> sprites = [];
        List<(DetailObjectLump Record, int Plan, int RoomProp)> props = [];
        for (int p = 0; p < plans.Length; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RoomPlan plan = plans[p];
            if (plan.DetailProps is not { } details)
            {
                continue;
            }

            DetailPropLump own = details.Lump;
            int[] modelMap = new int[own.ModelNames.Count];
            for (int m = 0; m < modelMap.Length; m++)
            {
                if (!models.TryGetValue(own.ModelNames[m], out int index))
                {
                    index = lump.ModelNames.Count;
                    Limit(plan, "detail prop models", index + 1, ushort.MaxValue + 1);
                    models[own.ModelNames[m]] = index;
                    lump.ModelNames.Add(own.ModelNames[m]);
                }

                modelMap[m] = index;
            }

            int[] spriteMap = new int[own.Sprites.Count];
            for (int s = 0; s < spriteMap.Length; s++)
            {
                SpriteKey key = new(own.Sprites[s]);
                if (!sprites.TryGetValue(key, out int index))
                {
                    index = lump.Sprites.Count;
                    Limit(plan, "detail prop sprites", index + 1, ushort.MaxValue + 1);
                    sprites[key] = index;
                    lump.Sprites.Add(own.Sprites[s]);
                }

                spriteMap[s] = index;
            }

            int rotation = plan.Transform.Placement.NormalizedRotation;
            Vec3[] origins = details.Origins(rotation);
            Vec3[] angles = details.Angles(rotation);
            Limit(plan, "detail props", props.Count + own.Props.Count, RoomDetailProps.MaxDetailProps);
            for (int i = 0; i < own.Props.Count; i++)
            {
                DetailObjectLump record = own.Props[i];
                record.Origin = RoomStaticProps.Unsigned(plan.Transform.Translate(origins[i]));
                record.Angles = angles[i];
                record.DetailModel = (ushort)(record.Type == (byte)DetailPropType.Model ? modelMap[record.DetailModel] : spriteMap[record.DetailModel]);
                // A leaf index fits the field: the capacity check holds the
                // level to 65,536 leaves.
                record.Leaf = (ushort)tree.LeafOf(record.Origin);
                props.Add((record, p, i));
            }
        }

        // A stable sort by leaf: equal leaves keep link order (the Comparison
        // breaks ties by the running index, which List.Sort alone would not).
        (DetailObjectLump Record, int Plan, int RoomProp)[] sorted = [.. props];
        int[] order = [.. Enumerable.Range(0, sorted.Length)];
        Array.Sort(order, (a, b) => sorted[a].Record.Leaf != sorted[b].Record.Leaf ? sorted[a].Record.Leaf.CompareTo(sorted[b].Record.Leaf) : a.CompareTo(b));
        DetailObjectLump[] records = [.. order.Select(i => sorted[i].Record)];

        List<GameLumpEntry> styleLumps = [];
        if (lightDetails && lit is not null)
        {
            Dictionary<RoomDetailLight, int[]> starts = new(ReferenceEqualityComparer.Instance);
            foreach (bool hdr in (ReadOnlySpan<bool>)[false, true])
            {
                if (!(hdr ? lit.Hdr : lit.Ldr))
                {
                    continue;
                }

                List<DetailPropLightstylesLump> runs = [];
                for (int k = 0; k < records.Length; k++)
                {
                    (_, int p, int roomProp) = sorted[order[k]];
                    RoomPlan plan = plans[p];
                    RoomDetailLight light = plan.Lit!.Range(hdr)!.Detail!;
                    if (!starts.TryGetValue(light, out int[]? runStarts))
                    {
                        starts[light] = runStarts = light.Starts();
                    }

                    ref DetailObjectLump record = ref records[k];
                    record.Lighting = light.Colors[roomProp];
                    record.LightStyleCount = light.Counts[roomProp];
                    if (light.Counts[roomProp] == 0)
                    {
                        continue;
                    }

                    record.LightStyles = (uint)runs.Count;
                    for (int s = runStarts[roomProp]; s < runStarts[roomProp + 1]; s++)
                    {
                        DetailPropLightstylesLump entry = light.Styles[s];
                        entry.Style = (byte)styles.Remap(plan.Placement.Index, entry.Style);
                        runs.Add(entry);
                    }
                }

                // As vrad writes a pass: the other range's lump first (kept,
                // or empty when that pass has not run), then this range's.
                ReplaceGameLump(styleLumps, FindStyleLump(styleLumps, !hdr) ?? DetailPropLighting.WriteStyleLump([], !hdr));
                ReplaceGameLump(styleLumps, DetailPropLighting.WriteStyleLump(CollectionsMarshal.AsSpan(runs), hdr));
            }
        }

        lump.Props.AddRange(records);
        ReplaceGameLump(linked.GameLumps, lump.Write());
        foreach (GameLumpEntry entry in styleLumps)
        {
            ReplaceGameLump(linked.GameLumps, entry);
        }
    }

    /// <summary>
    /// Whether a lit level's detail props take their rooms' baked lighting:
    /// every placed room with detail props has it for every lit range, or
    /// none has (a library lit with <c>-nodetaillight</c>).
    /// </summary>
    /// <exception cref="LinkException">Some rooms' bakes lit their detail props and some did not.</exception>
    private static bool DetailLightingOf(RoomPlan[] plans, LevelLight? lit)
    {
        if (lit is null)
        {
            return false;
        }

        string? withLight = null, without = null;
        foreach (RoomPlan plan in plans)
        {
            if (plan.DetailProps is null || plan.Lit is not { } payload)
            {
                continue;
            }

            string name = plan.Placement.Instance.Placement.Room;
            bool has = (!lit.Ldr || payload.Ldr?.Detail is not null) && (!lit.Hdr || payload.Hdr?.Detail is not null);
            if (has)
            {
                withLight ??= name;
            }
            else
            {
                without ??= name;
            }
        }

        if (withLight is not null && without is not null)
        {
            throw new LinkException(
                $"rooms {withLight} and {without} were lit with different settings (one bake lit its detail props, the other did not);"
                + " a level's rooms are lit alike. Recompile the library with ssmap room.");
        }

        return withLight is not null;
    }

    /// <summary>A style lump of one range among the ones written so far, or null.</summary>
    private static GameLumpEntry? FindStyleLump(List<GameLumpEntry> lumps, bool hdr)
    {
        int id = GameLumpId.MakeId(hdr ? GameLumpId.DetailPropLightingHdr : GameLumpId.DetailPropLighting);
        foreach (GameLumpEntry entry in lumps)
        {
            if (entry.Id == id)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>A game lump replaced in place, or appended when the list has none of its id, as vrad and vbsp place them.</summary>
    private static void ReplaceGameLump(List<GameLumpEntry> lumps, GameLumpEntry entry)
    {
        int at = lumps.FindIndex(e => e.Id == entry.Id);
        if (at >= 0)
        {
            lumps[at] = entry;
        }
        else
        {
            lumps.Add(entry);
        }
    }

    /// <summary>A sprite dictionary entry by its bytes, which is how vbsp tells two apart.</summary>
    private readonly record struct SpriteKey(int A, int B, int C, int D, int E, int F, int G, int H)
    {
        public SpriteKey(DetailSpriteDictLump s)
            : this(
                Bits(s.UpperLeft[0]), Bits(s.UpperLeft[1]), Bits(s.LowerRight[0]), Bits(s.LowerRight[1]),
                Bits(s.TexUpperLeft[0]), Bits(s.TexUpperLeft[1]), Bits(s.TexLowerRight[0]), Bits(s.TexLowerRight[1]))
        {
        }

        private static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
    }
}
