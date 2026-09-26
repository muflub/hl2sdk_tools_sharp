//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapGen.Catalog;

/// <summary>
/// The pieces every catalogue entry is built from: a sealed shell, walls with
/// doorways in them, pillars, wedges, and the tool textures.
///
/// <para>
/// Sealing is the thing this file exists to get right. A map whose shell has a
/// gap in it LEAKS, and a leaked map produces no `.prt`, so every vvis entry in
/// the catalogue would fail for the same reason and none of them would be
/// testing what it says it tests. So the shell is built as six slabs that meet
/// exactly and never overlap, and the arithmetic is written out rather than
/// nudged until it looked right.
/// </para>
/// </summary>
public static class RoomKit
{
    /// <summary>How thick a wall, floor or ceiling slab is.</summary>
    public const float WallThickness = 16f;

    // ------------------------------------------------------------------
    // Tool textures.
    //
    // Spelled the way the corpus map's own tool textures are spelled — upper
    // case, forward slash — because that map compiles with stock vbsp today and
    // a second spelling in the same tree is a thing to get wrong rather than a choice.
    // vbsp itself matches on the material's %compile* flags, not on the name,
    // so the name only has to resolve to the right .vmt.
    // ------------------------------------------------------------------

    /// <summary>Invisible, and contributes no face.</summary>
    public const string NoDraw = "TOOLS/TOOLSNODRAW";

    /// <summary>The face of a hint brush that forces a visibility split.</summary>
    public const string Hint = "TOOLS/TOOLSHINT";

    /// <summary>The other faces of a hint brush: ignored entirely.</summary>
    public const string Skip = "TOOLS/TOOLSSKIP";

    /// <summary>Blocks everything.</summary>
    public const string Clip = "TOOLS/TOOLSCLIP";

    /// <summary>Blocks players only.</summary>
    public const string PlayerClip = "TOOLS/TOOLSPLAYERCLIP";

    /// <summary>Blocks NPCs only.</summary>
    public const string NpcClip = "TOOLS/TOOLSNPCCLIP";

    /// <summary>The volume of a trigger brush entity.</summary>
    public const string Trigger = "TOOLS/TOOLSTRIGGER";

    /// <summary>Solid but not drawn.</summary>
    public const string Invisible = "TOOLS/TOOLSINVISIBLE";

    /// <summary>A climbable surface with no appearance.</summary>
    public const string Ladder = "TOOLS/TOOLSINVISIBLELADDER";

    /// <summary>Blocks NPC line of sight and nothing else.</summary>
    public const string BlockLos = "TOOLS/TOOLSBLOCK_LOS";

    /// <summary>Marks a brush entity's pivot.</summary>
    public const string Origin = "TOOLS/TOOLSORIGIN";

    /// <summary>The surface of a func_areaportal.</summary>
    public const string AreaPortal = "TOOLS/TOOLSAREAPORTAL";

    /// <summary>The sky.</summary>
    public const string Sky = "TOOLS/TOOLSSKYBOX";

    /// <summary>
    /// Water, cheap.
    ///
    /// <para>
    /// The same material the corpus map settled on, and for the same reason:
    /// it carries %compilewater and $forcecheap 1, so it needs no reflection
    /// pass and no cubemap to be a legitimate water surface.
    /// </para>
    /// </summary>
    public const string Water = "nature/water_canals_cheap001";

    /// <summary>The shell's walls.</summary>
    public const string WallMaterial = Site.Hall;

    /// <summary>The shell's floor.</summary>
    public const string FloorMaterial = Site.Floor;

    /// <summary>The shell's ceiling.</summary>
    public const string CeilingMaterial = Site.Overhead;

    /// <summary>What a pillar, a divider or a detail block is made of.</summary>
    public const string BlockMaterial = Site.Wall;

    /// <summary>
    /// Which side of a <see cref="VmfMap.Box"/> an axis sign names.
    ///
    /// <para>
    /// MIRRORS Vmf.cs's private `IndexOf` and its `Box` winding order
    /// (+Z, -Z, +X, -X, +Y, -Y). Duplicated rather than shared because Vmf.cs is
    /// another lane's file this lane may not edit — and because a copied table
    /// is a thing to be wrong about, `RoomKitTests` checks each index against
    /// the geometry the box actually produced rather than against the comment.
    /// </para>
    /// </summary>
    /// <param name="face">One of "+z", "-z", "+x", "-x", "+y", "-y".</param>
    public static int FaceIndex(string face) => face switch
    {
        "+z" => 0,
        "-z" => 1,
        "+x" => 2,
        "-x" => 3,
        "+y" => 4,
        "-y" => 5,
        _ => throw new ArgumentException($"unknown face '{face}'", nameof(face)),
    };

