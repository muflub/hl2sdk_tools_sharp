using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Validation;

/// <content>
/// The rules about the container: the header, the lump directory, lump lengths,
/// the <c>MAX_MAP_*</c> caps and the lump versions.
/// </content>
public static partial class BspValidator
{
    /// <summary>
    /// <see cref="BspRuleCodes.FileVersion"/> and
    /// <see cref="BspRuleCodes.PakFileLast"/>.
    /// </summary>
    private static void CheckHeader(BspData bsp, Findings findings)
    {
        // -- CMapLoadHelper::Init rejects the
        // file outright, "has wrong version (%i when expecting %i)". That
        // Branch's range is MINBSPVERSION..BSPVERSION,
        // 19..20; the ceiling here is BspData.MaxVersion, 21, because the
        // contemporary branches' loaders read 21 and the format presets write
        // it. Same range as the reader and as CheckFileAsync -- see the rule
        // remarks for why the three must agree.
        if (bsp.FileVersion is < BspData.MinVersion or > BspData.MaxVersion)
        {
            findings.Add(
                BspRuleCodes.FileVersion,
                $"the file version is {bsp.FileVersion}; the loaders read "
                + $"{BspData.MinVersion}..{BspData.MaxVersion}");
        }

        CheckPakFileLast(bsp, findings);
    }

    /// <summary>
    /// <see cref="BspRuleCodes.PakFileLast"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walks every lump looking for one
    /// that starts after the lighting lump and is not <c>LUMP_PAKFILE</c>; the
 /// 360 loader then reads the file up TO the pak lump's
    /// offset and calls that "guranateed last". So the format's rule is that
    /// nothing may start after the pak.
    /// </para>
    /// <para>
    /// This reads <see cref="BspData.SourceLayout"/>, which describes the file
    /// the container was read FROM. A container built in memory has none and is
    /// skipped: lump order is decided when it is written, and
    /// <c>BspFile.SaveAsync</c> already puts the pak last.
    /// </para>
    /// </remarks>
    private static void CheckPakFileLast(BspData bsp, Findings findings)
    {
        IReadOnlyList<BspLumpPlacement>? layout = bsp.SourceLayout;
        if (layout is null || layout.Count != BspData.HeaderLumps)
        {
            return;
        }

        BspLumpPlacement pak = layout[(int)BspLump.PakFile];
        if (!pak.Written || pak.Length == 0)
        {
            return;
        }

        int failures = 0;
        string first = string.Empty;
        for (int i = 0; i < layout.Count; i++)
        {
            if (i == (int)BspLump.PakFile || !layout[i].Written)
            {
                continue;
            }

            if (layout[i].Offset > pak.Offset)
            {
                failures++;
                if (failures == 1)
                {
                    first =
                        $"lump {i} starts at {layout[i].Offset}, after the PAKFILE lump at "
                        + $"{pak.Offset}; the engine expects the pak to be the last lump";
                }
            }
        }

        if (failures > 0)
        {
            findings.AddRepeated(BspRuleCodes.PakFileLast, first, failures);
        }
    }

    /// <summary>
    /// <see cref="BspRuleCodes.LumpElementSize"/>,
    /// <see cref="BspRuleCodes.LumpCap"/> and
    /// <see cref="BspRuleCodes.RequiredLumpEmpty"/>.
    /// </summary>
    private static void CheckLumpShapes(BspData bsp, Counts counts, Findings findings)
    {
        CheckElementSizes(bsp, findings);
        CheckCaps(bsp, findings);

        // 383,423,569,609,839,877 -- "Map with no
        // textures", "Map with no texinfo", "Map with no leafs", "Map with no
        // planes" (twice, the second is really leafbrushes), "Map with no
        // models", "Map has no nodes". Each is count < 1 and each is fatal.
        foreach ((BspLump lump, string engineMessage) in BspLimits.Required)
        {
            if (Counts.Of(bsp, lump) < 1)
            {
                findings.Add(
                    BspRuleCodes.RequiredLumpEmpty,
                    $"the {lump} lump is empty; the collision loader stops with "
                    + $"\"{engineMessage}\"");
            }
        }

        // -- the one MAX_MAP_* the renderer's
        // own loader checks, and the only lump with a LOWER bound as well.
        if (counts.SurfEdges < 1 || counts.SurfEdges >= BspLimits.MaxMapSurfEdges)
        {
            findings.Add(
                BspRuleCodes.SurfEdgeCount,
                $"the SurfEdges lump holds {counts.SurfEdges} entries; the engine demands at "
                + $"least 1 and fewer than MAX_MAP_SURFEDGES ({BspLimits.MaxMapSurfEdges})");
        }
    }

