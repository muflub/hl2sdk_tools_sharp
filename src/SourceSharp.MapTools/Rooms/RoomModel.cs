using System.Globalization;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// Builds the VMF of a room: the sealed shell, the kit's openings, and the
/// plug brushes that keep the compile sealed at them.
/// </summary>
/// <remarks>
/// <para>
/// A room is authored as a sealed box and written to disk sealed, because an
/// unsealed map is not a map — vbsp itself refuses to finish one. What makes
/// it a ROOM rather than a box is what the plugs are: <c>%compileTrigger</c>
/// brushes whose leaf contents stop vbsp's flood fill (so the compile is
/// sealed and vis is computed with the door shut) but whose contents are
/// non-blocking in the engine, so the joined level is walkable at the doorway
/// and the door entity the game hangs there replaces a volume that was never
/// collision-solid to the player anyway. The linker's visibility is composed
/// from the door graph, never from a flood through these plugs (§10b).
/// </para>
/// <para>
/// The wall thickness equals the kit's depth, so the plugs of two jointed
/// rooms fill the shared wall completely and the linked map has no open shaft
/// into the wall: the only space a sealed room hides from its own vis is the
/// doorway volume itself.
/// </para>
/// </remarks>
public static class RoomModel
{
    /// <summary>The material of every shell face.</summary>
    public const string ShellMaterial = "models/worldroom/concrete_room001";

    /// <summary>The material of a socket's plug: a trigger, solid to vis, walkable in game.</summary>
    public const string PlugMaterial = "engine/trigger";

    /// <summary>
    /// Builds one room's VMF document.
    /// </summary>
    /// <param name="definition">What the room claims to be; the model matches it exactly.</param>
    /// <param name="wallThickness">
    /// The shell's thickness. It must equal the kit's depth (the plugs then
    /// fill the wall); the linter's socket rules assume it, so the builder
    /// refuses otherwise.
    /// </param>
    /// <param name="shellMaterial">Override for the shell faces.</param>
    /// <returns>The document, room-local: the cell is <c>[0,cell]³</c>.</returns>
    /// <exception cref="LinkException">The wall thickness is not the kit depth.</exception>
    public static VmfDocument Build(RoomDefinition definition, float wallThickness, string? shellMaterial = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();

        if (Math.Abs(wallThickness - definition.Kit.Depth) > 0.001f)
        {
            throw new LinkException(
                $"a room's wall thickness must equal the socket kit's depth ({definition.Kit.Depth});"
                + $" {wallThickness} would leave the jointed wall open or the plugs short");
        }

        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");

        int id = 10;
        foreach (VmfChunk solid in RoomSolids(definition, shellMaterial ?? ShellMaterial, jointed: null))
        {
            solid.AddKey("id", (id++).ToString(CultureInfo.InvariantCulture));
            world.Children.Add(solid);
        }

        document.Chunks.Add(world);
        return document;
    }

    /// <summary>
    /// Builds the merged VMF of a level: every room's world geometry at its
    /// placement, the plugs dropped at jointed sockets (that is where the
    /// monolithic compile must see through) and kept at capped ones (where it
    /// must not).
    /// </summary>
    /// <param name="layout">The level.</param>
    /// <param name="library">The rooms it names.</param>
    /// <param name="shellMaterials">The shell material per room name, or null for the default.</param>
    /// <returns>The merged document, in world coordinates.</returns>
    /// <remarks>
    /// This is the oracle the superset gate compiles monolithically: the same
    /// content as the linker assembles, minus the jointed plugs, through which
    /// vvis then sees room to room. Quarter-turn rotations and whole-cell
    /// translations are exact, so the oracle and the linked map agree to the
    /// bit on every coordinate.
    /// </remarks>
    public static VmfDocument BuildMerged(
        LevelLayout layout,
        RoomLibrary library,
        IReadOnlyDictionary<string, string>? shellMaterials = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(library);

        layout.Validate();

        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");

        int id = 10;
        foreach (RoomInstance instance in layout.Rooms)
        {
            RoomDefinition definition = library.Get(instance.Placement.Room).Definition;
            string shell = shellMaterials is not null && shellMaterials.TryGetValue(definition.Name, out string? m)
                ? m
                : ShellMaterial;

            HashSet<string> jointed = [];
            foreach ((string socket, _) in instance.Joints)
            {
                _ = jointed.Add(socket);
            }

            RoomTransform transform = new(instance.Placement, layout.CellSize);
            foreach (VmfChunk solid in RoomSolids(definition, shell, jointed))
            {
                VmfChunk moved = transform.MoveSolid(solid);
                moved.AddKey("id", (id++).ToString(CultureInfo.InvariantCulture));
                world.Children.Add(moved);
            }
        }

        document.Chunks.Add(world);
        return document;
    }

