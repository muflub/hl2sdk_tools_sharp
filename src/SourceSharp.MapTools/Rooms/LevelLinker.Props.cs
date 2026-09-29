//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Props;

using SourceSharp.RoomContracts;

namespace SourceSharp.MapTools.Rooms;

public static partial class LevelLinker
{
    /// <summary>One static prop of the linked level: which placement's which room prop, and its linked model.</summary>
    /// <param name="Placement">The placement, by index into the layout.</param>
    /// <param name="RoomProp">The prop's index in its room's lump.</param>
    /// <param name="Model">Its index in the level's merged dictionary.</param>
    internal readonly record struct LinkedProp(int Placement, int RoomProp, int Model);

    /// <summary>
    /// The level's static props before the tree exists: which props it
    /// keeps, in linked order, the merged model dictionary, and each room's
    /// linked prop indices (the names its <c>.vhv</c> files take).
    /// </summary>
    /// <param name="Props">The kept props, in linked order: placements in link order, each room's props in its lump's order.</param>
    /// <param name="Models">The merged dictionary: each model once, in the order a kept prop first names it.</param>
    /// <param name="Hulls">Per merged model, the hull meshes of the first room that brought it.</param>
    /// <param name="Files">
    /// Per room with static props, every (room prop, linked prop) pair over
    /// all its placements, for <see cref="LevelPakFiles"/>'s merge; a room
    /// whose props the level drops has an empty list.
    /// </param>
    internal sealed record LevelProps(
        IReadOnlyList<LinkedProp> Props,
        IReadOnlyList<string> Models,
        IReadOnlyList<IReadOnlyList<Vec3[]>> Hulls,
        IReadOnlyDictionary<string, IReadOnlyList<(int RoomProp, int Linked)>> Files);

    /// <summary>
    /// Plans a level's static props: every placed room's, kept or dropped by
    /// <c>room_needs</c> and the socket furniture rule, their dictionaries
    /// merged; or null when no placed room has any.
    /// </summary>
    /// <param name="resolved">The placements, in link order.</param>
    /// <param name="layout">The level.</param>
    /// <returns>The plan, or null.</returns>
    /// <exception cref="LinkException">
    /// A room's lump has props but no prop data bound to its compile, or the
    /// level outgrows the lump's fields: 65,535 props or models.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Deterministic.</b> Everything here is a function of the layout and
    /// the rooms' stored props: placements in link order, props in their
    /// room's lump order, and the dictionary in the order a kept prop first
    /// names a model, deduplicated by exact name as vbsp's own dictionary
    /// is. A model no kept prop names is not in it.
    /// </para>
    /// <para>
    /// <b>Why before the tree.</b> Which props a level keeps and their
    /// linked indices depend on the layout alone, and the level's pak needs
    /// the indices (a prop's lighting file is named by it) before the rooms
    /// are planned; only the leaf lists need the linked tree
    /// (<see cref="WritePropsAsync"/>).
    /// </para>
    /// </remarks>
    internal static LevelProps? PlanProps(ResolvedPlacement[] resolved, LevelLayout layout)
    {
        RoomStaticProps?[] rooms = new RoomStaticProps?[resolved.Length];
        bool any = false;
        for (int i = 0; i < resolved.Length; i++)
        {
            rooms[i] = PropsOf(resolved[i].Room);
            any |= rooms[i] is not null;
        }

        if (!any)
        {
            return null;
        }

        HashSet<(int, int)> occupied = [.. layout.Rooms.Select(r => (r.Placement.CellX, r.Placement.CellY))];
        int? Furniture(int placement, string socketName)
        {
            RoomStaticProps? props = rooms[placement];
            if (props is null)
            {
                return null;
            }

            RoomDefinition definition = resolved[placement].Room.Definition;
            int socket = SocketIndex(definition, socketName);
            int? priority = null;
            foreach (RoomProp prop in props.Props)
            {
                if (prop.Socket == socket)
                {
                    priority = Math.Max(priority ?? int.MinValue, prop.Priority);
                }
            }

            return priority;
        }

        List<LinkedProp> kept = [];
        List<string> models = [];
        List<IReadOnlyList<Vec3[]>> hulls = [];
        Dictionary<string, int> modelIndex = new(StringComparer.Ordinal);
        Dictionary<string, List<(int RoomProp, int Linked)>> files = new(StringComparer.Ordinal);
        for (int p = 0; p < resolved.Length; p++)
        {
            if (rooms[p] is not { } props)
            {
                continue;
            }

            ResolvedPlacement placement = resolved[p];
            RoomDefinition definition = placement.Room.Definition;
            RoomPlacement where = placement.Instance.Placement;
            JoinedMask joined = JoinedSides(definition, placement.Instance);
            if (!files.TryGetValue(definition.Name, out List<(int RoomProp, int Linked)>? roomFiles))
            {
                files[definition.Name] = roomFiles = [];
            }

            for (int i = 0; i < props.Props.Count; i++)
            {
                RoomProp prop = props.Props[i];
                bool keep = RoomNeeds.Hold(prop.Needs, where.NormalizedRotation, where.CellX, where.CellY, occupied.Contains, joined)
                    && (prop.Socket < 0
                        || SocketFurniture.Keeps(layout, index => resolved[index].Room.Definition, p, definition.Sockets[prop.Socket].Name, Furniture));
                if (!keep)
                {
                    continue;
                }

                StaticProp record = props.Lump.Props[i];
                string model = props.Lump.ModelNames[record.PropType];
                if (!modelIndex.TryGetValue(model, out int linkedModel))
                {
                    linkedModel = models.Count;
                    Limit(definition.Name, where.CellX, where.CellY, "static prop models", linkedModel + 1, ushort.MaxValue);
                    modelIndex[model] = linkedModel;
                    models.Add(model);
                    hulls.Add(props.Hulls[record.PropType]);
                }

                Limit(definition.Name, where.CellX, where.CellY, "static props", kept.Count + 1, ushort.MaxValue);
                roomFiles.Add((i, kept.Count));
                kept.Add(new LinkedProp(p, i, linkedModel));
            }
        }

        return new LevelProps(kept, models, hulls, files.ToDictionary(f => f.Key, f => (IReadOnlyList<(int, int)>)f.Value, StringComparer.Ordinal));
    }

