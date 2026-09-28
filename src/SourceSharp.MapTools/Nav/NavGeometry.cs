//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Numerics;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapTools.Nav;

/// <summary>A dynamic obstacle as the room's compile found it: an entity the grid treats as open space and tags.</summary>
/// <param name="ClassName">Its classname.</param>
/// <param name="TargetName">Its <c>targetname</c> as written (possibly room-local), or null.</param>
/// <param name="HammerId">Its <c>hammerid</c>, or -1.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Brushes">Its solids: a brush entity's own brushes, or a prop's bounds as one box.</param>
public sealed record NavObstacleSource(
    string ClassName, string? TargetName, int HammerId, Nav3dObstacleKind Kind, IReadOnlyList<NavBrush> Brushes)
{
    /// <summary>The union of its brushes' bounds.</summary>
    public Box Bounds => new(
        new Vec3((float)Brushes.Min(b => b.MinX), (float)Brushes.Min(b => b.MinY), (float)Brushes.Min(b => b.MinZ)),
        new Vec3((float)Brushes.Max(b => b.MaxX), (float)Brushes.Max(b => b.MaxY), (float)Brushes.Max(b => b.MaxZ)));
}

/// <summary>
/// What the navigation is built from: the world's brushes (every contents:
/// solids, clips, water, ladders), the dynamic obstacles, and the ladder
/// volumes entities describe.
/// </summary>
/// <remarks>
/// <para>
/// <b>World brushes only are solid.</b> A compiled BSP holds the brushes of
/// every model: the world's, and each brush entity's. Only the world's are
/// static: a <c>func_door</c>'s brushes move, a <c>func_breakable</c>'s go
/// away, a trigger's never block. So the brushes a submodel's tree reaches
/// are set aside; those of an entity of a dynamic class become that
/// obstacle's solids, the rest (triggers, illusionaries, physics clips) are
/// dropped as nothing an agent walks into.
/// </para>
/// <para>
/// <b>Props</b> (<c>prop_door_rotating</c>, <c>prop_physics</c>,
/// <c>prop_dynamic</c> and their variants) are point entities: they have no
/// brush, so they are never solid in the grid. Their model's movement hull,
/// turned by the entity's angles, is their obstacle box; a model the game's
/// content does not have is reported and left out.
/// </para>
/// <para>
/// <b>Ladders</b> come three ways, all flagging the voxels they overlap: a
/// <c>CONTENTS_LADDER</c> brush in the world, an <c>info_ladder</c>'s box
/// (<c>mins</c>/<c>maxs</c>, or the <c>mins.x</c> ... keys), and a
/// <c>func_useableladder</c>'s climb: the box spanning its <c>point0</c> and
/// <c>point1</c>, widened by 16 units, a player's half-width, since those are
/// where the player's origin runs while it climbs.
/// </para>
/// </remarks>
public sealed class NavGeometry
{
    /// <summary>A <c>func_useableladder</c>'s climb is widened this much on each side: a player's half-width.</summary>
    public const float LadderHalfWidth = 16f;

    /// <summary>The world's brushes, every contents.</summary>
    public required IReadOnlyList<NavBrush> Brushes { get; init; }

    /// <summary>The dynamic obstacles, in entity order.</summary>
    public IReadOnlyList<NavObstacleSource> Obstacles { get; init; } = [];

    /// <summary>The ladder volumes entities describe.</summary>
    public IReadOnlyList<Box> Ladders { get; init; } = [];

    /// <summary>What could not be read, one sentence each.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The same geometry with more world brushes: the solids a room assumes beyond its cell.</summary>
    /// <param name="extra">The brushes to add.</param>
    /// <returns>The geometry.</returns>
    public NavGeometry With(IEnumerable<NavBrush> extra) => new()
    {
        Brushes = [.. Brushes, .. extra],
        Obstacles = Obstacles,
        Ladders = Ladders,
        Warnings = Warnings,
    };

    /// <summary>The obstacle kind a classname is, or null when it is not a dynamic obstacle.</summary>
    /// <param name="className">The classname.</param>
    /// <returns>The kind, or null.</returns>
    public static Nav3dObstacleKind? KindOf(string className) => className.ToLowerInvariant() switch
    {
        "func_door" or "func_door_rotating" or "prop_door_rotating" => Nav3dObstacleKind.Door,
        "func_movelinear" or "func_train" or "func_tracktrain" or "func_rotating" or "func_plat" or "func_platrot" => Nav3dObstacleKind.Mover,
        "func_brush" or "func_wall_toggle" => Nav3dObstacleKind.Toggle,
        "func_breakable" or "func_breakable_surf" => Nav3dObstacleKind.Breakable,
        "func_physbox" or "func_physbox_multiplayer" or "prop_physics" or "prop_physics_override" or "prop_physics_multiplayer"
            or "prop_physics_respawnable" => Nav3dObstacleKind.Physics,
        "prop_dynamic" or "prop_dynamic_override" => Nav3dObstacleKind.Prop,
        _ => null,
    };