    /// <summary>
    /// The room's own solids: floor, ceiling, four walls (split around the kit
    /// openings), and the plugs — every plug, or only the unjointed ones when
    /// <paramref name="jointed"/> says which sockets the merge opens.
    /// </summary>
    private static List<VmfChunk> RoomSolids(RoomDefinition definition, string shell, HashSet<string>? jointed)
    {
        List<VmfChunk> solids = [];
        float cell = definition.CellSize;
        float wall = definition.Kit.Depth;
        int id = 1_000_000;

        solids.Add(Slab(shell, (0, 0, 0), (cell, cell, wall), id++));
        solids.Add(Slab(shell, (0, 0, cell - wall), (cell, cell, cell), id++));

        foreach (RoomFacing facing in Enum.GetValues<RoomFacing>())
        {
            RoomSocket? socket = null;
            foreach (RoomSocket s in definition.Sockets)
            {
                if (s.Facing == facing)
                {
                    socket = s;
                }
            }

            foreach ((float, float, float, float) band in WallBands(definition, socket))
            {
                (Vec3 mins, Vec3 maxs) = WallBox(facing, cell, wall, band);
                _ = mins.X;
                solids.Add(Slab(shell, mins, maxs, id++));
            }
        }

        foreach (RoomSocket socket in definition.Sockets)
        {
            if (jointed is not null && jointed.Contains(socket.Name))
            {
                continue;
            }

            Box seal = RoomLinter.SealBox(definition, socket, cell);
            solids.Add(Slab(PlugMaterial, seal.Mins, seal.Maxs, id++));
        }

        return solids;
    }

    /// <summary>
    /// The bands of one cell face: a solid face is one band; a face with a
    /// socket is the full-height strips left and right of the opening, the
    /// lintel above it, and a sill below it when the opening does not reach
    /// the floor. The opening itself is the kit rectangle exactly.
    /// </summary>
    private static List<(float, float, float, float)> WallBands(RoomDefinition definition, RoomSocket? socket)
    {
        float cell = definition.CellSize;
        if (socket is null)
        {
            return [(0f, 0f, cell, cell)];
        }

        (float u0, float v0, float u1, float v1) = definition.Kit.OpeningUnit(cell);
        float a0 = u0 * cell;
        float b0 = v0 * cell;
        float a1 = u1 * cell;
        float b1 = v1 * cell;

        List<(float, float, float, float)> bands = [];
        if (a0 > 0.001f)
        {
            bands.Add((0f, 0f, a0, cell));
        }

        if (a1 < cell - 0.001f)
        {
            bands.Add((a1, 0f, cell, cell));
        }

        if (b1 < cell - 0.001f)
        {
            bands.Add((a0, b1, a1, cell));
        }

        if (b0 > 0.001f)
        {
            bands.Add((a0, 0f, a1, b0));
        }

        return bands;
    }

    /// <summary>
    /// The wall slab's box for a face. <c>(a0,b0)-(a1,b1)</c> is the band in
    /// face coordinates: <c>a</c> runs along the face, <c>b</c> up, both within
    /// <c>[0,cell]</c>; the slab spans the wall's thickness inside the cell.
    /// </summary>
    private static (Vec3 Mins, Vec3 Maxs) WallBox(RoomFacing facing, float cell, float wall, (float, float, float, float) band)
    {
        (float a0, float b0, float a1, float b1) = band;
        return facing switch
        {
            // X-facing wall: `a` runs along y, `b` up z.
            RoomFacing.PositiveX => (new Vec3(cell - wall, a0, b0), new Vec3(cell, a1, b1)),
            RoomFacing.NegativeX => (new Vec3(0, a0, b0), new Vec3(wall, a1, b1)),
            // Y-facing wall: `a` runs along x, `b` up z.
            RoomFacing.PositiveY => (new Vec3(a0, cell - wall, b0), new Vec3(a1, cell, b1)),
            RoomFacing.NegativeY => (new Vec3(a0, 0, b0), new Vec3(a1, wall, b1)),
            _ => throw new ArgumentOutOfRangeException(nameof(facing), facing, "Unknown facing."),
        };
    }