    /// <summary>
    /// Writes the level's static prop lump into the linked map, in place of
    /// the first room's: every kept prop moved to its placement, its model
    /// renumbered into the merged dictionary, and its leaves listed by
    /// walking the linked tree with its hull.
    /// </summary>
    /// <param name="linked">The assembled map; its tree is final.</param>
    /// <param name="plan">The level's props (<see cref="PlanProps"/>).</param>
    /// <param name="plans">The placements' plans, for their transforms.</param>
    /// <param name="cancellationToken">Cancels the walks.</param>
    /// <returns>A task that completes when the lump is in <paramref name="linked"/>.</returns>
    /// <exception cref="LinkException">
    /// A prop touches no leaf of the linked map (which the room's own compile
    /// would have dropped, so the pack does not describe the room), or the
    /// leaf list outgrows its <c>ushort</c> index.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Leaves.</b> A prop's leaf list is the leaves its hull touches in
    /// the tree the engine draws it from, and the linked tree is not the
    /// room's: the top tree sits above the rooms, a jointed plug's solid
    /// leaves are carved into a doorway leaf, and socket furniture reaches
    /// into the neighbour's doorway. So the list is recomputed: the same
    /// walk vbsp makes (<see cref="StaticPropLeaves"/>), over the linked
    /// tree, with the hull rebuilt from the meshes the pack stores and the
    /// managed collision's leaf test (the default cooker's). Inside the
    /// prop's cell the linked tree below the top tree is the room's own, so
    /// the walk finds the room's leaves (rebased) and, where the hull
    /// reaches into a jointed doorway, the doorway leaves.
    /// </para>
    /// <para>
    /// <b>Pose.</b> The stored turn's origin and lighting origin take the
    /// placement's translation, the same float additions every moved point
    /// of the link takes (<see cref="RoomTransform.Translate"/>), and a zero
    /// loses its sign as the flatten writes it; the angles are final.
    /// Every other field is the room's record as compiled.
    /// </para>
    /// </remarks>
    internal static async Task WritePropsAsync(BspData linked, LevelProps plan, RoomPlan[] plans, CancellationToken cancellationToken)
    {
        BspTreeView tree = BspTreeView.FromBsp(linked);
        ManagedStaticPropCollision collision = new();
        IStaticPropHull?[] hulls = new IStaticPropHull?[plan.Models.Count];
        StaticPropLump lump = new();
        lump.ModelNames.AddRange(plan.Models);
        Dictionary<int, RoomPropPose[]> poses = [];
        foreach (LinkedProp linkedProp in plan.Props)
        {
            RoomPlan roomPlan = plans[linkedProp.Placement];
            RoomStaticProps props = PropsOf(roomPlan.Placement.Room)!;
            if (!poses.TryGetValue(linkedProp.Placement, out RoomPropPose[]? turn))
            {
                poses[linkedProp.Placement] = turn = props.Poses(roomPlan.Placement.Instance.Placement.NormalizedRotation);
            }

            StaticProp record = props.Lump.Props[linkedProp.RoomProp];
            RoomPropPose pose = turn[linkedProp.RoomProp];
            RoomTransform transform = roomPlan.Transform;
            Vec3 origin = RoomStaticProps.Unsigned(transform.Translate(pose.Origin));
            IStaticPropHull hull = hulls[linkedProp.Model] ??= await collision.BuildHullAsync(plan.Hulls[linkedProp.Model], cancellationToken).ConfigureAwait(false)
                ?? throw new LinkException($"room {roomPlan.Placement.Room.Definition.Name}'s static prop model {plan.Models[linkedProp.Model]} has no hull");
            List<ushort> leaves = await StaticPropLeaves.ComputeAsync(tree, hull, origin, pose.Angles, cancellationToken).ConfigureAwait(false);
            RoomInstance instance = roomPlan.Placement.Instance;
            if (leaves.Count == 0)
            {
                throw new LinkException(
                    $"room {roomPlan.Placement.Room.Definition.Name} at cell ({instance.Placement.CellX}, {instance.Placement.CellY}):"
                    + $" prop_static {props.Props[linkedProp.RoomProp].Id} ({plan.Models[linkedProp.Model]}) touches no leaf of the linked map");
            }

            Limit(roomPlan, "static prop leaf entries", lump.LeafEntries.Count + leaves.Count, ushort.MaxValue);
            lump.Props.Add(new StaticProp
            {
                Origin = origin,
                Angles = pose.Angles,
                PropType = (ushort)linkedProp.Model,
                FirstLeaf = (ushort)lump.LeafEntries.Count,
                LeafCount = (ushort)leaves.Count,
                Solid = record.Solid,
                Skin = record.Skin,
                FadeMinDist = record.FadeMinDist,
                FadeMaxDist = record.FadeMaxDist,
                LightingOrigin = RoomStaticProps.HasLightingOrigin(record)
                    ? RoomStaticProps.Unsigned(transform.Translate(pose.LightingOrigin))
                    : record.LightingOrigin,
                ForcedFadeScale = record.ForcedFadeScale,
                MinDxLevel = record.MinDxLevel,
                MaxDxLevel = record.MaxDxLevel,
                Flags = record.Flags,
                LightmapResolutionX = record.LightmapResolutionX,
                LightmapResolutionY = record.LightmapResolutionY,
            });
            lump.LeafEntries.AddRange(leaves);
        }

        GameLumpEntry entry = lump.Write();
        int at = linked.GameLumps.FindIndex(e => e.Id == entry.Id);
        if (at >= 0)
        {
            linked.GameLumps[at] = entry;
        }
        else
        {
            linked.GameLumps.Add(entry);
        }
    }

