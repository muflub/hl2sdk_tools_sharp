using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Diagnostics;
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
/// plan_maptools.md §10b: "the room object = vbsp half + vvis half". The room
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
    public static async Task<RoomObject> CompileAsync(
        VmfDocument document,
        RoomDefinition definition,
        VbspContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);

        definition.Validate();

        // G1 + G4 on the model, before any compile time is spent.
        MapFile map = await MapFileLoader
            .LoadAsync(context, document, cancellationToken).ConfigureAwait(false);
        MapFileReader.TakeBounds(map);
        RoomLinter.CheckModel(definition, map);

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
        PortalSet portals = PortalSet.FromPortalFile(vbsp.Portals);
        VisResult vis = await Vvis
            .ComputeAsync(vbsp.Bsp, portals, new VisContext(), cancellationToken).ConfigureAwait(false);

        return new RoomObject(
            definition,
            vbsp.Bsp,
            vis,
            lint,
            InputKeysOf(document, definition, context));
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
            + definition.Kit.Depth.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        _ = context; // context-dependent keys (options, content revisions) join at the cache layer, §10a.
        return [model, claimed];
    }
}
