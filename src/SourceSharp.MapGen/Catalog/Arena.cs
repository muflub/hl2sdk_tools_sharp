namespace SourceSharp.MapGen.Catalog;

/// <summary>
/// One big sealed room with a grid of pillars in it: §2a's "open arena — many
/// mutually visible portals", which is vvis's expensive case.
///
/// <para>
/// The pillars are the point. An empty box is one convex leaf and no portals at
/// all, so it says nothing about portal flow however big it is. A grid of
/// pillars cuts the floor into a lattice of leaves that nearly all see each
/// other, which is the quadratic case `PortalFlow` is slow on and the one where
/// a wrong answer is easiest to hide in a large PVS.
/// </para>
/// </summary>
public sealed class Arena
{
    /// <summary>How tall the room is.</summary>
    public const float Height = 384f;

    /// <summary>How far apart the pillars stand, centre to centre.</summary>
    public const float Pitch = 256f;

    /// <summary>The narrowest a pillar may be, half-width.</summary>
    public const float MinHalfWidth = 32f;

    /// <summary>The widest a pillar may be, half-width.</summary>
    public const float MaxHalfWidth = 64f;

    /// <summary>The grid is this many pillars on a side.</summary>
    public required int PillarsPerSide { get; init; }

    /// <summary>
    /// The room's interior: the pillar grid plus a clear margin all round, so
    /// the outer pillars do not meet the walls and close the lattice off.
    /// </summary>
    public Bounds Interior
    {
        get
        {
            float half = ((PillarsPerSide * Pitch) * 0.5f) + Pitch;

            return new Bounds(new Point(-half, -half, 0f), new Point(half, half, Height));
        }
    }

    /// <summary>How many world solids <see cref="Build"/> produces.</summary>
    public int BrushCount => RoomKit.ShellBrushes + (PillarsPerSide * PillarsPerSide);

    /// <summary>
    /// Builds the arena.
    ///
    /// <para>
    /// Pillar widths are drawn from the entry's own seeded source and snapped to
    /// a 16-unit grid: a lattice of identical pillars is symmetric enough that a
    /// visibility bug can cancel itself out, and off-grid widths would make the
    /// map's bytes depend on float formatting rather than on the generator.
    /// </para>
    ///
    /// <para>
    /// The draw order is row-major and fixed, which is what makes the result
    /// reproducible: the same seed consumed in the same order gives the same
    /// widths, and two entries with different names get different arenas.
    /// </para>
    /// </summary>
    /// <param name="map">The map to add to.</param>
    /// <param name="random">This entry's seeded source.</param>
    public void Build(VmfMap map, CatalogRandom random)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(random);

        if (PillarsPerSide < 1)
            throw new InvalidOperationException("an arena has at least one pillar");

        Bounds interior = Interior;

        RoomKit.Worldspawn(map);
        RoomKit.Shell(map, interior);

        float first = -((PillarsPerSide - 1) * Pitch * 0.5f);

        for (int row = 0; row < PillarsPerSide; row++)
        {
            for (int column = 0; column < PillarsPerSide; column++)
            {
                float half = random.NextOnGrid(MinHalfWidth, MaxHalfWidth, 16f);

                RoomKit.Pillar(
                    map, interior,
                    first + (column * Pitch),
                    first + (row * Pitch),
                    half);
            }
        }

        RoomKit.PlayerStart(map, new Point(0f, 0f, 16f));
        RoomKit.Light(map, new Point(0f, 0f, Height - 32f));
    }

    /// <summary>How far the pillar grid itself reaches from the middle.</summary>
    public float LatticeReach => ((PillarsPerSide - 1) * Pitch * 0.5f) + MaxHalfWidth;

    /// <summary>
    /// The probes an arena entry reasons about.
    ///
    /// <para>
    /// TWO OF THEM ARE IN THE CLEAR RING AND ON THE SAME SIDE, and that is the
    /// only pair this entry declares a relation for. The obvious pair — two
    /// opposite corners — is not usable: the straight line between them runs
    /// diagonally through the lattice and whether it threads the gaps depends on
    /// the pillar widths the seed drew. Two probes at the same X, outside the
    /// lattice's reach, have a clear straight line between them whatever the
    /// pillars do, which is what makes the expectation a fact about the map
    /// rather than about one draw.
    /// </para>
    ///
    /// <para>
    /// `centre` is declared with no relation on purpose. What a pillar lattice
    /// does to a diagonal sight line is a MEASUREMENT, and Phase 2 is where it
    /// gets made; writing a guess here would produce an expectation that fails
    /// without telling anyone whether vvis or the guess was wrong.
    /// </para>
    /// </summary>
    public IReadOnlyList<VisProbe> Probes
    {
        get
        {
            Bounds interior = Interior;
            float ring = interior.Mins.X + 64f;
            float along = (PillarsPerSide - 1) * Pitch * 0.5f;

            return
            [
                new VisProbe("centre", new Point(0f, 0f, 64f)),
                new VisProbe("ring_low", new Point(ring, -along, 64f)),
                new VisProbe("ring_high", new Point(ring, along, 64f)),
            ];
        }
    }
}