    /// <summary>A box with every face one material except the one named.</summary>
    /// <param name="bounds">The box.</param>
    /// <param name="material">What the other five faces are.</param>
    /// <param name="face">Which face differs.</param>
    /// <param name="faceMaterial">What that face is.</param>
    public static VmfSolid BoxWithFace(Bounds bounds, string material, string face, string faceMaterial)
    {
        VmfSolid solid = VmfMap.Box(bounds.Mins, bounds.Maxs, material);
        solid.Sides[FaceIndex(face)].Material = faceMaterial;

        return solid;
    }

    /// <summary>
    /// Seals an interior volume with six slabs.
    ///
    /// <para>
    /// The slabs meet and do not overlap: the floor and the ceiling take the
    /// full outer footprint, the -X and +X walls take the full outer Y span
    /// between them, and the -Y and +Y walls fill what is left. Overlapping
    /// them would still seal, but it would put an overlapping-CSG case into
    /// every entry in the catalogue, which is a feature the catalogue has an
    /// entry of its own for.
    /// </para>
    /// </summary>
    /// <param name="map">Where the solids go.</param>
    /// <param name="interior">The volume to enclose — the empty space, not the shell.</param>
    /// <param name="thickness">How thick each slab is.</param>
    public static void Shell(VmfMap map, Bounds interior, float thickness = WallThickness)
    {
        ArgumentNullException.ThrowIfNull(map);

        Point lo = interior.Mins, hi = interior.Maxs;
        float t = thickness;

        void Slab(Point mins, Point maxs, string material, string? top = null)
            => map.WorldSolids.Add(VmfMap.Box(mins, maxs, material, top));

        // Floor and ceiling: the full outer footprint.
        Slab(new(lo.X - t, lo.Y - t, lo.Z - t), new(hi.X + t, hi.Y + t, lo.Z),
             FloorMaterial, FloorMaterial);
        Slab(new(lo.X - t, lo.Y - t, hi.Z), new(hi.X + t, hi.Y + t, hi.Z + t),
             CeilingMaterial, CeilingMaterial);

        // -X and +X: the full outer Y span, between floor and ceiling.
        Slab(new(lo.X - t, lo.Y - t, lo.Z), new(lo.X, hi.Y + t, hi.Z), WallMaterial);
        Slab(new(hi.X, lo.Y - t, lo.Z), new(hi.X + t, hi.Y + t, hi.Z), WallMaterial);

        // -Y and +Y: what is left, between the two X walls.
        Slab(new(lo.X, lo.Y - t, lo.Z), new(hi.X, lo.Y, hi.Z), WallMaterial);
        Slab(new(lo.X, hi.Y, lo.Z), new(hi.X, hi.Y + t, hi.Z), WallMaterial);
    }

    /// <summary>How many solids <see cref="Shell"/> adds.</summary>
    public const int ShellBrushes = 6;

    /// <summary>
    /// A wall across an interior at a given X, with a doorway through it.
    ///
    /// <para>
    /// Three solids when the opening is shorter than the room — left of the
    /// doorway, right of it, and a header over it — and two when it is not.
    /// The count is returned because an entry declares its brush count and this
    /// is where the number comes from.
    /// </para>
    /// </summary>
    /// <param name="map">Where the solids go.</param>
    /// <param name="interior">The volume the wall crosses.</param>
    /// <param name="x">Where the wall's low face sits.</param>
    /// <param name="thickness">How thick the wall is.</param>
    /// <param name="doorCentreY">The middle of the doorway, in Y.</param>
    /// <param name="doorWidth">How wide the doorway is.</param>
    /// <param name="doorHeight">How tall it is, measured from the floor.</param>
    /// <returns>How many solids were added.</returns>
    public static int DividerWithDoorway(
        VmfMap map, Bounds interior, float x, float thickness,
        float doorCentreY, float doorWidth, float doorHeight)
    {
        ArgumentNullException.ThrowIfNull(map);

        Point lo = interior.Mins, hi = interior.Maxs;
        float y0 = doorCentreY - (doorWidth * 0.5f);
        float y1 = doorCentreY + (doorWidth * 0.5f);

        if (y0 <= lo.Y || y1 >= hi.Y)
        {
            throw new ArgumentOutOfRangeException(
                nameof(doorWidth),
                "the doorway has to leave wall on both sides, or the wall does not divide anything");
        }

        int added = 0;

        void Piece(Point mins, Point maxs)
        {
            map.WorldSolids.Add(VmfMap.Box(mins, maxs, BlockMaterial));
            added++;
        }

        Piece(new(x, lo.Y, lo.Z), new(x + thickness, y0, hi.Z));
        Piece(new(x, y1, lo.Z), new(x + thickness, hi.Y, hi.Z));

        if (lo.Z + doorHeight < hi.Z)
            Piece(new(x, y0, lo.Z + doorHeight), new(x + thickness, y1, hi.Z));

        return added;
    }

