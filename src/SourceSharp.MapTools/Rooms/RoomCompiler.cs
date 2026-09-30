//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Vis;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// Turns one room's VMF into a linkable room object: the two compiles every map
/// goes through (<see cref="Vbsp"/> then <see cref="Vvis"/>), run on the room
/// alone, with the linter refusing anything the linker's shortcuts would make
/// unsound.
/// </summary>
/// <remarks>
/// <para>
/// The room object is the vbsp half plus the vvis half. The room
/// compiles exactly like a map — nothing room-specific happens inside the
/// compilers — so the only room-specific code is the linter and the two cluster
/// lists the linker will need. That is also what makes the superset gate
/// meaningful: the room's intra-room PVS is the real vvis result, not an
/// approximation the linker hopes matches.
/// </para>
/// <para>
/// The room's own vis is computed with its plugs IN place: the room is sealed by
/// them, and its PVS must be the PVS of the room with its doors shut, because
/// cross-room visibility is the door graph's business alone (§10b's second
/// table: "no cross-room CSG ⇒ intra-room visibility independent of
/// neighbours").
/// </para>
/// </remarks>
public static class RoomCompiler
{
    /// <summary>
    /// Compiles one room: load, lint, vbsp, lint the compile, vvis.
    /// </summary>
    /// <param name="document">The room's VMF, room-local (its cell is <c>[0,cell]³</c>).</param>
    /// <param name="definition">What the room claims to be; every claim is checked.</param>
    /// <param name="context">The compile context — content mounts, options, progress.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>The linkable room object.</returns>
    /// <exception cref="RoomLintException">A guarantee is broken; the message names the rule.</exception>
    /// <exception cref="MapCompileException">The room fails to compile as a map would.</exception>
    /// <remarks>
    /// Async at the edges (content reads on the caller's I/O, compute on the
    /// compile's own workers), as every stage of this port is.
    /// </remarks>
    public static Task<RoomObject> CompileAsync(
        VmfDocument document,
        RoomDefinition definition,
        VbspContext context,
        CancellationToken cancellationToken = default) =>
        HostHandoff.ReturnAsync(
            CompileCoreAsync(document, definition, context, nameKeys: null, tighteningClaimProbe: null, tighteningSettleProbe: null, waterSockets: null, cancellationToken));

    /// <summary>
    /// <see cref="CompileAsync(VmfDocument, RoomDefinition, VbspContext, CancellationToken)"/>
    /// with the library's own name-valued keys, which the naming rule reads
    /// as names besides the built-in table.
    /// </summary>
    /// <param name="document">The room's VMF, room-local.</param>
    /// <param name="definition">What the room claims to be.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="nameKeys">Name-valued keys the library adds (its <c>rooms_name_keys</c>), or null.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>The linkable room object.</returns>
    internal static Task<RoomObject> CompileAsync(
        VmfDocument document,
        RoomDefinition definition,
        VbspContext context,
        IReadOnlySet<string>? nameKeys,
        CancellationToken cancellationToken) =>
        CompileCoreAsync(document, definition, context, nameKeys, tighteningClaimProbe: null, tighteningSettleProbe: null, waterSockets: null, cancellationToken);

    /// <summary>
    /// <see cref="CompileAsync(VmfDocument, RoomDefinition, VbspContext, IReadOnlySet{string}, CancellationToken)"/>
    /// with the water sockets the room's <c>info_room</c> declares
    /// (<see cref="LibraryRoom.WaterSockets"/>): water may reach those
    /// sockets' plugs, and is held to the declarations
    /// (<see cref="RoomWater"/>).
    /// </summary>
    /// <param name="document">The room's VMF, room-local.</param>
    /// <param name="definition">What the room claims to be.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="nameKeys">Name-valued keys the library adds, or null.</param>
    /// <param name="waterSockets">The declared water sockets by socket name, or null for none.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>The linkable room object.</returns>
    internal static Task<RoomObject> CompileAsync(
        VmfDocument document,
        RoomDefinition definition,
        VbspContext context,
        IReadOnlySet<string>? nameKeys,
        IReadOnlyDictionary<string, RoomWaterSocket>? waterSockets,
        CancellationToken cancellationToken) =>
        CompileCoreAsync(document, definition, context, nameKeys, tighteningClaimProbe: null, tighteningSettleProbe: null, waterSockets, cancellationToken);