    /// <summary>
    /// One axis-aligned box brush, in Hammer's winding order.
    /// </summary>
    /// <remarks>
    /// The three plane points of each face are ordered so the normal points
    /// OUT of the box — the same operand order the unit maps use, and the one
    /// thing a hand-built brush gets wrong by default.
    /// </remarks>
    internal static VmfChunk Slab(string material, Vec3 mins, Vec3 maxs, int id) =>
        Slab(material, (mins.X, mins.Y, mins.Z), (maxs.X, maxs.Y, maxs.Z), id);

    private static VmfChunk Slab(string material, (float, float, float) mins, (float, float, float) maxs, int id)
    {
        (float x0, float y0, float z0) = mins;
        (float x1, float y1, float z1) = maxs;

        VmfChunk solid = new("solid");
        solid.AddKey("id", id.ToString(CultureInfo.InvariantCulture));

        // +Z, -Z, +X, -X, +Y, -Y — the winding Hammer writes, copied point for
        // point from the unit maps' Box helper so the normals face OUT of the
        // box. Getting it backwards inverts the brush.
        Side((x0, y1, z1), (x1, y1, z1), (x1, y0, z1));
        Side((x0, y0, z0), (x1, y0, z0), (x1, y1, z0));
        Side((x1, y1, z1), (x1, y1, z0), (x1, y0, z0));
        Side((x0, y1, z0), (x0, y1, z1), (x0, y0, z1));
        Side((x1, y1, z1), (x0, y1, z1), (x0, y1, z0));
        Side((x0, y0, z1), (x1, y0, z1), (x1, y0, z0));

        void Side((float, float, float) a, (float, float, float) b, (float, float, float) c)
        {
            VmfChunk side = solid.AddChunk("side");
            side.AddKey("id", (id * 8).ToString(CultureInfo.InvariantCulture));
            side.AddKey("plane", $"{P(a)} {P(b)} {P(c)}");
            side.AddKey("material", material);
            side.AddKey("uaxis", "[1 0 0 0] 0.25");
            side.AddKey("vaxis", "[0 -1 0 0] 0.25");
            side.AddKey("rotation", "0");
            side.AddKey("lightmapscale", "16");
            side.AddKey("smoothing_groups", "0");
        }

        return solid;
    }

    private static string P((float, float, float) p) =>
        string.Create(CultureInfo.InvariantCulture, $"({p.Item1} {p.Item2} {p.Item3})");

    internal static string Tuple(float x, float y, float z) =>
        string.Create(CultureInfo.InvariantCulture, $"{x:0.####} {y:0.####} {z:0.####}");
}