    /// <summary>A pillar from floor to ceiling.</summary>
    /// <param name="map">Where the solid goes.</param>
    /// <param name="interior">The room the pillar stands in.</param>
    /// <param name="centreX">Where it stands, in X.</param>
    /// <param name="centreY">Where it stands, in Y.</param>
    /// <param name="half">Half its width, on both horizontal axes.</param>
    public static void Pillar(VmfMap map, Bounds interior, float centreX, float centreY, float half)
    {
        ArgumentNullException.ThrowIfNull(map);

        map.WorldSolids.Add(VmfMap.Box(
            new(centreX - half, centreY - half, interior.Mins.Z),
            new(centreX + half, centreY + half, interior.Maxs.Z),
            BlockMaterial));
    }

    /// <summary>
    /// A right triangular prism: the catalogue's non-axial brush.
    ///
    /// <para>
    /// Five faces, and the winding of each is the part worth being careful
    /// about. vbsp reads a face's normal as (p0 - p1) x (p2 - p1) and takes it
    /// as pointing OUT of the solid, so a face wound the other way turns the
    /// brush inside out — which compiles, and produces geometry you fall
    /// through. Each winding below was derived from `Vmf.cs`'s `Box`, which is
    /// known good, rather than guessed.
    /// </para>
    ///
    /// <para>
    /// The slope runs from the low X, low Z edge up to the high X, high Z edge,
    /// so the right angle is at high X, low Z.
    /// </para>
    /// </summary>
    /// <param name="bounds">The box the prism is cut from.</param>
    /// <param name="material">What every face is made of.</param>
    public static VmfSolid Wedge(Bounds bounds, string material)
    {
        Point lo = bounds.Mins, hi = bounds.Maxs;
        var solid = new VmfSolid();

        const string alongX = "[1 0 0 0] 0.25";
        const string alongY = "[0 -1 0 0] 0.25";
        const string downZ = "[0 0 -1 0] 0.25";
        const string plusY = "[0 1 0 0] 0.25";

        void Side(Point p0, Point p1, Point p2, string u, string v)
            => solid.Sides.Add(new VmfSide
            {
                P0 = p0, P1 = p1, P2 = p2, Material = material, UAxis = u, VAxis = v,
            });

        // -Z, the base.
        Side(new(lo.X, lo.Y, lo.Z), new(hi.X, lo.Y, lo.Z), new(hi.X, hi.Y, lo.Z),
             alongX, alongY);

        // +X, the vertical face at the right angle.
        Side(new(hi.X, hi.Y, hi.Z), new(hi.X, hi.Y, lo.Z), new(hi.X, lo.Y, lo.Z),
             plusY, downZ);

        // -Y and +Y, the two triangles.
        Side(new(hi.X, lo.Y, lo.Z), new(lo.X, lo.Y, lo.Z), new(hi.X, lo.Y, hi.Z),
             alongX, downZ);
        Side(new(lo.X, hi.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(hi.X, hi.Y, hi.Z),
             alongX, downZ);

        // The slope. Its normal has no Y component, so V along Y is guaranteed
        // not to be parallel to it whatever the box's proportions are.
        Side(new(lo.X, lo.Y, lo.Z), new(lo.X, hi.Y, lo.Z), new(hi.X, hi.Y, hi.Z),
             plusY, downZ);

        return solid;
    }

    /// <summary>
    /// A body of water filling the bottom of a room.
    ///
    /// <para>
    /// The water material on the top face and nodraw on the other five: the
    /// surface is the only side of a water brush the compiler or the engine has
    /// any use for, and texturing the walls of the volume as water as well makes
    /// vbsp emit five more water surfaces nobody can see.
    /// </para>
    /// </summary>
    /// <param name="map">Where the solid goes.</param>
    /// <param name="water">The volume the water fills. Its top face is the surface.</param>
    public static void Pool(VmfMap map, Bounds water)
    {
        ArgumentNullException.ThrowIfNull(map);

        map.WorldSolids.Add(VmfMap.Box(water.Mins, water.Maxs, NoDraw, Water));
    }

    /// <summary>A world brush: a box of one material, and nothing else.</summary>
    /// <param name="map">Where the solid goes.</param>
    /// <param name="bounds">The box.</param>
    /// <param name="material">What every face is.</param>
    public static void Block(VmfMap map, Bounds bounds, string material)
    {
        ArgumentNullException.ThrowIfNull(map);

        map.WorldSolids.Add(VmfMap.Box(bounds.Mins, bounds.Maxs, material));
    }

    /// <summary>A brush entity, from solids the caller has already built.</summary>
    /// <param name="map">Where the entity goes.</param>
    /// <param name="className">Its classname.</param>
    /// <param name="solids">Its geometry.</param>
    /// <param name="keyValues">Keys, in pairs.</param>
    public static VmfEntity BrushEntity(
        VmfMap map, string className, IEnumerable<VmfSolid> solids, params string[] keyValues)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(solids);
        ArgumentNullException.ThrowIfNull(keyValues);

        if (keyValues.Length % 2 != 0)
            throw new ArgumentException("keyvalues come in pairs", nameof(keyValues));

        var entity = new VmfEntity { ClassName = className };
        entity.Solids.AddRange(solids);

        for (int i = 0; i < keyValues.Length; i += 2)
            entity.Set(keyValues[i], keyValues[i + 1]);

        map.Entities.Add(entity);

        return entity;
    }