    /// <summary>
    /// <see cref="CompileAsync(VmfDocument, RoomDefinition, VbspContext, CancellationToken)"/>
    /// with the vvis half's tightening probes set.
    /// </summary>
    /// <param name="document">The room's VMF, room-local.</param>
    /// <param name="definition">What the room claims to be.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="tighteningClaimProbe">
    /// For the facts: see <see cref="VisContext.TighteningClaimProbe"/>. A fact
    /// that holds the first claimed portal here until the other workers have
    /// flowed the rest forces every one of those runs that reads it to
    /// speculate, which is the schedule a busy machine produces by chance and
    /// a fact needs on demand. Null in every real compile.
    /// </param>
    /// <param name="tighteningSettleProbe">
    /// For the facts: see <see cref="VisContext.TighteningSettleProbe"/> --
    /// how such a fact knows the other runs have flowed, and that one of them
    /// speculated. Null in every real compile.
    /// </param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>The linkable room object.</returns>
    internal static Task<RoomObject> CompileAsync(
        VmfDocument document,
        RoomDefinition definition,
        VbspContext context,
        Action<int>? tighteningClaimProbe,
        Action<int, bool>? tighteningSettleProbe,
        CancellationToken cancellationToken) =>
        CompileCoreAsync(document, definition, context, null, tighteningClaimProbe, tighteningSettleProbe, waterSockets: null, cancellationToken);

    /// <summary>The compile itself, with every setting the overloads pass.</summary>
    private static async Task<RoomObject> CompileCoreAsync(
        VmfDocument document,
        RoomDefinition definition,
        VbspContext context,
        IReadOnlySet<string>? nameKeys,
        Action<int>? tighteningClaimProbe,
        Action<int, bool>? tighteningSettleProbe,
        IReadOnlyDictionary<string, RoomWaterSocket>? waterSockets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        waterSockets ??= new Dictionary<string, RoomWaterSocket>();
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);

        definition.Validate();

        // Rule 7 on the VMF, before any compile time is spent: the naming
        // grammar and room_needs, which only the VMF shows on a static prop
        // (vbsp turns it into a prop record).
        RoomNameAnalysis.CheckVmf(
            definition.Name,
            [.. document.GetChunks(MapFileLoader.EntityChunk).Select((e, i) => LevelEntity.FromVmf(e, -1, i))],
            nameKeys);

        // A brush entity whose angles a turned placement could not treat
        // right (open point O15), refused before the compile as the split
        // refuses it for a library's rooms.
        foreach (VmfChunk entity in document.GetChunks(MapFileLoader.EntityChunk))
        {
            if (BrushEntityDirections.Problem(definition.Name, entity) is { } problem)
            {
                throw new RoomLintException(problem);
            }
        }

        // An overlay on a socket's plug (the rooms design, 4.9), refused
        // before the compile as the split refuses it for a library's rooms.
        if (RoomOverlays.PlugProblem(definition, document) is { } overlayProblem)
        {
            throw new RoomLintException(overlayProblem);
        }

        // An area portal in a socket's plug box, or one named as socket
        // furniture (the rooms design, 4.11), refused before the compile as
        // the split refuses it for a library's rooms.
        if (RoomAreaPortals.Problem(definition, document) is { } portalProblem)
        {
            throw new RoomLintException(portalProblem);
        }

        // A displacement of power 4, out of the cell or in a doorway (the
        // rooms design, 4.5), refused before the compile as the split
        // refuses it for a library's rooms.
        if (RoomDisplacements.Problem(definition, document) is { } displacementProblem)
        {
            throw new RoomLintException(displacementProblem);
        }