    /// <summary>The geometry of a compiled BSP.</summary>
    /// <param name="bsp">The BSP.</param>
    /// <param name="modelBounds">A model's movement hull by its path as an entity names it, or null when unknown; null to leave props out.</param>
    /// <returns>The geometry.</returns>
    public static NavGeometry FromBsp(BspData bsp, Func<string, (Vec3 Mins, Vec3 Maxs)?>? modelBounds = null)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        List<(int Index, NavBrush Brush)> all = NavBrush.FromBspIndexed(bsp);
        Dictionary<int, NavBrush> byIndex = all.ToDictionary(b => b.Index, b => b.Brush);
        List<int>[] modelBrushes = ModelBrushes(bsp);
        HashSet<int> entityBrushes = [.. modelBrushes.Skip(1).SelectMany(m => m)];

        List<NavObstacleSource> obstacles = [];
        List<Box> ladders = [];
        List<string> warnings = [];
        foreach (BspEntity entity in EntityLump.Parse(bsp[BspLump.Entities]))
        {
            string className = entity.ClassName;
            string lower = className.ToLowerInvariant();
            if (lower == "info_ladder" && LadderBox(entity) is { } box)
            {
                ladders.Add(box);
                continue;
            }

            if (lower == "func_useableladder" && UseableLadder(entity) is { } climb)
            {
                ladders.Add(climb);
                continue;
            }

            if (KindOf(className) is not { } kind)
            {
                continue;
            }

            string? targetName = entity.Get("targetname") is { Length: > 0 } t ? t : null;
            int hammerId = int.TryParse(entity.Get("hammerid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : -1;
            string? model = entity.Get("model");
            List<NavBrush> solids = [];
            if (model is { Length: > 1 } && model[0] == '*')
            {
                if (int.TryParse(model.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index > 0
                    && index < modelBrushes.Length)
                {
                    foreach (int b in modelBrushes[index])
                    {
                        if (byIndex.TryGetValue(b, out NavBrush? brush))
                        {
                            solids.Add(brush);
                        }
                    }
                }
            }
            else if (model is { Length: > 0 })
            {
                if (modelBounds?.Invoke(model) is { } hull && PropBox(entity, hull) is { } propBox)
                {
                    solids.Add(NavBrush.Box(propBox.Mins, propBox.Maxs, 1));
                }
                else
                {
                    warnings.Add($"{className} {(targetName ?? (hammerId >= 0 ? hammerId.ToString(CultureInfo.InvariantCulture) : "(unnamed)"))}: model \"{model}\" has no hull the content gives, so it is left out of the navigation's obstacles.");
                }
            }

            if (solids.Count > 0)
            {
                obstacles.Add(new NavObstacleSource(className, targetName, hammerId, kind, solids));
            }
        }

        return new NavGeometry
        {
            Brushes = [.. all.Where(b => !entityBrushes.Contains(b.Index)).Select(b => b.Brush)],
            Obstacles = obstacles,
            Ladders = ladders,
            Warnings = warnings,
        };
    }

    /// <summary>Each model's brush indices, found by walking its tree to its leaves' brush lists.</summary>
    private static List<int>[] ModelBrushes(BspData bsp)
    {
        ReadOnlySpan<DModel> models = BspStructView.As<DModel>(bsp[BspLump.Models]);
        ReadOnlySpan<DNode> nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]);
        ReadOnlySpan<DLeaf> leafs = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]);
        ReadOnlySpan<ushort> leafBrushes = BspStructView.As<ushort>(bsp[BspLump.LeafBrushes]);
        List<int>[] result = new List<int>[models.Length];
        Stack<int> pending = new();
        for (int m = 0; m < models.Length; m++)
        {
            SortedSet<int> brushes = [];
            pending.Clear();
            pending.Push(models[m].HeadNode);
            while (pending.Count > 0)
            {
                int node = pending.Pop();
                if (node < 0)
                {
                    int leaf = -(node + 1);
                    if (leaf < leafs.Length)
                    {
                        for (int b = 0; b < leafs[leaf].NumLeafBrushes; b++)
                        {
                            int at = leafs[leaf].FirstLeafBrush + b;
                            if (at < leafBrushes.Length)
                            {
                                brushes.Add(leafBrushes[at]);
                            }
                        }
                    }

                    continue;
                }

                if (node < nodes.Length)
                {
                    pending.Push(nodes[node].Children[1]);
                    pending.Push(nodes[node].Children[0]);
                }
            }

            result[m] = [.. brushes];
        }

        return result;
    }

    /// <summary>An <c>info_ladder</c>'s box: <c>mins</c> and <c>maxs</c>, or their <c>.x</c>, <c>.y</c>, <c>.z</c> keys.</summary>
    internal static Box? LadderBox(BspEntity entity)
    {
        Vec3? Corner(string name)
        {
            if (VmfValue.TryParseVector3(entity.Get(name), out Vec3 v))
            {
                return v;
            }

            float[] parts = new float[3];
            string[] axes = ["x", "y", "z"];
            for (int i = 0; i < 3; i++)
            {
                if (!float.TryParse(entity.Get($"{name}.{axes[i]}"), NumberStyles.Float, CultureInfo.InvariantCulture, out parts[i]))
                {
                    return null;
                }
            }

            return new Vec3(parts[0], parts[1], parts[2]);
        }

        if (Corner("mins") is not { } a || Corner("maxs") is not { } b)
        {
            return null;
        }

        return new Box(Min(a, b), Max(a, b));
    }

    /// <summary>A <c>func_useableladder</c>'s climb: the box spanning its two points, widened by a player's half-width.</summary>
    internal static Box? UseableLadder(BspEntity entity)
    {
        if (!VmfValue.TryParseVector3(entity.Get("point0"), out Vec3 a) || !VmfValue.TryParseVector3(entity.Get("point1"), out Vec3 b))
        {
            return null;
        }

        Vec3 grow = new(LadderHalfWidth, LadderHalfWidth, 0);
        return new Box(Min(a, b) - grow, Max(a, b) + grow);
    }

    /// <summary>A prop's obstacle box: its model's hull turned by its angles and moved to its origin; the box of the turned corners.</summary>
    internal static Box? PropBox(BspEntity entity, (Vec3 Mins, Vec3 Maxs) hull)
    {
        if (!VmfValue.TryParseVector3(entity.Get("origin"), out Vec3 origin))
        {
            return null;
        }

        Vec3 angles = VmfValue.TryParseVector3(entity.Get("angles"), out Vec3 a) ? a : default;
        (double sp, double cp) = SinCos(angles.X);
        (double sy, double cy) = SinCos(angles.Y);
        (double sr, double cr) = SinCos(angles.Z);

        // Source's angle order: yaw about z, pitch about y, roll about x.
        double[,] m =
        {
            { cp * cy, (sr * sp * cy) - (cr * sy), (cr * sp * cy) + (sr * sy) },
            { cp * sy, (sr * sp * sy) + (cr * cy), (cr * sp * sy) - (sr * cy) },
            { -sp, sr * cp, cr * cp },
        };
        double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue];
        double[] hi = [double.MinValue, double.MinValue, double.MinValue];
        for (int c = 0; c < 8; c++)
        {
            double px = (c & 1) == 0 ? hull.Mins.X : hull.Maxs.X;
            double py = (c & 2) == 0 ? hull.Mins.Y : hull.Maxs.Y;
            double pz = (c & 4) == 0 ? hull.Mins.Z : hull.Maxs.Z;
            for (int k = 0; k < 3; k++)
            {
                double v = (m[k, 0] * px) + (m[k, 1] * py) + (m[k, 2] * pz);
                lo[k] = Math.Min(lo[k], v);
                hi[k] = Math.Max(hi[k], v);
            }
        }

        Vec3 mins = new((float)(origin.X + lo[0]), (float)(origin.Y + lo[1]), (float)(origin.Z + lo[2]));
        Vec3 maxs = new((float)(origin.X + hi[0]), (float)(origin.Y + hi[1]), (float)(origin.Z + hi[2]));
        return mins.X < maxs.X && mins.Y < maxs.Y && mins.Z < maxs.Z ? new Box(mins, maxs) : null;
    }

    private static Vec3 Min(Vec3 a, Vec3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));

    private static Vec3 Max(Vec3 a, Vec3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    /// <summary>A sine and cosine of degrees, exact at quarter turns so a prop turned by one keeps an exact box.</summary>
    private static (double Sin, double Cos) SinCos(double degrees)
    {
        double turns = degrees / 90.0;
        if (turns == Math.Floor(turns))
        {
            int q = (int)(((long)turns % 4 + 4) % 4);
            return q switch { 0 => (0, 1), 1 => (1, 0), 2 => (0, -1), _ => (-1, 0) };
        }

        double radians = degrees * (Math.PI / 180.0);
        return (DetMath.Sin(radians), DetMath.Cos(radians));
    }
}