    /// <summary>A point entity with an origin and nothing else declared.</summary>
    /// <param name="map">Where the entity goes.</param>
    /// <param name="className">Its classname.</param>
    /// <param name="at">Where it is.</param>
    /// <param name="keyValues">Extra keys, in pairs.</param>
    public static VmfEntity PointEntity(VmfMap map, string className, Point at, params string[] keyValues)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(keyValues);

        if (keyValues.Length % 2 != 0)
            throw new ArgumentException("keyvalues come in pairs", nameof(keyValues));

        var entity = new VmfEntity { ClassName = className };
        entity.Set("origin", at.ToString());

        for (int i = 0; i < keyValues.Length; i += 2)
            entity.Set(keyValues[i], keyValues[i + 1]);

        map.Entities.Add(entity);

        return entity;
    }

    /// <summary>
    /// The entity every sealed map needs: something inside the shell for vbsp to
    /// flood outwards from.
    ///
    /// <para>
    /// Not a nicety. vbsp decides what is inside the map by flooding from every
    /// entity's origin; a sealed map with no entity in it has nothing to flood
    /// from, reports "no entities in map", and produces nothing to test. Which
    /// is also exactly how the leak entry is built — by putting one OUTSIDE.
    /// </para>
    /// </summary>
    /// <param name="map">Where the entity goes.</param>
    /// <param name="at">Where the player starts.</param>
    public static VmfEntity PlayerStart(VmfMap map, Point at)
        => PointEntity(map, "info_player_start", at, "angles", "0 0 0");

    /// <summary>A plain light.</summary>
    /// <param name="map">Where the entity goes.</param>
    /// <param name="at">Where the light is.</param>
    /// <param name="brightness">The `_light` keyvalue: R G B and brightness.</param>
    public static VmfEntity Light(VmfMap map, Point at, string brightness = "255 255 255 200")
        => PointEntity(map, "light", at, "_light", brightness);

    /// <summary>
    /// The worldspawn keys every generated map carries.
    ///
    /// <para>
    /// A fixed set, deliberately small: every key here is one more thing that
    /// has to be the same in two runs for the byte-stability fact to hold, and
    /// one more thing a compiler could read. `skyname` is included because vbsp
    /// looks for the sky material by that name whether or not the map has any
    /// sky in it.
    /// </para>
    /// </summary>
    /// <param name="map">The map to write them on.</param>
    public static void Worldspawn(VmfMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        map.World.Add(new("skyname", "sky_day01_01"));
        map.World.Add(new("maxpropscreenwidth", "-1"));
    }
}