        // The room packs what vbsp packs for any map, the default cubemaps
        // named after the room included: the link carries every room's
        // files and renames those to the level's map name (LevelPakFiles),
        // which is how a room of a game whose sky textures resolve links
        // with its level's default cubemaps and without game files.

        // G1 + G4 on the model, before any compile time is spent.
        MapFile map = await MapFileLoader
            .LoadAsync(context, document, cancellationToken).ConfigureAwait(false);
        MapFileReader.TakeBounds(map);
        RoomLinter.CheckModel(definition, map);

        // Water that reaches a socket's plug (the rooms design, 4.6 and
        // open point O7), refused before the compile: the loaded map is
        // what knows a brush is water, from its materials.
        if (RoomWater.PlugProblem(definition, map, waterSockets) is { } waterProblem)
        {
            throw new RoomLintException(waterProblem);
        }

        // The static props as the loader read them: vbsp turns each into a
        // record and drops the entity with the keys the link still needs
        // (room_needs, socket furniture), so they are taken now. A prop
        // asking for texel lighting is refused before the compile (O13).
        IReadOnlyList<RoomPropSource> props = RoomStaticProps.Sources(map.Entities);
        RoomStaticProps.RefuseTexelLighting(definition.Name, props);

        // The compile. A room that leaks is not a room.
        VbspResult vbsp = await Vbsp.CompileAsync(map, context, cancellationToken).ConfigureAwait(false);
        if (vbsp.Bsp is null || vbsp.Portals is null)
        {
            throw new RoomLintException(
                $"rule {(int)RoomRule.ShellSealedExceptAtSockets} ({nameof(RoomRule.ShellSealedExceptAtSockets)}):"
                + $" the room {definition.Name} leaked"
                + (vbsp.Leak is null ? "." : $" — entity {vbsp.Leak.EntityId} ({vbsp.Leak.ClassName}) reached the outside."));
        }

        // G2 + G3 on the compile: the plug leaves are where the kit says, the
        // interior never escapes the cell.
        IReadOnlyList<Box> seals = SealBoxes(definition);
        RoomLintReport lint = RoomLinter.CheckCompiled(definition, vbsp.Bsp, seals, leaked: false);

        // The vvis half: the room's PVS with its doors shut.
        //
        // On the compile's own parallelism, not a default context's. vvis used
        // to get `new VisContext()`, which is every core of the machine on the
        // shared default pool: `ssmap room -threads 1` compiled vbsp on one
        // thread and vvis on all of them, and a service host that gave the
        // compile its own pool or degree lost that choice for the vis half.
        // The answer is the same either way (the tightened flow's rows are a
        // function of the map at any degree); what the host's choice decides
        // is how much of the machine the room takes, which is the host's call.
        PortalSet portals = PortalSet.FromPortalFile(vbsp.Portals);
        VisContext visContext = new()
        {
            Parallelism = context.Parallelism,
            TighteningClaimProbe = tighteningClaimProbe,
            TighteningSettleProbe = tighteningSettleProbe,
        };
        VisResult vis = await Vvis
            .ComputeAsync(vbsp.Bsp, portals, visContext, cancellationToken).ConfigureAwait(false);

        // The names per turn, from the compile's own entity list (the one the
        // link indexes), so every link of the room fills in cells and
        // nothing more.
        RoomNameTurn[] names = RoomNameAnalysis.Analyse(definition.Name, vbsp.Bsp, nameKeys);

        // The static props the link carries: each record matched to its
        // entity, its model's hull read from the content (the link has no
        // game files), the cell rule checked (O6), its pose turned four ways.
        RoomStaticProps? staticProps = await RoomStaticProps
            .BuildAsync(definition, vbsp.Bsp, props, context, cancellationToken).ConfigureAwait(false);

        // The brush entities the link carries as their own models: the runs
        // each owns, its entity's brushes (which only the loaded map says),
        // its conditions and furniture keys, its collision turned four ways.
        RoomBrushModels? brushModels = RoomBrushModels.Build(definition, vbsp.Bsp, map);