/// <summary>
/// The exact transform of one placement: quarter turns about +z and a whole-cell
/// translation, as a component permutation with signs.
/// </summary>
/// <remarks>
/// No rotation matrix is ever multiplied: a quarter turn permutes and negates
/// components, and a whole-cell translation adds an integer multiple of the
/// cell size, so the same input byte yields the same output byte on one thread
/// or sixty-four (invariant I4). The <c>angles</c> an entity gets is the same
/// quarter turn as an integer multiple of 90, which the engine and vbsp's
/// model transforms read exactly.
/// </remarks>
public readonly record struct RoomTransform(RoomPlacement Placement, float CellSize)
{
    private readonly int _rotation = ((Placement.Rotation % 4) + 4) % 4;

    /// <summary>Applies the permutation, signs, and translation.</summary>
    /// <param name="p">The room-local point.</param>
    /// <returns>The world point.</returns>
    public readonly Vec3 Apply(Vec3 p)
    {
        float tx = Placement.CellX * CellSize;
        float ty = Placement.CellY * CellSize;
        return _rotation switch
        {
            0 => new Vec3(p.X + tx, p.Y + ty, p.Z),
            1 => new Vec3(-p.Y + tx + CellSize, p.X + ty, p.Z),
            2 => new Vec3(-p.X + tx + CellSize, -p.Y + ty + CellSize, p.Z),
            _ => new Vec3(p.Y + tx, -p.X + ty + CellSize, p.Z),
        };
    }

    /// <summary>The inverse: a world point back to the room's own coordinates.</summary>
    /// <param name="p">The world point.</param>
    /// <returns>The room-local point.</returns>
    public readonly Vec3 Unapply(Vec3 p)
    {
        float tx = Placement.CellX * CellSize;
        float ty = Placement.CellY * CellSize;
        return _rotation switch
        {
            0 => new Vec3(p.X - tx, p.Y - ty, p.Z),
            1 => new Vec3(p.Y - ty, -(p.X - tx - CellSize), p.Z),
            2 => new Vec3(-(p.X - tx - CellSize), -(p.Y - ty - CellSize), p.Z),
            _ => new Vec3(-(p.Y - ty - CellSize), p.X - tx, p.Z),
        };
    }

    /// <summary>The placement's cell-face plane normal for one of its sockets, in world axes.</summary>
    /// <param name="facing">The room-local face.</param>
    /// <returns>The outward world-space axis and its sign.</returns>
    /// <remarks>
    /// A quarter turn maps the four horizontal axes to each other; the linker
    /// matches a socket to its neighbour by these axes meeting head-on on the
    /// shared wall, which is why the kit needs no world-space position table.
    /// </remarks>
    public readonly (int Axis, int Sign) WorldNormal(RoomFacing facing)
    {
        (int axis, int sign) = facing switch
        {
            RoomFacing.PositiveX => (0, +1),
            RoomFacing.NegativeX => (0, -1),
            RoomFacing.PositiveY => (1, +1),
            RoomFacing.NegativeY => (1, -1),
            _ => throw new ArgumentOutOfRangeException(nameof(facing), facing, "Unknown facing."),
        };

        return _rotation switch
        {
            0 => (axis, sign),
            1 => (1 - axis, axis == 0 ? sign : -sign),
            2 => (axis, -sign),
            _ => (1 - axis, axis == 0 ? -sign : sign),
        };
    }

    /// <summary>Rewrites one <c>solid</c> chunk's plane points through the transform.</summary>
    /// <param name="solid">The room-local brush.</param>
    /// <returns>A new chunk whose planes are in world coordinates.</returns>
    internal readonly VmfChunk MoveSolid(VmfChunk solid)
    {
        VmfChunk moved = new(solid.Name);
        foreach (VmfKey kv in solid.Keys)
        {
            if (kv.Name == "id")
            {
                continue;
            }

            moved.AddKey(kv.Name, kv.Name == "plane" ? MovePlane(kv.Value) : kv.Value);
        }

        foreach (VmfChunk child in solid.Chunks)
        {
            VmfChunk movedSide = new(child.Name);
            foreach (VmfKey kv in child.Keys)
            {
                if (kv.Name == "id" || kv.Name == "group")
                {
                    continue;
                }

                movedSide.AddKey(kv.Name, kv.Name == "plane" ? MovePlane(kv.Value) : kv.Value);
            }

            moved.Children.Add(movedSide);
        }

        return moved;
    }

    private readonly string MovePlane(string plane)
    {
        // "(x y z) (x y z) (x y z)"
        string[] parts = plane.Split(')', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            throw new LinkException($"a brush plane must name three points, got \"{plane}\"");
        }

        System.Text.StringBuilder sb = new();
        for (int i = 0; i < 3; i++)
        {
            string trimmed = parts[i].Trim().TrimStart('(').TrimEnd(')');
            Vec3 w = Apply(ParseVec3(trimmed));
            sb.Append('(').Append(RoomModel.Tuple(w.X, w.Y, w.Z)).Append(") ");
        }

        return sb.ToString(0, sb.Length - 1);
    }

    internal static Vec3 ParseVec3(string text)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            throw new LinkException($"\"{text}\" is not three coordinates");
        }

        return new Vec3(
            float.Parse(parts[0], CultureInfo.InvariantCulture),
            float.Parse(parts[1], CultureInfo.InvariantCulture),
            float.Parse(parts[2], CultureInfo.InvariantCulture));
    }
}
