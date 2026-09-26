//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Bsp.Cubemaps;

/// <summary>
/// The load-time half of the reference implementation: pointing specular
/// brush sides at a per-cubemap patch of their material, and collecting the
/// names of the cubemap VTFs the default-cubemap pass must write.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stock call order</b>, all after
/// <c>LoadMapFile</c> and <c>WorldVertexTransitionFixup</c> and only when
/// <c>g_nDXLevel</c> is 0 or at least 70:
/// <see cref="FixupBrushSidesMaterialsAsync"/>, then
/// <see cref="AttachDefaultCubemapToSpecularSidesAsync"/>, then
/// <see cref="AddUnreferencedCubemaps"/>. The names gathered here are consumed
/// much later by <see cref="DefaultCubemapBuilder"/>, which stock runs after
/// <c>ProcessModels</c> and before <c>EndBSPFile</c>.
/// </para>
/// <para>
/// One instance per compile; it holds what stock keeps in the statics
/// <c>s_EnvCubemapToBrushSides</c>, <c>s_DefaultCubemapNames</c>,
/// <c>g_IsCubemapTexData</c> and <c>s_aCubemapSideData</c>.
/// </para>
/// <para>
/// A displacement side's face carries its own copy of the texinfo in stock
/// (<c>pSide-&gt;pMapDisp-&gt;face.texinfo</c>, <c>,950-953</c>).
/// <see cref="IMapDisplacement"/> has no such field; whoever builds the
/// displacement face must take the texinfo from the side AFTER these fixups.
/// </para>
/// </remarks>
public sealed class CubemapFixups
{
    /// <summary><c>TEXTURE_NAME_LENGTH</c>.</summary>
    public const int TextureNameLength = 128;

    // s_pDependentMaterialVar, in its order.
    private static string[] DependentMaterialVars => ["$bottommaterial", "$crackmaterial", "$fallbackmaterial"];

    private readonly VbspContext _context;
    private readonly MapFile _map;
    private readonly MaterialPatcher _patcher;
    private readonly List<List<int>> _sampleSides = [];
    private readonly HashSet<int> _cubemapTexData = [];
    private readonly List<string> _defaultCubemapNames = [];

    /// <summary>Starts the fixups for one compile.</summary>
    /// <param name="context">
    /// The compile. Its <see cref="VbspContext.CubemapSamples"/> are the
    /// <c>env_cubemap</c>s, and each sample's <c>sides</c> string is parsed
    /// here as <c>Cubemap_SaveBrushSides</c> did at load
    /// </param>
    /// <param name="map">The main map: <c>g_MainMap</c>.</param>
    /// <param name="patcher">The compile's material patcher.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public CubemapFixups(VbspContext context, MapFile map, MaterialPatcher patcher)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(patcher);

        _context = context;
        _map = map;
        _patcher = patcher;