        // The cubemap samples as the loader read them (the lump holds them
        // truncated) and the names vbsp made after them, which the link
        // renames for every placement to the level's name and positions.
        RoomCubemaps? cubemaps = await RoomCubemaps
            .BuildAsync(vbsp.Bsp, context.CubemapSamples, context.MapBase, cancellationToken).ConfigureAwait(false);

        // The overlays the link carries: every record's origin and basis
        // turned four ways (the face lists, texinfos and ids are the link's).
        RoomOverlays? overlays = RoomOverlays.Build(definition.Name, vbsp.Bsp);

        // The areas and area portals the link carries: the lumps checked,
        // the clip vertices turned four ways, the portal numbers counted
        // (the areas themselves, the listings and the keys are the link's).
        RoomAreaPortals? areaPortals = RoomAreaPortals.Build(definition.Name, vbsp.Bsp);

        // The water the link carries: the records counted, the fluids read
        // from the collision, their convexes and the water overlays turned
        // four ways (the texinfos, ids and faces are the link's).
        RoomWater? water = RoomWater.Build(definition, vbsp.Bsp, waterSockets, context.Patcher.OriginalNameFor);

        // The displacements the link carries: every start position and
        // vertex vector turned four ways (the runs, faces and neighbours are
        // rebased by the link; the rest is the room's lumps byte for byte).
        RoomDisplacements? displacements = RoomDisplacements.Build(definition.Name, vbsp.Bsp);

        // The room's part of its level's map (the rooms design, 18.2): its
        // walkable faces unioned in its own frame, and its doors. The
        // markers and the label are its library's (a library compile adds
        // them, RoomLibraryCompiler); a room compiled alone has the markers
        // of the document it was given and no label.
        RoomMapView? mapView = RoomMapView.Build(definition, vbsp.Bsp, document, RoomMapView.MarkersOf(document), string.Empty);

        return new RoomObject(
            definition,
            vbsp.Bsp,
            vis,
            lint,
            InputKeysOf(document, definition, context))
        {
            Names = new RoomNameTables(names, vbsp.Bsp),
            Props = staticProps,
            BrushModels = brushModels,
            Cubemaps = cubemaps,
            Overlays = overlays,
            AreaPortals = areaPortals,
            Water = water,
            Displacements = displacements,
            MapView = mapView,
        };
    }

    /// <summary>The kit's plug boxes for every socket, room-local, in socket order.</summary>
    /// <param name="definition">The room.</param>
    /// <returns>One box per socket.</returns>
    public static IReadOnlyList<Box> SealBoxes(RoomDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();

        List<Box> seals = [];
        foreach (RoomSocket socket in definition.Sockets)
        {
            seals.Add(RoomLinter.SealBox(definition, socket, definition.CellSize));
        }

        return seals;
    }

    /// <summary>
    /// The cache keys' raw material (§10a seam): opaque strings, equal only for
    /// byte-equal inputs.
    /// </summary>
    /// <remarks>
    /// The cache package composes these with its own context (tool identity,
    /// options, content revisions) into its keys — it never interprets them, and
    /// the room side promises only: same keys ⇒ same room object bytes. The VMF
    /// text is hashed verbatim, so a reordering that changes the file changes
    /// the key even when the geometry is identical — over-missing, which the
    /// plan's §10a accepts in exchange for never serving a stale room.
    /// </remarks>
    private static IReadOnlyList<string> InputKeysOf(VmfDocument document, RoomDefinition definition, VbspContext context)
    {
        byte[] vmf = document.ToBytes();
        string model = "vmf:" + Convert.ToHexString(SHA256.HashData(vmf));
        string claimed = "room:" + definition.Name + "|" + definition.CellSize.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + "|" + definition.Kit.Width.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ","
            + definition.Kit.Height.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ","
            + definition.Kit.Depth.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + (definition.IsShaped ? "|h" + definition.Height.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
        _ = context; // context-dependent keys (options, content revisions) join at the cache layer, §10a.
        return [model, claimed];
    }
}
