namespace SourceSharp.MapGen.Catalog;

// The content-catalogue entries: static props, detail props, overlays, water
// overlays, env_cubemap and the default cubemaps. Kept in
// their own file so other content-catalogue additions do not collide in
// TestMapCatalog.cs; the only edit there is the two spreads in `All`.
//
// Every entry is compiled with stock vbsp against SurfaceContentFixture's
// content (a detail.vbsp and a handful of VMTs) mounted ahead of the game's own, and
// its expectations were read off stock's output — see SurfaceContentFixture.
public static partial class TestMapCatalog
{
    /// <summary>The prop every "one ordinary prop" case uses: static-only, no prop_data.</summary>
    public const string PlainPropModel = "models/props_c17/concrete_barrier001a.mdl";

    /// <summary>A model compiled with <c>$staticprop</c> but carrying <c>prop_data</c>
    /// and no <c>allowstatic</c>: vbsp deletes it.</summary>
    public const string DynamicOnlyPropModel = "models/props_c17/oildrum001.mdl";

    /// <summary>A model that is not there at all.</summary>
    public const string MissingPropModel = "models/p3g/this_model_does_not_exist.mdl";

    /// <summary>A specular world material: <c>$envmap env_cubemap</c>, no dependents.</summary>
    public const string SpecularMaterial = "metal/metalwall058a";

    /// <summary>The overlay material the overlay entries use.</summary>
    public const string OverlayMaterial = "overlays/tideline01a";

    /// <summary>The water overlay material.</summary>
    public const string WaterOverlayMaterial = "overlays/tideline01b";

