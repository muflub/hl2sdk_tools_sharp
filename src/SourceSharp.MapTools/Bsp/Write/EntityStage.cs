using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Bsp.Write;

/// <summary>
/// The entity-lump half of vbsp: model numbers, switched light styles, the
/// default <c>water_lod_control</c>, the world bounds keys and the lump text
/// Itself;).
/// </summary>
internal static class EntityStage
{
    /// <summary>
    /// <c>SetModelNumbers</c>: every brush entity
    /// but the world gets <c>"model" "*N"</c>, numbered in entity order;
    /// a <c>func_occluder</c> gets an empty model and does not advance N.
    /// </summary>
    /// <param name="map">The map.</param>
    internal static void SetModelNumbers(MapFile map)
    {
        ArgumentNullException.ThrowIfNull(map);

        int models = 1;
        for (int i = 1; i < map.Entities.Count; i++)
        {
            MapEntity e = map.Entities[i];
            if (e.BrushCount == 0)
            {
                continue;
            }

            string value;
            if (!IsFuncOccluder(e))
            {
                value = string.Create(CultureInfo.InvariantCulture, $"*{models}");
                models++;
            }
            else
            {
                value = string.Empty;
            }

            e.SetKeyValue("model", value);
        }
    }

    /// <summary>
    /// <c>SetLightStyles</c>: every named
    /// <c>light*</c> entity except <c>light_dynamic</c> gets a switchable style
    /// from 32 up, one per distinct targetname in first-seen order.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <exception cref="MapCompileException">More than 32 switched light names.</exception>
    internal static void SetLightStyles(MapFile map)
    {
        ArgumentNullException.ThrowIfNull(map);

        List<string> lightTargets = [];

        for (int i = 1; i < map.Entities.Count; i++)
        {
            MapEntity e = map.Entities[i];

            string t = e.ValueForKey("classname");

            // Q_strncasecmp(t, "light", 5)
            if (t.Length < 5 || !t.AsSpan(0, 5).Equals("light", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // This is not true for dynamic lights
            if (string.Equals(t, "light_dynamic", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            t = e.ValueForKey("targetname");
            if (t.Length == 0)
            {
                continue;
            }

            // find this targetname: strcmp, case-SENSITIVE
            int j = lightTargets.IndexOf(t);
            if (j < 0)
            {
                if (lightTargets.Count == WriteLimits.MaxSwitchedLights)
                {
                    throw new MapCompileException(WriteCodes.LimitExceeded,
                        $"Too many switched lights (error at light {t}), max = {WriteLimits.MaxSwitchedLights}");
                }

                j = lightTargets.Count;
                lightTargets.Add(t);
            }

            string value = (32 + j).ToString(CultureInfo.InvariantCulture);

            // the designer has set a default lightstyle as well as making the
            // light switchable: ValueForKey never returns NULL, so this is
            // always taken, and atoi decides.
            string currentStyle = e.ValueForKey("style");
            if (Atoi(currentStyle) != 0)
            {
                // save off the default style so the game code can make a
                // switchable copy of it
                e.SetKeyValue("defaultstyle", currentStyle);
            }

            e.SetKeyValue("style", value);
        }
    }

    /// <summary>
    /// <c>EnsurePresenceOfWaterLODControlEntity</c>.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="hasWater"><c>g_bHasWater</c>: whether any side's material is water.</param>
    /// <param name="diagnostics">Where the warning goes.</param>
    internal static void EnsurePresenceOfWaterLodControlEntity(
        MapFile map, bool hasWater, IList<CompileDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (!hasWater)
        {
            // Don't bother if there isn't any water in the map.
            return;
        }

        foreach (MapEntity e in map.Entities)
        {
            if (string.Equals(e.ValueForKey("classname"), "water_lod_control", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        diagnostics.Add(new CompileDiagnostic(
            WriteCodes.DefaultWaterLodControl,
            DiagnosticSeverity.Warning,
            "Water found with no water_lod_control entity, creating a default one."));

        MapEntity added = new()
        {
            FirstBrush = map.Brushes.Count,
            BrushCount = 0,
        };

        // SetKeyValue prepends, so the lump lists these in reverse.
        added.SetKeyValue("classname", "water_lod_control");
        added.SetKeyValue("cheapwaterstartdistance", "1000");
        added.SetKeyValue("cheapwaterenddistance", "2000");
        map.Entities.Add(added);
    }

    /// <summary>
    /// <c>ComputeBoundsNoSkybox</c>: the world's
    /// drawn bounds, excluding the 3D skybox areas, sky and nodraw faces,
    /// written onto <c>worldspawn</c> as <c>world_mins</c>/<c>world_maxs</c>.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="world">The world lumps, emitted or loaded.</param>
    /// <param name="dispBounds">
    /// The displacements' bounds, in dispinfo order (<c>ComputeDispInfoBounds</c>);
    /// empty until Phase 3f is wired.
    /// </param>
    internal static void ComputeBoundsNoSkybox(
        MapFile map,
        WorldLumps world,
        IReadOnlyList<(Vec3 Mins, Vec3 Maxs)> dispBounds)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(dispBounds);

        Vec3 mins = MapFile.ClearedMins;
        Vec3 maxs = MapFile.ClearedMaxs;

        int headNode = world.Models[0].HeadNode;
        AddNodeToBounds(world, headNode, ref mins, ref maxs);

        foreach ((Vec3 dMins, Vec3 dMaxs) in dispBounds)
        {
            if (IsBoxInsideWorld(world, headNode, dMins, dMaxs))
            {
                AddPointToBounds(dMins, ref mins, ref maxs);
                AddPointToBounds(dMaxs, ref mins, ref maxs);
            }
        }

        // Add the bounds to the worldspawn data (strcmp: case-sensitive).
        foreach (MapEntity e in map.Entities)
        {
            if (string.Equals(e.ValueForKey("classname"), "worldspawn", StringComparison.Ordinal))
            {
                e.SetKeyValue("world_mins", Ints(mins));
                e.SetKeyValue("world_maxs", Ints(maxs));
                break;
            }
        }
    }

    /// <summary>
    /// <c>UnparseEntities</c>: the ENTITIES lump.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <returns>The lump.</returns>
    internal static BspLumpData Unparse(MapFile map)
    {
        ArgumentNullException.ThrowIfNull(map);

        List<BspEntity> entities = new(map.Entities.Count);
        foreach (MapEntity e in map.Entities)
        {
            BspEntity copy = new();
            foreach (MapKeyValue pair in e.Pairs)
            {
                copy.Pairs.Add(new BspKeyValue(pair.Key, pair.Value));
            }

            entities.Add(copy);
        }

        return EntityLump.Write(entities);
    }

    /// <summary><c>IsFuncOccluder</c>: <c>strcmp</c>, case-sensitive.</summary>
    /// <param name="e">The entity.</param>
    /// <returns>Whether it is a <c>func_occluder</c>.</returns>
    internal static bool IsFuncOccluder(MapEntity e) =>
        string.Equals(e.ValueForKey("classname"), "func_occluder", StringComparison.Ordinal);

    // atoi: optional leading whitespace and sign, then digits; 0 on none.
    internal static int Atoi(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || (s[i] >= '\t' && s[i] <= '\r')))
        {
            i++;
        }

        bool negative = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
        {
            negative = s[i] == '-';
            i++;
        }

        long value = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
        {
            value = (value * 10) + (s[i] - '0');
            if (value > int.MaxValue)
            {
                break;
            }

            i++;
        }

        return (int)(negative ? -value : value);
    }

    // sprintf("%i %i %i", (int)mins[0], ...): truncation toward zero.
    private static string Ints(Vec3 v) => string.Create(
        CultureInfo.InvariantCulture, $"{(int)v.X} {(int)v.Y} {(int)v.Z}");

    /// <summary><c>AddNodeToBounds</c>.</summary>
    private static void AddNodeToBounds(
        WorldLumps state, int node, ref Vec3 mins, ref Vec3 maxs)
    {
        // not a leaf
        if (node >= 0)
        {
            DNode n = state.Nodes[node];
            AddNodeToBounds(state, n.Children[0], ref mins, ref maxs);
            AddNodeToBounds(state, n.Children[1], ref mins, ref maxs);
            return;
        }

        DLeaf leaf = state.Leafs[-1 - node];

        // Don't bother with solid leaves
        if ((leaf.Contents & (int)BrushContents.Solid) != 0)
        {
            return;
        }

        // Skip 3D skybox
        for (int i = state.SkyAreas.Count; --i >= 0;)
        {
            if (leaf.GetArea() == state.SkyAreas[i])
            {
                return;
            }
        }

        for (int i = 0; i < leaf.NumLeafFaces; ++i)
        {
            DFace face = state.Faces[state.LeafFaces[leaf.FirstLeafFace + i]];

            // Skip skyboxes + nodraw
            if ((state.TexInfoFlags(face.TexInfo) & (int)(SurfaceFlags.Sky | SurfaceFlags.NoDraw)) != 0)
            {
                continue;
            }

            for (int j = 0; j < face.NumEdges; ++j)
            {
                int edge = Math.Abs(state.SurfEdges[face.FirstEdge + j]);
                DEdge e = state.Edges[edge];
                AddPointToBounds(state.Vertexes[e.V[0]], ref mins, ref maxs);
                AddPointToBounds(state.Vertexes[e.V[1]], ref mins, ref maxs);
            }
        }
    }

    /// <summary><c>IsBoxInsideWorld</c>.</summary>
    private static bool IsBoxInsideWorld(WorldLumps state, int node, Vec3 mins, Vec3 maxs)
    {
        while (true)
        {
            // leaf
            if (node < 0)
            {
                DLeaf leaf = state.Leafs[-1 - node];

                // Don't bother with solid leaves
                if ((leaf.Contents & (int)BrushContents.Solid) != 0)
                {
                    return false;
                }

                // Skip 3D skybox
                for (int i = state.SkyAreas.Count; --i >= 0;)
                {
                    if (leaf.GetArea() == state.SkyAreas[i])
                    {
                        return false;
                    }
                }

                return true;
            }

            DNode n = state.Nodes[node];
            DPlane p = state.Planes[n.PlaneNum];
            int side = Tree.BrushBspTree.BoxOnPlaneSide(
                mins, maxs, new Plane(p.Normal, p.Dist), (PlaneType)p.Type);

            if (side == 1)
            {
                node = n.Children[0];
            }
            else if (side == 2)
            {
                node = n.Children[1];
            }
            else
            {
                if (IsBoxInsideWorld(state, n.Children[0], mins, maxs))
                {
                    return true;
                }

                node = n.Children[1];
            }
        }
    }

    private static void AddPointToBounds(Vec3 v, ref Vec3 mins, ref Vec3 maxs)
    {
        float minX = mins.X, minY = mins.Y, minZ = mins.Z;
        float maxX = maxs.X, maxY = maxs.Y, maxZ = maxs.Z;

        if (v.X < minX) { minX = v.X; }
        if (v.X > maxX) { maxX = v.X; }
        if (v.Y < minY) { minY = v.Y; }
        if (v.Y > maxY) { maxY = v.Y; }
        if (v.Z < minZ) { minZ = v.Z; }
        if (v.Z > maxZ) { maxZ = v.Z; }

        mins = new Vec3(minX, minY, minZ);
        maxs = new Vec3(maxX, maxY, maxZ);
    }
}

/// <summary>
/// The world lumps <c>ComputeBoundsNoSkybox</c> walks, whether just emitted or
/// loaded from an existing BSP for <c>-onlyents</c>.
/// </summary>
internal sealed record WorldLumps(
    IReadOnlyList<DModel> Models,
    IReadOnlyList<DNode> Nodes,
    IReadOnlyList<DLeaf> Leafs,
    IReadOnlyList<ushort> LeafFaces,
    IReadOnlyList<DFace> Faces,
    IReadOnlyList<int> SurfEdges,
    IReadOnlyList<DEdge> Edges,
    IReadOnlyList<Vec3> Vertexes,
    IReadOnlyList<DPlane> Planes,
    Func<int, int> TexInfoFlags,
    IReadOnlyList<int> SkyAreas)
{
    /// <summary>The lumps a compile has emitted, with its uncompacted texinfo table.</summary>
    internal static WorldLumps From(BspWriteState state, TexInfoTable texInfos) => new(
        state.Models, state.Nodes, state.Leafs, state.LeafFaces, state.DrawFaces, state.SurfEdges,
        state.Edges.Edges, state.Vertices.Vertexes, state.Planes, i => texInfos[i].Flags, state.SkyAreas);

    /// <summary>
    /// The lumps of a compiled BSP. <c>-onlyents</c> never recomputes
    /// <c>g_SkyAreas</c>, so it is empty and no area is skipped.
    /// </summary>
    internal static WorldLumps From(BspData bsp)
    {
        TexInfo[] texInfo = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).ToArray();
        return new(
            BspStructView.As<DModel>(bsp[BspLump.Models]).ToArray(),
            BspStructView.As<DNode>(bsp[BspLump.Nodes]).ToArray(),
            BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray(),
            BspStructView.As<ushort>(bsp[BspLump.LeafFaces]).ToArray(),
            BspStructView.As<DFace>(bsp[BspLump.Faces]).ToArray(),
            BspStructView.As<int>(bsp[BspLump.SurfEdges]).ToArray(),
            BspStructView.As<DEdge>(bsp[BspLump.Edges]).ToArray(),
            BspStructView.As<Vec3>(bsp[BspLump.Vertexes]).ToArray(),
            BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray(),
            i => texInfo[i].Flags,
            []);
    }
}
