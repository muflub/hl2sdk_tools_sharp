using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// The rule a room broke, as the linter names it.
/// </summary>
public enum RoomRule
{
    /// <summary>Brushes lie inside the room's own cells; nothing crosses a cell face except the kit.</summary>
    BrushesInsideOwnCells = 1,

    /// <summary>The shell is sealed except at sockets, and every socket is matched or capped.</summary>
    ShellSealedExceptAtSockets = 2,

    /// <summary>A straight line between two interior points cannot leave the room without crossing its own shell.</summary>
    InteriorCannotEscape = 3,

    /// <summary>Sockets come from the fixed kit at known positions on cell faces.</summary>
    SocketsFromFixedKit = 4,

    /// <summary>Placement is whole-cell translation and 90° rotation.</summary>
    PlacementIsQuarterTurnGrid = 5,
}

/// <summary>
/// A linter verdict: the room is usable, and here is what was checked.
/// </summary>
/// <param name="Rules">Every rule that ran and passed.</param>
/// <param name="InteriorClusters">Interior leaf-clusters, room-local.</param>
/// <param name="SealClusters">Which seal boxes the compile's census confirmed sealed, as socket-order indices.</param>
/// <param name="Seals">The seal brush boxes, room-local, in socket order.</param>
/// <remarks>
/// The census is the whole point of the verdict: for every socket, some solid
/// leaf of the compile fills the kit's plug box and carries a trigger-brushed
/// side, so the plug is really there, really sealed the flood fill, and really
/// is door hardware rather than a piece of shell. The plug leaf itself is
/// solid and thus cluster -1 — <c>BuildVisLeafList</c>
/// never numbers a solid leaf — so the verdict is geometry plus the trigger
/// surface flag, never a cluster number.
/// </remarks>
public sealed record RoomLintReport(
    IReadOnlyList<RoomRule> Rules,
    IReadOnlyList<int> InteriorClusters,
    IReadOnlyList<int> SealClusters,
    IReadOnlyList<Box> Seals);

/// <summary>An axis-aligned box, for the linter's cell arithmetic.</summary>
/// <param name="Mins">The low corner.</param>
/// <param name="Maxs">The high corner.</param>
public readonly record struct Box(Vec3 Mins, Vec3 Maxs)
{
    /// <summary>Whether the box's interior overlaps the other box's interior.</summary>
    /// <param name="other">The other box.</param>
    /// <param name="eps">How much overlap counts: below it the boxes merely touch.</param>
    /// <returns>True when they share interior space.</returns>
    public readonly bool Overlaps(Box other, float eps) =>
        Mins.X < other.Maxs.X - eps && other.Mins.X < Maxs.X - eps &&
        Mins.Y < other.Maxs.Y - eps && other.Mins.Y < Maxs.Y - eps &&
        Mins.Z < other.Maxs.Z - eps && other.Mins.Z < Maxs.Z - eps;

    /// <summary>Whether this box is inside the other, allowing a tolerance.</summary>
    /// <param name="outer">The containing box.</param>
    /// <param name="eps">How far out of <paramref name="outer"/> still counts as in.</param>
    /// <returns>True when contained.</returns>
    public readonly bool ContainsWithin(Box outer, float eps) =>
        outer.Mins.X - eps <= Mins.X && Maxs.X <= outer.Maxs.X + eps &&
        outer.Mins.Y - eps <= Mins.Y && Maxs.Y <= outer.Maxs.Y + eps &&
        outer.Mins.Z - eps <= Mins.Z && Maxs.Z <= outer.Maxs.Z + eps;
}