        foreach (CubemapSample sample in context.CubemapSamples)
        {
            _sampleSides.Add(ParseSideList(sample.Sides));
        }
    }

    /// <summary>
    /// <c>s_DefaultCubemapNames</c>: the VTFs the default-cubemap pass copies
    /// the skybox cubemap into, <c>materials/maps/&lt;map&gt;/c&lt;x&gt;_&lt;y&gt;_&lt;z&gt;.vtf</c>,
    /// in the order they were added. May hold duplicates; see
    /// <see cref="AddUnreferencedCubemaps"/>.
    /// </summary>
    public IReadOnlyList<string> DefaultCubemapNames => _defaultCubemapNames;

    /// <summary>
    /// <c>Cubemap_InsertSample</c>'s integer origin: each coordinate converted
    /// with a C cast, which TRUNCATES toward zero.
    /// </summary>
    /// <param name="origin">The entity origin.</param>
    /// <returns>The sample origin.</returns>
    public static (int X, int Y, int Z) SampleOrigin(Vec3 origin) =>
        ((int)origin.X, (int)origin.Y, (int)origin.Z);

    /// <summary>
    /// <c>GeneratePatchedName</c>:
    /// <c>maps/&lt;map&gt;/&lt;material&gt;[_]x_y_z</c>, slashes forward, lower case.
    /// </summary>
    /// <param name="materialName">The material, or <c>"c"</c> for a cubemap texture.</param>
    /// <param name="mapBase">The map name.</param>
    /// <param name="origin">The sample origin.</param>
    /// <param name="isMaterialName">True to separate the coordinates with an underscore.</param>
    /// <returns>The name.</returns>
    /// <exception cref="MapCompileException">
    /// A material name of <see cref="TextureNameLength"/> - 1 characters or more.
    /// </exception>
    public static string PatchedName(string materialName, string mapBase, (int X, int Y, int Z) origin, bool isMaterialName)
    {
        ArgumentNullException.ThrowIfNull(materialName);
        ArgumentNullException.ThrowIfNull(mapBase);

        string separator = isMaterialName ? "_" : string.Empty;
        string name = string.Create(
            CultureInfo.InvariantCulture,
            $"maps/{mapBase}/{materialName}{separator}{origin.X}_{origin.Y}_{origin.Z}");

        if (isMaterialName && name.Length >= TextureNameLength - 1)
        {
            throw new MapCompileException(
                $"Generated env_cubemap patch name : {name} too long! (max = {TextureNameLength})");
        }

        return name.Replace('\\', '/').ToLowerInvariant();
    }

    /// <summary>
    /// <c>Cubemap_FixupBrushSidesMaterials</c>:
    /// every side an <c>env_cubemap</c> named gets that cubemap's patch.
    /// </summary>
    /// <param name="cancellationToken">Cancels the material reads.</param>
    /// <returns>A task.</returns>
    public async Task FixupBrushSidesMaterialsAsync(CancellationToken cancellationToken = default)
    {
        for (int cubemap = 0; cubemap < _context.CubemapSamples.Count; cubemap++)
        {
            (int X, int Y, int Z) origin = SampleOrigin(_context.CubemapSamples[cubemap].Origin);

            foreach (int sideId in _sampleSides[cubemap])
            {
                cancellationToken.ThrowIfCancellationRequested();

                int sideIndex = _map.SideIdToIndex(sideId);
                if (sideIndex < 0)
                {
                    _context.Diagnostics.Add(new CompileDiagnostic(
                        SurfaceContentDiagnostics.CubemapDeletedSide,
                        DiagnosticSeverity.Warning,
                        $"env_cubemap pointing at deleted brushside near ({origin.X}, {origin.Y}, {origin.Z})",
                        new MapLocation(Position: (origin.X, origin.Y, origin.Z))));
                    continue;
                }

                MapBrushSide side = _map.BrushSides[sideIndex];
                side.TexInfo = await CreateTexInfoAsync(side.TexInfo, origin, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// <c>Cubemap_AttachDefaultCubemapToSpecularSides</c>
    /// Every side whose material (or a
    /// dependent) has <c>$envmap</c>, and that no <c>env_cubemap</c> named, is
    /// patched to the nearest sample in front of it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the material reads.</param>
    /// <returns>A task.</returns>
    public async Task AttachDefaultCubemapToSpecularSidesAsync(CancellationToken cancellationToken = default)
    {
        bool[] specular = await InitSpecularAsync(cancellationToken).ConfigureAwait(false);
        bool[] manual = ManuallyPicked();

        int[] sideEntity = new int[_map.BrushSideCount];
        Array.Fill(sideEntity, -1);
        foreach (MapBrush brush in _map.Brushes)
        {
            for (int j = 0; j < brush.SideCount; j++)
            {
                sideEntity[brush.FirstSide + j] = brush.EntityNumber;
            }
        }

        for (int i = 0; i < _map.BrushSideCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!specular[i] || manual[i])
            {
                continue;
            }

            // A side no kept brush owns (a discarded brush's slot was reused,
            //) reads entities[-1] in stock: memory before
            // the array, unreproducible. The world's origin is used instead.
            int entity = sideEntity[i];
            Vec3 entityOrigin = entity >= 0 ? _map.Entities[entity].Origin : Vec3.Zero;

            MapBrushSide side = _map.BrushSides[i];
            int cubemap = FindClosestCubemap(entityOrigin, side);
            if (cubemap == -1)
            {
                continue;
            }

            (int X, int Y, int Z) origin = SampleOrigin(_context.CubemapSamples[cubemap].Origin);
            side.TexInfo = await CreateTexInfoAsync(side.TexInfo, origin, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>Cubemap_AddUnreferencedCubemaps</c>:
    /// every sample's VTF name, whether or not any side used it.
    /// </summary>
    /// <remarks>
    /// The "already added" test compares the bare texture name
    /// (<c>maps/m/c1_2_3</c>) with the stored FILE names
    /// (<c>materials/maps/m/c1_2_3.vtf</c>) and so never matches: every sample
    /// is appended again. Harmless in stock's output, because the writer
    /// skips a name already in the pak: the pak is the same
    /// either way. Reproduced under
    /// <see cref="StockQuirk.CubemapUnreferencedNeverMatches"/>; fixed otherwise.
    /// </remarks>
    public void AddUnreferencedCubemaps()
    {
        foreach (CubemapSample sample in _context.CubemapSamples)
        {
            string texture = PatchedName("c", _context.MapBase, SampleOrigin(sample.Origin), isMaterialName: false);

            string file = $"materials/{texture}.vtf";

            // Stock compares the bare texture name with the stored FILE names
            // Which never match; Correct compares like with like.
            string wanted = _context.Options.Compliance.Emulates(StockQuirk.CubemapUnreferencedNeverMatches) ? texture : file;
            bool found = _defaultCubemapNames.Any(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
            if (!found)
            {
                _defaultCubemapNames.Add(file);
            }
        }
    }

    /// <summary>
    /// <c>Cubemap_FindClosestCubemap</c>.
    /// </summary>
    /// <param name="entityOrigin">The origin of the entity owning the side.</param>
    /// <param name="side">The side.</param>
    /// <returns>
    /// The nearest sample in front of the side's plane, else the nearest
    /// sample, else -1 when there are none; 0 for a side with no winding
    /// ("a valid (if random) cubemap",).
    /// </returns>
    public int FindClosestCubemap(Vec3 entityOrigin, MapBrushSide side)
    {
        ArgumentNullException.ThrowIfNull(side);

        if (side.Winding.IsNull)
        {
            return 0;
        }

        ReadOnlySpan<Vec3> points = _map.Windings.Points(side.Winding);
        Vec3 centre = Vec3.Zero;
        foreach (Vec3 p in points)
        {
            centre = centre + p;
        }

        // VectorScale(vecCenter, 1.0f / numpoints): a float reciprocal, then
        // three multiplies.
        float scale = 1.0f / points.Length;
        centre = new Vec3(centre.X * scale, centre.Y * scale, centre.Z * scale);
        centre = centre + entityOrigin;

        Vec3 normal = _map.Planes[side.PlaneNumber].Normal;
        bool stockNormalise = _context.Options.Compliance.Emulates(StockQuirk.CubemapDistanceNormalise);

        int best = -1;
        float bestDist = float.MaxValue;

        for (int i = 0; i < _context.CubemapSamples.Count; i++)
        {
            (int X, int Y, int Z) s = SampleOrigin(_context.CubemapSamples[i].Origin);
            Vec3 delta = new Vec3(s.X, s.Y, s.Z) - centre;

            (Vec3 direction, float dist) = stockNormalise ? delta.NormaliseLikeStock() : delta.Normalise();
            float dot = Vec3.Dot(direction, normal);
            if (dot >= 0.0f && dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }

        if (best == -1)
        {
            for (int i = 0; i < _context.CubemapSamples.Count; i++)
            {
                (int X, int Y, int Z) s = SampleOrigin(_context.CubemapSamples[i].Origin);
                float dist = (new Vec3(s.X, s.Y, s.Z) - centre).Length();
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = i;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// <c>Cubemap_CreateTexInfo</c>: the texinfo
    /// of the per-cubemap patch of a side's material, creating the patch,
    /// the texdata and the texinfo as needed.
    /// </summary>
    /// <param name="originalTexInfo">The side's texinfo.</param>
    /// <param name="origin">The sample origin.</param>
    /// <param name="cancellationToken">Cancels the material reads.</param>
    /// <returns>The texinfo the side should use.</returns>
    public async ValueTask<int> CreateTexInfoAsync(
        int originalTexInfo,
        (int X, int Y, int Z) origin,
        CancellationToken cancellationToken = default)
    {
        if (originalTexInfo == TexInfoTable.TexInfoNode)
        {
            return originalTexInfo;
        }

        TexInfo texInfo = _context.TexInfos[originalTexInfo];
        string materialName = _context.TexDatas.NameOf(texInfo.TexData);

        if (_cubemapTexData.Contains(texInfo.TexData))
        {
            _context.Diagnostics.Add(new CompileDiagnostic(
                SurfaceContentDiagnostics.CubemapMultipleReferences,
                DiagnosticSeverity.Warning,
                $"Multiple references for cubemap on texture {materialName}!!!"));
            return originalTexInfo;
        }

        // Q_stristr: already a patch for this very position.
        string suffix = string.Create(CultureInfo.InvariantCulture, $"_{origin.X}_{origin.Y}_{origin.Z}");
        if (materialName.Contains(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return originalTexInfo;
        }

        string generated = PatchedName(materialName, _context.MapBase, origin, isMaterialName: true);

        int texData = _context.TexDatas.Find(generated);
        bool hadTexData = texData != -1;

        if (!hadTexData)
        {
            string texture = PatchedName("c", _context.MapBase, origin, isMaterialName: false);

            if (!await PatchEnvmapAsync(materialName, origin, texture, 0, cancellationToken).ConfigureAwait(false))
            {
                return originalTexInfo;
            }

            _defaultCubemapNames.Add($"materials/{texture}.vtf");

            texData = _context.TexDatas.AddClone(texInfo.TexData, generated);
            _cubemapTexData.Add(texData);
        }

        TexInfo patched = texInfo;
        patched.TexData = texData;

        if (hadTexData)
        {
            int existing = _context.TexInfos.Find(patched);
            if (existing != -1)
            {
                return existing;
            }
        }

        return _context.TexInfos.Add(patched);
    }

    // PatchEnvmapForMaterialAndDependents. Stock recurses
    // without a bound, so a cycle of dependents overflows its stack; the
    // depth bound here turns that crash into "no patch".
    private async ValueTask<bool> PatchEnvmapAsync(
        string materialName,
        (int X, int Y, int Z) origin,
        string cubemapTexture,
        int depth,
        CancellationToken cancellationToken)
    {
        const int maxDepth = 32;
        if (depth > maxDepth)
        {
            return false;
        }

        bool patchEnvmap = await _patcher
            .HasKeyValuePairAsync(materialName, "$envmap", "env_cubemap", cancellationToken)
            .ConfigureAwait(false);

        (string Name, string Var)? dependent = await FindDependentMaterialAsync(materialName, cancellationToken)
            .ConfigureAwait(false);

        bool dependentPatched = dependent is { } d &&
            await PatchEnvmapAsync(d.Name, origin, cubemapTexture, depth + 1, cancellationToken).ConfigureAwait(false);

        if (!patchEnvmap && !dependentPatched)
        {
            return false;
        }

        List<MaterialPatchInfo> infos = [];
        if (patchEnvmap)
        {
            infos.Add(new MaterialPatchInfo("$envmap", cubemapTexture, "env_cubemap"));
        }

        if (dependentPatched)
        {
            infos.Add(new MaterialPatchInfo(
                dependent!.Value.Var,
                PatchedName(dependent.Value.Name, _context.MapBase, origin, isMaterialName: true)));
        }

        await _patcher.CreatePatchAsync(
                materialName,
                PatchedName(materialName, _context.MapBase, origin, isMaterialName: true),
                infos,
                MaterialPatchType.Replace,
                cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    // FindDependentMaterial: the first of the three vars
    // the material has at its top level, skipping one that names the material
    // itself.
    private async ValueTask<(string Name, string Var)?> FindDependentMaterialAsync(
        string materialName,
        CancellationToken cancellationToken)
    {
        foreach (string var in DependentMaterialVars)
        {
            string? value = await _patcher.GetValueAsync(materialName, var, cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                continue;
            }

            // Q_strncpy into MAX_MATERIAL_NAME - 1 = 511 bytes, so a longer
            // value is cut to 510 characters plus the NUL.
            if (value.Length > 510)
            {
                value = value[..510];
            }

            if (string.Equals(value, materialName, StringComparison.OrdinalIgnoreCase))
            {
                _context.Diagnostics.Add(new CompileDiagnostic(
                    SurfaceContentDiagnostics.MaterialDependsOnItself,
                    DiagnosticSeverity.Warning,
                    $"Material {materialName} is depending on itself through materialvar {var}! Ignoring..."));
                continue;
            }

            return (value, var);
        }

        return null;
    }

    // Cubemap_InitCubemapSideData's first loop, with the
    // cache keyed on the texdata's string-table id as stock's is.
    private async ValueTask<bool[]> InitSpecularAsync(CancellationToken cancellationToken)
    {
        bool[] specular = new bool[_map.BrushSideCount];
        Dictionary<int, bool> byName = [];

        for (int i = 0; i < _map.BrushSideCount; i++)
        {
            MapBrushSide side = _map.BrushSides[i];
            if (side.TexInfo == TexInfoTable.TexInfoNode)
            {
                continue;
            }

            DTexData texData = _context.TexDatas[_context.TexInfos[side.TexInfo].TexData];
            if (byName.TryGetValue(texData.NameStringTableId, out bool known))
            {
                specular[i] = known;
                continue;
            }

            string name = _context.Strings.GetString(texData.NameStringTableId);
            bool uses = await UsesEnvmapAsync(name, 0, cancellationToken).ConfigureAwait(false);
            specular[i] = uses;
            byName.Add(texData.NameStringTableId, uses);
        }

        return specular;
    }

    // DoesMaterialOrDependentsUseEnvmap.
    private async ValueTask<bool> UsesEnvmapAsync(string patchedName, int depth, CancellationToken cancellationToken)
    {
        if (depth > 32)
        {
            return false;
        }

        string original = _patcher.OriginalNameFor(patchedName);
        if (await _patcher.HasKeyAsync(original, "$envmap", cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        (string Name, string Var)? dependent = await FindDependentMaterialAsync(original, cancellationToken)
            .ConfigureAwait(false);

        return dependent is { } d && await UsesEnvmapAsync(d.Name, depth + 1, cancellationToken).ConfigureAwait(false);
    }

    // Cubemap_InitCubemapSideData's second loop.
    private bool[] ManuallyPicked()
    {
        bool[] manual = new bool[_map.BrushSideCount];
        foreach (List<int> sides in _sampleSides)
        {
            foreach (int id in sides)
            {
                int index = _map.SideIdToIndex(id);
                if (index >= 0)
                {
                    manual[index] = true;
                }
            }
        }

        return manual;
    }

    /// <summary>
    /// <c>strtok(" ")</c> then <c>sscanf("%d")</c>: each space-separated
    /// token's leading integer, a token with none skipped
    /// </summary>
    /// <param name="sides">The <c>sides</c> key.</param>
    /// <returns>The ids, in order.</returns>
    public static List<int> ParseSideList(string sides)
    {
        ArgumentNullException.ThrowIfNull(sides);

        List<int> ids = [];
        foreach (string token in sides.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryScanInt(token, out int id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static bool TryScanInt(string token, out int value)
    {
        // sscanf %d: optional whitespace (tabs survive strtok(" ")), sign, digits.
        int i = 0;
        while (i < token.Length && char.IsWhiteSpace(token[i]))
        {
            i++;
        }

        int start = i;
        if (i < token.Length && (token[i] == '+' || token[i] == '-'))
        {
            i++;
        }

        int digits = i;
        while (i < token.Length && char.IsAsciiDigit(token[i]))
        {
            i++;
        }

        if (i == digits)
        {
            value = 0;
            return false;
        }

        // Out-of-range input is undefined for sscanf; clamp rather than throw.
        value = long.TryParse(token[start..i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v)
            ? (int)Math.Clamp(v, int.MinValue, int.MaxValue)
            : 0;
        return true;
    }
}