    private static IEnumerable<CatalogEntry> SurfaceContent()
    {
        yield return new CatalogEntry
        {
            Name = "l1_static_prop",
            Level = MapLevel.L1,
            Features = [MapFeature.StaticProp],
            Summary = "one prop_static on the floor, straddling the block grid's x = 0 boundary",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomKit.ShellBrushes),
                PointEntities = CountRange.Exactly(3),
                Classnames = ["prop_static"],
                Leaks = false,
                BrushEntities = CountRange.Exactly(0),
                Areas = CountRange.Exactly(2),
                Clusters = CountRange.Exactly(4),
                Portals = CountRange.Exactly(4),
            },
            Generator = _ =>
            {
                VmfMap map = SealedRoom();
                StaticProp(map, PlainPropModel, new Point(0f, 32f, 0f), "0 30 0");
                return map;
            },
        };

        yield return new CatalogEntry
        {
            Name = "l1_static_prop_solid_types",
            Level = MapLevel.L1,
            Features = [MapFeature.StaticPropSolidTypes],
            Summary = "props of each solid type (0 none, 2 bbox, 6 vphysics) and every flag and fade key vbsp reads",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomKit.ShellBrushes),
                PointEntities = CountRange.Exactly(7),
                Classnames = ["prop_static", "info_lighting"],
                Leaks = false,
                BrushEntities = CountRange.Exactly(0),
                Areas = CountRange.Exactly(2),
                Clusters = CountRange.Exactly(4),
                Portals = CountRange.Exactly(4),
            },
            Generator = _ =>
            {
                VmfMap map = SealedRoom();

                // solid 0, and the fade pair with a NEGATIVE min, which vbsp
                // replaces with the max.
                StaticProp(map, PlainPropModel, new Point(-128f, -128f, 0f), "0 0 0",
                           "solid", "0", "fademindist", "-1", "fademaxdist", "400");

                // solid 2, fades, fadescale, and all five boolean flags.
                StaticProp(map, "models/props_c17/fence01a.mdl", new Point(128f, -128f, 0f), "0 90 0",
                           "solid", "2", "fademindist", "256", "fademaxdist", "512", "fadescale", "0.5",
                           "disableshadows", "1", "screenspacefade", "1", "ignorenormals", "1",
                           "disablevertexlighting", "1", "disableselfshadowing", "1", "skin", "1");

                // solid 6, per-texel lightmap resolution, DX levels, and a
                // lighting origin that resolves to an info_lighting.
                StaticProp(map, "models/props_foliage/shrub_01a.mdl", new Point(-128f, 128f, 0f), "0 45 0",
                           "solid", "6", "generatelightmaps", "1", "lightmapresolutionx", "32",
                           "lightmapresolutiony", "16", "mindxlevel", "80", "maxdxlevel", "95",
                           "lightingorigin", "p3g_lighting");
                RoomKit.PointEntity(map, "info_lighting", new Point(-96f, 96f, 64f),
                                    "targetname", "p3g_lighting");

                // A lighting origin that names nothing: the flag stays clear.
                // Tilted, so pitch and roll reach the lump.
                StaticProp(map, "models/props_c17/lamppost03a_off.mdl", new Point(128f, 128f, 0f), "5 200 3",
                           "solid", "6", "lightingorigin", "p3g_nobody");

                return map;
            },
        };

        yield return new CatalogEntry
        {
            Name = "l1_missing_prop_model",
            Level = MapLevel.L1,
            Features = [MapFeature.MissingPropModel],
            Summary = "a prop whose model does not exist and one whose model must be dynamic, beside one that is fine",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomKit.ShellBrushes),
                PointEntities = CountRange.Exactly(5),
                Classnames = ["prop_static"],
                Leaks = false,
                BrushEntities = CountRange.Exactly(0),
                Areas = CountRange.Exactly(2),
                Clusters = CountRange.Exactly(4),
                Portals = CountRange.Exactly(4),
            },
            Generator = _ =>
            {
                VmfMap map = SealedRoom();
                StaticProp(map, MissingPropModel, new Point(-128f, 0f, 0f), "0 0 0");
                StaticProp(map, DynamicOnlyPropModel, new Point(0f, -128f, 0f), "0 0 0");
                StaticProp(map, PlainPropModel, new Point(128f, 128f, 0f), "0 0 0");
                return map;
            },
        };

        yield return new CatalogEntry
        {
            Name = "l1_detail_props",
            Level = MapLevel.L1,
            Features = [MapFeature.DetailProps],
            Summary = "a floor and a 45-degree ramp whose material names a detail.vbsp type: sprites, shapes and models, placed by stock's rand()",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomKit.ShellBrushes + 1),
                PointEntities = CountRange.Exactly(4),
                Materials = [SurfaceContentFixture.DetailFloorMaterial],
                Classnames = ["prop_detail", "prop_detail_sprite"],
                Leaks = false,
                BrushEntities = CountRange.Exactly(0),
                Areas = CountRange.Exactly(2),
                Clusters = CountRange.Exactly(7),
                Portals = CountRange.Exactly(12),
            },
            Generator = _ =>
            {
                VmfMap map = SealedRoom();
                map.World.Add(new("detailvbsp", SurfaceContentFixture.DetailVbspName));
                map.WorldSolids[0].Sides[0].Material = SurfaceContentFixture.DetailFloorMaterial;
                map.WorldSolids.Add(RoomKit.Wedge(
                    new Bounds(new Point(64f, -192f, 0f), new Point(192f, -64f, 128f)),
                    SurfaceContentFixture.DetailFloorMaterial));

                RoomKit.PointEntity(map, "prop_detail", new Point(-64f, -64f, 0f),
                                    "model", "models/props_foliage/shrub_01a.mdl",
                                    "angles", "0 17 0", "detailOrientation", "2");
                RoomKit.PointEntity(map, "prop_detail_sprite", new Point(-96f, 64f, 0f),
                                    "angles", "0 0 0", "detailOrientation", "1",
                                    "position_ul", "-8 16", "position_lr", "8 0",
                                    "tex_ul", "0 0", "tex_size", "64 64", "tex_total_size", "512");
                return map;
            },
        };

        yield return new CatalogEntry
        {
            Name = "l1_detail_type_material",
            Level = MapLevel.L1,
            Features = [MapFeature.DetailTypeMaterial],
            Summary = "%detailtype naming a sparse model-only type on the floor and an unknown type on the walls",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomKit.ShellBrushes),
                PointEntities = CountRange.Exactly(2),
                Materials = [SurfaceContentFixture.DetailSparseMaterial, SurfaceContentFixture.DetailUnknownMaterial],
                Leaks = false,
                BrushEntities = CountRange.Exactly(0),
                Areas = CountRange.Exactly(2),
                Clusters = CountRange.Exactly(4),
                Portals = CountRange.Exactly(4),
            },
            Generator = _ =>
            {
                VmfMap map = SealedRoom();
                map.World.Add(new("detailvbsp", SurfaceContentFixture.DetailVbspName));
                map.WorldSolids[0].Sides[0].Material = SurfaceContentFixture.DetailSparseMaterial;
                for (int wall = 2; wall < RoomKit.ShellBrushes; wall++)
                {
                    foreach (VmfSide side in map.WorldSolids[wall].Sides)
                        side.Material = SurfaceContentFixture.DetailUnknownMaterial;
                }

                return map;
            },
        };

        yield return new CatalogEntry
        {
            Name = "l1_overlay",
            Level = MapLevel.L1,
            Features = [MapFeature.Overlay],
            Summary = "a named faded overlay on the floor and an unnamed one of render order 1 wrapping floor and wall",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomKit.ShellBrushes),
                PointEntities = CountRange.Exactly(4),
                Classnames = ["info_overlay"],
                Leaks = false,
                BrushEntities = CountRange.Exactly(0),
                Areas = CountRange.Exactly(2),
                Clusters = CountRange.Exactly(4),
                Portals = CountRange.Exactly(4),
            },
            Generator = _ =>
            {
                VmfMap map = SealedRoom();
                int floor = SideId(map, 0, 0);
                int wall = SideId(map, 2, 2);

                Overlay(map, OverlayMaterial, new Point(-64f, -64f, 0f), $"{floor}",
                        "targetname", "p3g_named_overlay", "fademindist", "300", "fademaxdist", "600");

                Overlay(map, OverlayMaterial, new Point(-240f, 0f, 0.1f), $"{floor} {wall}",
                        "RenderOrder", "1", "BasisU", "0 1 0", "BasisV", "1 0 0", "uv3", "32 -32 0.25",
                        "StartU", "0.25", "EndU", "0.75", "StartV", "1", "EndV", "0");
                return map;
            },
        };

        yield return new CatalogEntry
        {
            Name = "l1_water_overlay",
            Level = MapLevel.L1,
            Features = [MapFeature.WaterOverlay],
            Summary = "a pool with an overlaytransition block on its surface",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomKit.ShellBrushes + 1),
                PointEntities = CountRange.Exactly(3),
                Classnames = ["info_overlay_transition"],
                Leaks = false,
                BrushEntities = CountRange.Exactly(0),
                Areas = CountRange.Exactly(2),
                Clusters = CountRange.Exactly(8),
                Portals = CountRange.Exactly(12),
            },
            Generator = _ =>
            {
                VmfMap map = SealedRoom();
                RoomKit.Pool(map, new Bounds(
                    StandardRoom.Mins,
                    new Point(StandardRoom.Maxs.X, StandardRoom.Maxs.Y, StandardRoom.Mins.Z + 64f)));
                int surface = SideId(map, RoomKit.ShellBrushes, 0);

                VmfEntity transition = RoomKit.PointEntity(map, "info_overlay_transition", new Point(0f, 0f, 64f));
                VmfChunkNode block = new() { Name = "overlaytransition" };
                block.Children.Add(OverlayData(WaterOverlayMaterial, new Point(0f, 0f, 64f), $"{surface}", flipV: false));
                block.Children.Add(OverlayData(WaterOverlayMaterial, new Point(128f, 64f, 64f), $"{surface}", flipV: true));
                transition.Chunks.Add(block);
                return map;
            },
        };

        yield return new CatalogEntry
        {
            Name = "l1_env_cubemap",
            Level = MapLevel.L1,
            Features = [MapFeature.EnvCubemap],
            Summary = "specular walls, one env_cubemap naming a wall side and one naming none",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomKit.ShellBrushes),
                PointEntities = CountRange.Exactly(4),
                Materials = [SpecularMaterial],
                Classnames = ["env_cubemap"],
                Leaks = false,
                BrushEntities = CountRange.Exactly(0),
                Areas = CountRange.Exactly(2),
                Clusters = CountRange.Exactly(4),
                Portals = CountRange.Exactly(4),
            },
            Generator = _ =>
            {
                VmfMap map = SpecularRoom();
                int wall = SideId(map, 2, 2);

                RoomKit.PointEntity(map, "env_cubemap", new Point(-128.75f, 0.5f, 128.25f),
                                    "sides", $"{wall}", "cubemapsize", "5");
                RoomKit.PointEntity(map, "env_cubemap", new Point(160f, 160f, 64f),
                                    "sides", "", "cubemapsize", "0");
                return map;
            },
        };

        yield return new CatalogEntry
        {
            Name = "l1_default_cubemap",
            Level = MapLevel.L1,
            Features = [MapFeature.DefaultCubemap],
            Summary = "specular walls and no env_cubemap: only the skybox's default cubemaps reach the pak",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomKit.ShellBrushes),
                PointEntities = CountRange.Exactly(2),
                Materials = [SpecularMaterial],
                Leaks = false,
                BrushEntities = CountRange.Exactly(0),
                Areas = CountRange.Exactly(2),
                Clusters = CountRange.Exactly(4),
                Portals = CountRange.Exactly(4),
            },
            Generator = _ => SpecularRoom(),
        };
    }

    private static IEnumerable<CatalogEntry> SurfaceContentInteractions()
    {
        yield return new CatalogEntry
        {
            Name = "l2_cubemap_on_water_and_patch",
            Level = MapLevel.L2,
            Features = [MapFeature.EnvCubemap, MapFeature.Water, MapFeature.MaterialPatch],
            Summary = "an env_cubemap over water ($bottommaterial dependent) and over a patch material: patches of patches",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomKit.ShellBrushes + 1),
                PointEntities = CountRange.Exactly(3),
                Materials = [RoomKit.Water, SurfaceContentFixture.PatchedSpecularMaterial],
                Classnames = ["env_cubemap"],
                Leaks = false,
                BrushEntities = CountRange.Exactly(0),
                Areas = CountRange.Exactly(2),
                Clusters = CountRange.Exactly(8),
                Portals = CountRange.Exactly(12),
                HasLeafWaterData = true,
            },
            Generator = _ =>
            {
                VmfMap map = SealedRoom();
                for (int wall = 2; wall < RoomKit.ShellBrushes; wall++)
                {
                    foreach (VmfSide side in map.WorldSolids[wall].Sides)
                        side.Material = SurfaceContentFixture.PatchedSpecularMaterial;
                }

                RoomKit.Pool(map, new Bounds(
                    StandardRoom.Mins,
                    new Point(StandardRoom.Maxs.X, StandardRoom.Maxs.Y, StandardRoom.Mins.Z + 64f)));
                RoomKit.PointEntity(map, "env_cubemap", new Point(0f, 0f, 160f), "sides", "", "cubemapsize", "0");
                return map;
            },
        };

        yield return new CatalogEntry
        {
            Name = "l2_props_overlays_detail_cubemap",
            Level = MapLevel.L2,
            Features = [MapFeature.StaticProp, MapFeature.Overlay, MapFeature.DetailProps, MapFeature.EnvCubemap],
            Summary = "props, an overlay, detail props and a cubemap in one room: every 3g emitter at once",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomKit.ShellBrushes),
                PointEntities = CountRange.Exactly(6),
                Materials = [SurfaceContentFixture.DetailFloorMaterial, SpecularMaterial],
                Classnames = ["prop_static", "info_overlay", "env_cubemap"],
                Leaks = false,
                BrushEntities = CountRange.Exactly(0),
                Areas = CountRange.Exactly(2),
                Clusters = CountRange.Exactly(4),
                Portals = CountRange.Exactly(4),
            },
            Generator = _ =>
            {
                VmfMap map = SpecularRoom();
                map.World.Add(new("detailvbsp", SurfaceContentFixture.DetailVbspName));
                map.WorldSolids[0].Sides[0].Material = SurfaceContentFixture.DetailFloorMaterial;

                StaticProp(map, PlainPropModel, new Point(-160f, 96f, 0f), "0 10 0");
                StaticProp(map, "models/props_foliage/shrub_01a.mdl", new Point(100f, -100f, 0f), "0 250 0",
                           "solid", "0");
                Overlay(map, OverlayMaterial, new Point(32f, 32f, 0f), $"{SideId(map, 0, 0)}");
                RoomKit.PointEntity(map, "env_cubemap", new Point(0f, 0f, 128f), "sides", "", "cubemapsize", "0");
                return map;
            },
        };
    }

    /// <summary>A sealed room whose four walls are <see cref="SpecularMaterial"/>.</summary>
    public static VmfMap SpecularRoom()
    {
        VmfMap map = SealedRoom();
        for (int wall = 2; wall < RoomKit.ShellBrushes; wall++)
        {
            foreach (VmfSide side in map.WorldSolids[wall].Sides)
                side.Material = SpecularMaterial;
        }

        return map;
    }

    /// <summary>
    /// The id <see cref="VmfMap.Write"/> will give one world solid's side.
    /// </summary>
    /// <remarks>
    /// Ids are handed out at write time (world 1, then each solid and each of
    /// its sides in order), so a side list can only be written by replaying
    /// that numbering. World solids come before every entity, which is what
    /// makes this computable while entities are still being added.
    /// </remarks>
    /// <param name="map">The map, with its world solids already built.</param>
    /// <param name="solid">The world solid's index.</param>
    /// <param name="side">The side's index in that solid.</param>
    public static int SideId(VmfMap map, int solid, int side)
    {
        ArgumentNullException.ThrowIfNull(map);

        int id = 1;
        for (int s = 0; s < solid; s++)
            id += 1 + map.WorldSolids[s].Sides.Count;

        return id + 1 + side + 1;
    }

    private static VmfEntity StaticProp(VmfMap map, string model, Point at, string angles, params string[] keyValues)
    {
        VmfEntity prop = RoomKit.PointEntity(map, "prop_static", at, "model", model, "angles", angles,
                                             "solid", "6", "skin", "0", "fademindist", "-1",
                                             "fademaxdist", "0", "fadescale", "1");
        for (int i = 0; i < keyValues.Length; i += 2)
            prop.Set(keyValues[i], keyValues[i + 1]);

        return prop;
    }

    private static VmfEntity Overlay(VmfMap map, string material, Point at, string sides, params string[] keyValues)
    {
        VmfEntity overlay = RoomKit.PointEntity(map, "info_overlay", at,
            "material", material, "sides", sides, "RenderOrder", "0",
            "StartU", "0", "EndU", "1", "StartV", "0", "EndV", "1",
            "BasisOrigin", at.ToString(), "BasisU", "1 0 0", "BasisV", "0 1 0", "BasisNormal", "0 0 1",
            "uv0", "-32 -32 0", "uv1", "-32 32 0", "uv2", "32 32 0", "uv3", "32 -32 0",
            "fademindist", "-1", "fademaxdist", "0", "angles", "0 0 0");
        for (int i = 0; i < keyValues.Length; i += 2)
            overlay.Set(keyValues[i], keyValues[i + 1]);

        return overlay;
    }

    // Stock reads overlaydata's vectors with a chunk-key reader that wants
    // BRACKETS ("[%f %f %f]") -- unlike info_overlay's keys,
    // which go through the entity-key reader. An unbracketed value fails the parse
    // and leaves the vector as constructed.
    private static VmfChunkNode OverlayData(string material, Point at, string sides, bool flipV)
    {
        VmfChunkNode data = new() { Name = "overlaydata" };
        data.KeyValues.Add(new("material", material));
        data.KeyValues.Add(new("StartU", "0"));
        data.KeyValues.Add(new("EndU", "1"));
        data.KeyValues.Add(new("StartV", "0.125"));
        data.KeyValues.Add(new("EndV", "1"));
        data.KeyValues.Add(new("BasisOrigin", $"[{at}]"));
        data.KeyValues.Add(new("BasisU", "[1 0 0]"));
        data.KeyValues.Add(new("BasisV", flipV ? "[0 -1 0]" : "[0 1 0]"));
        data.KeyValues.Add(new("BasisNormal", "[0 0 1]"));
        data.KeyValues.Add(new("uv0", "[-24 -24 0]"));
        data.KeyValues.Add(new("uv1", "[-24 24 0]"));
        data.KeyValues.Add(new("uv2", "[24 24 0]"));
        data.KeyValues.Add(new("uv3", "[24 -24 0.5]"));
        data.KeyValues.Add(new("sides", sides));
        return data;
    }
}