/// <summary>
/// The room linter: the five guarantees of plan_maptools.md §10b's first table,
/// as refusals.
/// </summary>
/// <remarks>
/// <para>
/// Every shortcut the linker takes — no cross-room CSG, per-room flood fill,
/// intra-room visibility independent of neighbours, seam geometry known ahead
/// of time, exact component-permutation transforms — is unsound without the
/// guarantee named on it. So none of them is assumed: each is a check here, and
/// a room that breaks one is refused rather than linked into a level whose PVS
/// would then not be a superset of anything.
/// </para>
/// <para>
/// The checks are geometric, on the parsed <see cref="MapFile"/> and the
/// compiled room, because the VMF is what the author controls and the compile
/// is what the guarantees must actually hold over. Plan §10b: "the leak pass is
/// a linter fact, not a compile step".
/// </para>
/// </remarks>
public static class RoomLinter
{
    /// <summary>How far out of a cell a brush may hang on the inside before it is "crossing".</summary>
    /// <remarks>
    /// Half a brush-bevel, which is the slop vbsp's own <c>AddBrushBevels</c>
    /// would widen a brush by; anything beyond that is the author's geometry.
    /// </remarks>
    public const float CellEpsilon = 0.05f;

    /// <summary>
    /// Checks a parsed room model against the five guarantees.
    /// </summary>
    /// <param name="definition">What the room claims to be.</param>
    /// <param name="map">The parsed room model, room-local coordinates.</param>
    /// <exception cref="RoomLintException">A guarantee is broken; the message names the rule.</exception>
    /// <remarks>
    /// Runs before the room is compiled: G1 (brushes in their cells, crossings
    /// only in the kit) and G4 (sockets from the kit at the face centres) are
    /// properties of the model. G2 and G3 need the compile and the flood result,
    /// so they run in <see cref="CheckCompiled"/>.
    /// </remarks>
    public static void CheckModel(RoomDefinition definition, MapFile map)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(map);

        definition.Validate();

        float cell = definition.CellSize;
        Box cellBox = new(Vec3.Zero, new Vec3(cell, cell, cell));

        // G4 first: the socket set has to be the kit at the face centres before
        // any crossing is excused, because the kit's rectangle is what defines
        // where a brush is allowed to cross at all.
        foreach (RoomSocket socket in definition.Sockets)
        {
            CheckSocketGeometry(definition, socket, cell);
        }