    /// <summary>
    /// <see cref="BspRuleCodes.LumpElementSize"/>: the "funny lump size" gate.
    /// </summary>
    /// <remarks>
    /// Is the generic form --
    /// <c>if ( lh.LumpSize() % elementSize ) Host_Error( "Mod_LoadLump: funny
    /// lump size in %s" )</c> -- and the same idiom is written out by hand at
 /// Modelloader.
 /// 1868 and
 /// Cmodel_bsp,:318,
    /// </remarks>
    private static void CheckElementSizes(BspData bsp, Findings findings)
    {
        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            BspLumpData data = bsp[i];
            if (data.IsEmpty)
            {
                continue;
            }

            BspLump lump = (BspLump)i;
            int? size = ElementSizeAsRead(lump, data.Version);
            if (size is not > 0)
            {
                // Not an array of a fixed struct: visibility, occlusion, the
                // game lump, the pak, the cooked physics. Those have framing
                // rules of their own rather than a modulus.
                continue;
            }

            int element = size.GetValueOrDefault();
            if (data.Length % element != 0)
            {
                findings.Add(
                    BspRuleCodes.LumpElementSize,
                    $"lump {i} ({lump}) is {data.Length} bytes, which is not a whole number of "
                    + $"{element}-byte elements; {data.Length % element} bytes are left over");
            }
        }
    }

    /// <summary>
    /// The size of one element of a lump AS THE ENGINE READS IT, which is not
    /// always the size of the struct the current header names for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="BspLumpLayout.ElementSize"/> answers with
    /// <c>dleafambientlighting_t</c> (28 bytes) for the two leaf-ambient lumps,
    /// which is right for a lump at version 1. It is wrong for one at any other
    /// Version: takes the legacy branch
    /// and casts the SAME lump to <c>CompressedLightCube*</c> (24 bytes),
    /// asserting the length divides by that and holds one per leaf.
    /// </para>
    /// <para>
    /// So the lump's element size is decided by its version here too, exactly
    /// as <c>dleaf_t</c>'s is. Reporting a version-0 ambient lump as a ragged
    /// array of 28-byte records would be a finding about a struct the engine
    /// never reads it as.
    /// </para>
    /// </remarks>
    private static int? ElementSizeAsRead(BspLump lump, int lumpVersion)
    {
        if (lump is BspLump.LeafAmbientLighting or BspLump.LeafAmbientLightingHdr
            && lumpVersion != BspLumpLayout.CurrentVersion(BspLump.LeafAmbientLighting))
        {
            return System.Runtime.CompilerServices.Unsafe.SizeOf<CompressedLightCube>();
        }

        return BspLumpLayout.ElementSize(lump, lumpVersion);
    }

    /// <summary>
    /// <see cref="BspRuleCodes.LumpCap"/>: the <c>MAX_MAP_*</c> caps the
    /// collision loader checks.
    /// </summary>
    private static void CheckCaps(BspData bsp, Findings findings)
    {
        foreach ((BspLump lump, int max, string constant) in BspLimits.Caps)
        {
            int count = Counts.Of(bsp, lump);
            if (count > max)
            {
                findings.Add(
                    BspRuleCodes.LumpCap,
                    $"the {lump} lump holds {count} elements, above {constant} ({max})");
            }
        }

        // -- the visibility lump is capped on its
        // BYTE length, because it is not an array of anything.
        int visBytes = bsp[BspLump.Visibility].Length;
        if (visBytes > BspLimits.MaxMapVisibilityBytes)
        {
            findings.Add(
                BspRuleCodes.LumpCap,
                $"the Visibility lump is {visBytes} bytes, above MAX_MAP_VISIBILITY "
                + $"({BspLimits.MaxMapVisibilityBytes})");
        }
    }

    /// <summary>
    /// <see cref="BspRuleCodes.OcclusionVersion"/>,
    /// <see cref="BspRuleCodes.LeafsVersion"/>,
    /// <see cref="BspRuleCodes.LeafAmbientLegacyPath"/>,
    /// <see cref="BspRuleCodes.LeafAmbientLegacyCount"/>,
    /// <see cref="BspRuleCodes.HdrLumpPair"/> and
    /// <see cref="BspRuleCodes.TexDataStringNul"/>.
    /// </summary>
    private static void CheckLumpVersions(BspData bsp, Counts counts, Findings findings)
    {
        // -- a switch on the lump version with
        // cases 2, 1 and 0, and Host_Error("Invalid occlusion lump version!")
 // in the default. The size test comes FIRST, so an empty
        // occlusion lump is never asked what version it is.
        BspLumpData occlusion = bsp[BspLump.Occlusion];
        if (!occlusion.IsEmpty && occlusion.Version is not (0 or 1 or 2))
        {
            findings.Add(
                BspRuleCodes.OcclusionVersion,
                $"the Occlusion lump is at version {occlusion.Version}; the engine reads 0, 1 "
                + "and 2 and stops on anything else");
        }

        // both loaders switch on this version and both Error on the default.
        // It is also what decides whether a leaf is 56 bytes or 32, so a wrong
        // version reads every leaf at the wrong stride.
        int leafVersion = bsp[BspLump.Leafs].Version;
        if (leafVersion is not (0 or 1))
        {
            findings.Add(
                BspRuleCodes.LeafsVersion,
                $"the Leafs lump is at version {leafVersion}; the engine reads 0 (56-byte "
                + "leaves) and 1 (32-byte leaves) and stops on anything else");
        }

        CheckLeafAmbient(bsp, counts, findings);
        CheckHdrPair(bsp, findings);

        // -- "It's assumed by other code
        // that the data lump is filled with C strings. We need to make sure
        // that the buffer as a whole ends with a '\0'." Without it every
        // consumer's strlen walks off the end of the lump.
        BspLumpData strings = bsp[BspLump.TexDataStringData];
        if (!strings.IsEmpty && strings.Data.Span[^1] != 0)
        {
            findings.Add(
                BspRuleCodes.TexDataStringNul,
                $"the TexDataStringData lump's last byte is 0x{strings.Data.Span[^1]:X2}, not a "
                + "NUL; every material name is read from it as a C string");
        }
    }

    /// <summary>
    /// <see cref="BspRuleCodes.LeafAmbientLegacyPath"/> and
    /// <see cref="BspRuleCodes.LeafAmbientLegacyCount"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>if (
    /// ambientLightingLump.LumpVersion() != LUMP_LEAF_AMBIENT_LIGHTING_VERSION
    /// || ambientLightingTable.LumpSize() == 0 )</c> takes the legacy branch,
    /// which reads the lump as a flat <c>CompressedLightCube</c> per leaf and
    /// throws away the per-leaf sample positions entirely.
    /// </para>
    /// <para>
    /// The map is still loaded, so this is a warning -- but it is the shape of
    /// bug that is invisible in-game until someone notices the ambient lighting
    /// is one flat cube per leaf. A map with no ambient lump at all is silent:
    /// that is an unlit map, not a broken one.
    /// </para>
    /// </remarks>
    private static void CheckLeafAmbient(BspData bsp, Counts counts, Findings findings)
    {
        BspLumpData ambient = bsp[BspLump.LeafAmbientLighting];
        if (ambient.IsEmpty)
        {
            return;
        }

        bool legacy = ambient.Version != BspLumpLayout.CurrentVersion(BspLump.LeafAmbientLighting)
            || bsp[BspLump.LeafAmbientIndex].IsEmpty;

        if (!legacy)
        {
            return;
        }

        findings.Add(
            BspRuleCodes.LeafAmbientLegacyPath,
            $"the LeafAmbientLighting lump is at version {ambient.Version} with a "
            + $"{bsp[BspLump.LeafAmbientIndex].Length}-byte index, so the engine takes the "
            + "legacy path and reads it as one light cube per leaf, discarding the sample "
            + "positions");

        // 2210-2212 asserts the lump is a whole number of CompressedLightCube
 // and that there is exactly one per leaf, memcpy's
        // inLightCubes[i] for every leaf. Short, and it reads past the lump.
        int cubeSize = System.Runtime.CompilerServices.Unsafe.SizeOf<CompressedLightCube>();
        int cubes = ambient.Length / cubeSize;
        if (ambient.Length % cubeSize != 0 || cubes != counts.Leafs)
        {
            findings.Add(
                BspRuleCodes.LeafAmbientLegacyCount,
                $"the legacy path reads one {cubeSize}-byte light cube per leaf, but the "
                + $"{ambient.Length}-byte lump holds {cubes} for {counts.Leafs} leaves");
        }
    }

    /// <summary>
    /// <see cref="BspRuleCodes.HdrLumpPair"/>.
    /// </summary>
    /// <remarks>
    /// <c>bHasHDR = LumpSize(
    /// LUMP_LIGHTING_HDR ) &gt; 0 &amp;&amp; LumpSize( LUMP_WORLDLIGHTS_HDR )
 /// &gt; 0;</c>, and then clears it again if the file is
    /// version 20 or newer and has no HDR leaf ambient lump. Half a set of HDR
    /// lumps is megabytes of data the engine will never look at.
    /// </remarks>
    private static void CheckHdrPair(BspData bsp, Findings findings)
    {
        bool lighting = !bsp[BspLump.LightingHdr].IsEmpty;
        bool worldLights = !bsp[BspLump.WorldLightsHdr].IsEmpty;

        if (lighting != worldLights)
        {
            findings.Add(
                BspRuleCodes.HdrLumpPair,
                lighting
                    ? "LightingHdr is present but WorldLightsHdr is empty, so the engine treats "
                      + "the map as having no HDR at all"
                    : "WorldLightsHdr is present but LightingHdr is empty, so the engine treats "
                      + "the map as having no HDR at all");
            return;
        }

        if (lighting && bsp.FileVersion >= 20 && bsp[BspLump.LeafAmbientLightingHdr].IsEmpty)
        {
            findings.Add(
                BspRuleCodes.HdrLumpPair,
                $"the map is version {bsp.FileVersion} with HDR lighting but no "
                + "LeafAmbientLightingHdr, which turns HDR back off when it loads the map");
        }
    }

    /// <summary>
    /// <see cref="BspRuleCodes.StaticPropVersion"/>,
    /// <see cref="BspRuleCodes.DetailPropVersion"/> and the framing half of
    /// <see cref="BspRuleCodes.SubLumpFraming"/> for the game lumps.
    /// </summary>
    private static void CheckGameLumps(BspData bsp, Findings findings)
    {
        foreach (GameLumpEntry entry in bsp.GameLumps)
        {
            string code = entry.IdString();

            // -- "Really old map format!
            // Static props can't be loaded..." is a Warning and a return, so
            // the map loads and every prop in it is gone.
            if (code == GameLumpId.StaticProps && entry.Version < BspLimits.MinStaticPropVersion)
            {
                findings.Add(
                    BspRuleCodes.StaticPropVersion,
                    $"the sprp game lump is at version {entry.Version}; below "
                    + $"{BspLimits.MinStaticPropVersion} the engine drops every static prop and "
                    + "only prints a warning");
            }

            // -- the same shape:
            // "Map uses old detail prop file format.. ignoring detail props".
            if (code == GameLumpId.DetailProps && entry.Version < BspLimits.MinDetailPropVersion)
            {
                findings.Add(
                    BspRuleCodes.DetailPropVersion,
                    $"the dprp game lump is at version {entry.Version}; below "
                    + $"{BspLimits.MinDetailPropVersion} the client drops every detail prop and "
                    + "only prints a warning");
            }
        }
    }
}