    /// <summary>
    /// A room's static props for the link: its stored ones while they
    /// describe its compile, or null when its lump has none.
    /// </summary>
    /// <exception cref="LinkException">The lump has props and no prop data bound to the compile describes them.</exception>
    private static RoomStaticProps? PropsOf(RoomObject room)
    {
        if (room.StaticProps is { } props)
        {
            return props;
        }

        RefuseUndescribedProps(room);
        return null;
    }

    /// <summary>
    /// Refuses a room whose static prop lump has content but that carries no
    /// prop data from its compile: the models' hulls and the keys vbsp
    /// consumed are not in the lump, and the link cannot read the game's
    /// files to find them (decision D1), so linking it would mean guessing
    /// its props' leaves and dropping its conditions.
    /// </summary>
    private static void RefuseUndescribedProps(RoomObject room)
    {
        int id = GameLumpId.MakeId(GameLumpId.StaticProps);
        foreach (GameLumpEntry entry in room.Bsp.GameLumps)
        {
            if (entry.Id == id && entry.Data.Span.ContainsAnyExcept((byte)0))
            {
                throw new LinkException(
                    $"room {room.Definition.Name} has static props but no static prop data from its compile"
                    + " (a pack written before the link carried static props, or a room built without ssmap room);"
                    + " recompile the library with ssmap room.");
            }
        }
    }
}