        foreach (MapBrush brush in map.Brushes)
        {
            Box box = new(brush.Mins, brush.Maxs);
            CheckBrush(definition, box, cellBox, cell, "brush");
        }
    }

    /// <summary>
    /// Checks the compile's verdict on the guarantees only a compile can show:
    /// the sealed shell (the leak pass had nothing to say) and the interior that
    /// cannot escape.
    /// </summary>
    /// <param name="definition">The room.</param>
    /// <param name="bsp">The room's compiled BSP.</param>
    /// <param name="seals">The seal boxes, room-local, in socket order.</param>
    /// <param name="leaked">Whether vbsp's own leak pass found a leak.</param>
    /// <returns>The verdict, with the clusters the linker needs.</returns>
    /// <exception cref="RoomLintException">A guarantee is broken.</exception>
    public static RoomLintReport CheckCompiled(
        RoomDefinition definition,
        BspData bsp,
        IReadOnlyList<Box> seals,
        bool leaked)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(seals);

        float cell = definition.CellSize;

        if (leaked)
        {
            throw new RoomLintException(
                $"rule {(int)RoomRule.ShellSealedExceptAtSockets} ({nameof(RoomRule.ShellSealedExceptAtSockets)}):"
                + $" the room {definition.Name} leaks — its shell is open somewhere other than a registered socket.");
        }

        IReadOnlyList<DLeaf> leaves = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
        if (leaves.Count == 0)
        {
            throw new RoomLintException(
                $"rule {(int)RoomRule.InteriorCannotEscape} ({nameof(RoomRule.InteriorCannotEscape)}):"
                + $" the room {definition.Name} has no leaves to be inside.");
        }

        // G2's second half: every socket has a seal, and the seal sits at the
        // kit rectangle on the face it claims. A socket whose plug is missing
        // would leave the room open at that face, and the assembly could leak.
        if (seals.Count != definition.Sockets.Count)
        {
            throw new RoomLintException(
                $"rule {(int)RoomRule.SocketsFromFixedKit} ({nameof(RoomRule.SocketsFromFixedKit)}):"
                + $" room {definition.Name} has {definition.Sockets.Count} sockets but {seals.Count} seal brushes.");
        }

        for (int i = 0; i < seals.Count; i++)
        {
            CheckSeal(definition, definition.Sockets[i], seals[i], cell);
        }

        // The seal census, one verdict per seal box: some solid leaf of the
        // compile must overlap the kit's plug box AND carry a trigger-brushed
        // side. Geometry alone would pass for a piece of shell that happened to
        // sit there; the trigger flag is what says the leaf is the plug —
        // %compileTrigger sets SURF_TRIGGER (→
        // MaterialSurface), and the CONTENTS_SOLID that made the leaf solid is
        // the settled plug semantics. The per-box existence
        // form cannot over-count: several solid leaves in one seal's box still
        // satisfy that one box once, and a box nobody filled stays false.
        LeafSurfaceTables tables = LeafSurfaceTables.From(bsp, seals.Count > 0);
        List<int> interior = [];
        bool[] sealedBox = new bool[seals.Count];
        for (int leafIndex = 0; leafIndex < leaves.Count; leafIndex++)
        {
            DLeaf leaf = leaves[leafIndex];
            Box box = BoxOf(leaf);

            if ((leaf.Contents & (int)BrushContents.Solid) != 0)
            {
                // Solid leaves are cluster -1 by construction —
                // BuildVisLeafList never numbers a leaf whose
                // contents are solid — so the seal verdict is the solid leaf's
                // geometry and surface flags, never its cluster. Solid leaves
                // legally hang outside the cell too: they are the void vbsp
                // wraps around the map (clipped to whole blocks), which is why
                // guarantee 1 is checked as model geometry (CheckModel) and not
                // here.
                if (OverlapsAny(box, seals))
                {
                    for (int i = 0; i < seals.Count; i++)
                    {
                        if (!sealedBox[i]
                            && box.Overlaps(seals[i], CellEpsilon)
                            && tables.HasTriggerSide(leafIndex))
                        {
                            sealedBox[i] = true;
                        }
                    }
                }

                continue;
            }

            int cluster = leaf.Cluster;
            if (cluster < 0)
            {
                continue;
            }

            // G3: an interior leaf must be inside the cell. A leaf outside the
            // cell means interior space escaped the room — the flood was stopped
            // by something that a line still passes (a clip or grate cap, whose
            // contents are not in the leak mask), which is exactly the "cannot
            // leave without crossing its own shell" guarantee failing.
            if (!box.ContainsWithin(new Box(Vec3.Zero, new Vec3(cell, cell, cell)), CellEpsilon))
            {
                throw new RoomLintException(
                    $"rule {(int)RoomRule.InteriorCannotEscape} ({nameof(RoomRule.InteriorCannotEscape)}):"
                    + $" leaf {cluster} of room {definition.Name} is open space outside the cell,"
                    + " so a line between two interior points can leave the room without crossing its shell.");
            }

            interior.Add(cluster);
        }

        if (interior.Count == 0)
        {
            throw new RoomLintException(
                $"rule {(int)RoomRule.InteriorCannotEscape} ({nameof(RoomRule.InteriorCannotEscape)}):"
                + $" room {definition.Name} has no open interior leaf.");
        }

        List<int> sealClusters = [];
        for (int i = 0; i < sealedBox.Length; i++)
        {
            if (sealedBox[i])
            {
                sealClusters.Add(i);
            }
        }

        if (sealClusters.Count != definition.Sockets.Count)
        {
            throw new RoomLintException(
                $"rule {(int)RoomRule.SocketsFromFixedKit} ({nameof(RoomRule.SocketsFromFixedKit)}):"
                + $" room {definition.Name}: {definition.Sockets.Count} sockets, but only {sealClusters.Count}"
                + " of them were sealed by a trigger plug — a plug is missing or is not trigger-brushed.");
        }

        interior.Sort();
        Dedupe(interior);

        return new RoomLintReport(
            [RoomRule.BrushesInsideOwnCells, RoomRule.ShellSealedExceptAtSockets, RoomRule.InteriorCannotEscape, RoomRule.SocketsFromFixedKit],
            interior,
            sealClusters,
            seals);
    }

    /// <summary>
    /// The guarantee about the layout rather than the room: whole-cell placement,
    /// quarter turns, and every socket matched or capped.
    /// </summary>
    /// <param name="layout">The level.</param>
    /// <param name="library">The rooms the layout names.</param>
    /// <exception cref="RoomLintException">A rule is broken.</exception>
    public static void CheckLayout(LevelLayout layout, RoomLibrary library)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(library);

        layout.Validate();

        foreach (RoomInstance room in layout.Rooms)
        {
            RoomPlacement placement = room.Placement;
            if (library.Find(placement.Room) is null)
            {
                throw new RoomLintException(
                    $"rule {(int)RoomRule.PlacementIsQuarterTurnGrid} ({nameof(RoomRule.PlacementIsQuarterTurnGrid)}):"
                    + $" the layout places room \"{placement.Room}\", which the library does not have.");
            }

            // G5's numeric half; RoomPlacement.Validate already refused a
            // rotation that is not a quarter turn, and LevelLayout.Validate a
            // shared cell.
            _ = placement.NormalizedRotation;

            // G2's other half: only the layout knows its neighbours, so "matched
            // or capped" is a layout rule.
            RoomDefinition definition = library.Get(placement.Room).Definition;
            HashSet<string> jointed = [.. room.Joints.Select(j => j.Socket)];
            HashSet<string> capped = [.. room.Capped];
            foreach (RoomSocket socket in definition.Sockets)
            {
                if (!jointed.Contains(socket.Name) && !capped.Contains(socket.Name))
                {
                    throw new RoomLintException(
                        $"rule {(int)RoomRule.ShellSealedExceptAtSockets} ({nameof(RoomRule.ShellSealedExceptAtSockets)}):"
                        + $" socket \"{socket.Name}\" of the room at cell ({placement.CellX}, {placement.CellY})"
                        + " is neither jointed to a neighbour nor capped.");
                }
            }
        }
    }

    private static void CheckBrush(RoomDefinition definition, Box box, Box cellBox, float cell, string what)
    {
        if (box.ContainsWithin(cellBox, CellEpsilon))
        {
            return;
        }

        // A brush may cross a cell face only inside a socket's kit rectangle,
        // and only as the kit's own hardware does: reaching no further inward
        // than the kit's depth and no further outward than the same.
        foreach (RoomSocket socket in definition.Sockets)
        {
            if (CrossesAtSocket(definition, box, socket, cell))
            {
                return;
            }
        }

        throw new RoomLintException(
            $"rule {(int)RoomRule.BrushesInsideOwnCells} ({nameof(RoomRule.BrushesInsideOwnCells)}):"
            + $" a {what} of room {definition.Name} crosses a cell face outside the socket kit:"
            + $" mins ({Fmt(box.Mins)}) maxs ({Fmt(box.Maxs)}) against the cell 0..{cell:0.###}.");
    }

    private static void CheckSeal(RoomDefinition definition, RoomSocket socket, Box seal, float cell)
    {
        // The seal must cover the opening and reach no further than the kit's
        // depth on either side of the face, or two jointed rooms' plugs would
        // not meet inside the wall and the sealed pair would leak.
        Box expected = SealBox(definition, socket, cell);
        if (!Within(seal, expected, 0.01f))
        {
            throw new RoomLintException(
                $"rule {(int)RoomRule.SocketsFromFixedKit} ({nameof(RoomRule.SocketsFromFixedKit)}):"
                + $" socket \"{socket.Name}\" of room {definition.Name} is sealed by a brush at"
                + $" ({Fmt(seal.Mins)})-({Fmt(seal.Maxs)}) instead of the kit's"
                + $" ({Fmt(expected.Mins)})-({Fmt(expected.Maxs)}).");
        }
    }

    /// <summary>The kit's plug box for one socket, room-local.</summary>
    /// <param name="definition">The room.</param>
    /// <param name="socket">The socket.</param>
    /// <param name="cell">The cell edge.</param>
    /// <returns>The box the plug brush must occupy.</returns>
    public static Box SealBox(RoomDefinition definition, RoomSocket socket, float cell)
    {
        ArgumentNullException.ThrowIfNull(definition);

        (float u0, float v0, float u1, float v1) = definition.Kit.OpeningUnit(cell);
        float depth = definition.Kit.Depth;

        // The plug is INWARD from the face, its outer side exactly on the cell
        // face, so the room's brushes never cross the face at all (guarantee 1
        // stays absolute) and two jointed rooms' plugs meet face to face in the
        // wall — which is what keeps a legal assembly sealed before any door is
        // hung.
        float nu0 = u0 * cell;
        float nu1 = u1 * cell;
        float nv0 = v0 * cell;
        float nv1 = v1 * cell;

        return socket.Facing switch
        {
            RoomFacing.PositiveX => new Box(new Vec3(cell - depth, nu0, nv0), new Vec3(cell, nu1, nv1)),
            RoomFacing.NegativeX => new Box(new Vec3(0, nu0, nv0), new Vec3(depth, nu1, nv1)),
            RoomFacing.PositiveY => new Box(new Vec3(nu0, cell - depth, nv0), new Vec3(nu1, cell, nv1)),
            RoomFacing.NegativeY => new Box(new Vec3(nu0, 0, nv0), new Vec3(nu1, depth, nv1)),
            _ => throw new ArgumentOutOfRangeException(nameof(socket), socket, "Unknown facing."),
        };
    }

    private static void CheckSocketGeometry(RoomDefinition definition, RoomSocket socket, float cell)
    {
        _ = definition.Kit.OpeningUnit(cell);
        (float u0, _, float u1, _) = definition.Kit.OpeningUnit(cell);
        if (u0 < 0f || u1 > 1f)
        {
            throw new RoomLintException(
                $"rule {(int)RoomRule.SocketsFromFixedKit} ({nameof(RoomRule.SocketsFromFixedKit)}):"
                + $" socket \"{socket.Name}\" kit {definition.Kit} does not fit in a face of a {cell:0.###}-unit cell.");
        }
    }

    private static bool CrossesAtSocket(RoomDefinition definition, Box box, RoomSocket socket, float cell)
    {
        Box seal = SealBox(definition, socket, cell);
        // The kit's hardware may cross the face only within the kit rectangle
        // and only within its depth on both sides; anything wider, taller or
        // deeper is not the kit.
        return box.Mins.X >= seal.Mins.X - CellEpsilon
            && box.Maxs.X <= seal.Maxs.X + CellEpsilon
            && box.Mins.Y >= seal.Mins.Y - CellEpsilon
            && box.Maxs.Y <= seal.Maxs.Y + CellEpsilon
            && box.Mins.Z >= seal.Mins.Z - CellEpsilon
            && box.Maxs.Z <= seal.Maxs.Z + CellEpsilon;
    }

    private static Box BoxOf(DLeaf leaf) =>
        new(
            new Vec3(leaf.Mins[0], leaf.Mins[1], leaf.Mins[2]),
            new Vec3(leaf.Maxs[0], leaf.Maxs[1], leaf.Maxs[2]));

    /// <summary>
    /// Which leaves of a compile carry a trigger-brushed side, indexed by leaf.
    /// </summary>
    /// <remarks>
    /// The census join: leaf brush ranges (<c>LUMP_LEAFBRUSHES</c>) to
    /// <see cref="DBrush"/> to <see cref="DBrushSide.TexInfo"/> to
    /// <see cref="TexInfo.Flags"/>; a side is trigger when its surface flags
    /// carry <see cref="SurfaceFlags.Trigger"/>. Built once per compile — the
    /// whole census is a linear walk, no per-leaf scanning.
    /// </remarks>
    private sealed class LeafSurfaceTables
    {
        private readonly bool[] _triggerLeaf;

        private LeafSurfaceTables(bool[] triggerLeaf) => _triggerLeaf = triggerLeaf;

        /// <summary>Builds the table, or an empty one when nothing needs it.</summary>
        /// <param name="bsp">The compiled room.</param>
        /// <param name="needed">Whether any seal box exists to check.</param>
        /// <returns>The table.</returns>
        public static LeafSurfaceTables From(BspData bsp, bool needed)
        {
            IReadOnlyList<DLeaf> leaves = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
            bool[] trigger = new bool[needed ? leaves.Count : 0];
            if (needed)
            {
                ushort[] leafBrushes = BspStructView.As<ushort>(bsp[BspLump.LeafBrushes]).ToArray();
                IReadOnlyList<DBrush> brushes = BspStructView.As<DBrush>(bsp[BspLump.Brushes]).ToArray();
                IReadOnlyList<DBrushSide> sides = BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]).ToArray();
                IReadOnlyList<TexInfo> texInfos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).ToArray();

                for (int i = 0; i < leaves.Count; i++)
                {
                    DLeaf leaf = leaves[i];
                    for (int b = 0; b < leaf.NumLeafBrushes && !trigger[i]; b++)
                    {
                        DBrush brush = brushes[leafBrushes[leaf.FirstLeafBrush + b]];
                        for (int s = 0; s < brush.NumSides; s++)
                        {
                            short texInfo = sides[brush.FirstSide + s].TexInfo;
                            if (texInfo >= 0
                                && (texInfos[texInfo].Flags & (int)SurfaceFlags.Trigger) != 0)
                            {
                                trigger[i] = true;
                                break;
                            }
                        }
                    }
                }
            }

            return new LeafSurfaceTables(trigger);
        }

        /// <summary>Whether the leaf's brushes include a trigger-brushed side.</summary>
        /// <param name="leafIndex">The leaf's index in the leaf lump.</param>
        /// <returns>True when some side of some leaf brush is a trigger.</returns>
        public bool HasTriggerSide(int leafIndex) =>
            (uint)leafIndex < (uint)_triggerLeaf.Length && _triggerLeaf[leafIndex];
    }

    private static bool OverlapsAny(Box box, IReadOnlyList<Box> others)
    {
        foreach (Box other in others)
        {
            if (box.Overlaps(other, CellEpsilon))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Within(Box actual, Box expected, float eps) =>
        Math.Abs(actual.Mins.X - expected.Mins.X) <= eps
        && Math.Abs(actual.Maxs.X - expected.Maxs.X) <= eps
        && Math.Abs(actual.Mins.Y - expected.Mins.Y) <= eps
        && Math.Abs(actual.Maxs.Y - expected.Maxs.Y) <= eps
        && Math.Abs(actual.Mins.Z - expected.Mins.Z) <= eps
        && Math.Abs(actual.Maxs.Z - expected.Maxs.Z) <= eps;

    private static void Dedupe(List<int> values)
    {
        int write = 0;
        for (int read = 0; read < values.Count; read++)
        {
            if (write == 0 || values[read] != values[write - 1])
            {
                values[write++] = values[read];
            }
        }

        values.RemoveRange(write, values.Count - write);
    }

    private static string Fmt(Vec3 v) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{v.X:0.###} {v.Y:0.###} {v.Z:0.###}");
}
