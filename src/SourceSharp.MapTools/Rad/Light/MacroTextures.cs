//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>One macro texture, decoded to RGBA8888: <c>CMacroTextureData</c>.</summary>
/// <param name="Width">Texels across.</param>
/// <param name="Height">Texels down.</param>
/// <param name="Rgba">Four bytes per texel, row-major, mip 0 of frame 0.</param>
public sealed record MacroTexture(int Width, int Height, byte[] Rgba);

/// <summary>
/// The whole-map and per-material "macro" textures
/// <c>FinalLightFace</c> multiplies into every luxel.
/// </summary>
/// <remarks>
/// <para>
/// A macro texture is a top-down image stretched over the map's
/// <c>world_mins</c>/<c>world_maxs</c> box (worldspawn keys) and multiplied into
/// the lightmap by X/Y position (<c>ApplyMacroTextures</c>). There
/// are two: a global one at <c>materials/macro/&lt;map&gt;/base.vtf</c>, and one
/// per face named by LUMP_FACE_MACRO_TEXTURE_INFO. Loaded here in the stage's
/// async load phase; <see cref="Apply"/> is the pure half 4f calls per luxel.
/// </para>
/// <para>
/// Stock opens the PER-FACE file at <c>gamedir + "materials/" + name +
/// ".vtf"</c> -- an absolute path into the mod directory, not a search-path
/// lookup. The global one IS a search-path lookup. The content
/// system passed here decides both; a host wanting stock's exact reach passes
/// one mounted on the mod directory alone for the per-face files.
/// </para>
/// </remarks>
public sealed class MacroTextures
{
    private readonly MacroTexture?[] _faceTextures;

    private MacroTextures(Vec3 worldMins, Vec3 worldMaxs, MacroTexture? global, MacroTexture?[] faceTextures)
    {
        WorldMins = worldMins;
        WorldMaxs = worldMaxs;
        Global = global;
        _faceTextures = faceTextures;
    }

    /// <summary>A map with no macro textures at all.</summary>
    public static MacroTextures None { get; } = new(Vec3.Zero, Vec3.Zero, null, []);

    /// <summary><c>g_MacroWorldMins</c>.</summary>
    public Vec3 WorldMins { get; }

    /// <summary><c>g_MacroWorldMaxs</c>.</summary>
    public Vec3 WorldMaxs { get; }

    /// <summary><c>g_pGlobalMacroTextureData</c>, or null.</summary>
    public MacroTexture? Global { get; }

    /// <summary>A face's own macro texture, or null.</summary>
    /// <param name="faceNum">The face.</param>
    /// <returns>The texture.</returns>
    public MacroTexture? ForFace(int faceNum) =>
        (uint)faceNum < (uint)_faceTextures.Length ? _faceTextures[faceNum] : null;

    /// <summary>
    /// <c>InitMacroTexture</c>.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="geometry">Its texdata strings.</param>
    /// <param name="entities">Its entities, for worldspawn's bounds.</param>
    /// <param name="mapName">The map's file base name (<c>Q_FileBase(source)</c>).</param>
    /// <param name="content">Where the textures are read from.</param>
    /// <param name="warnings">Receives stock's warnings.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The textures.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidVtfException">
    /// A file exists and is not a VTF; stock's <c>Error</c>.
    /// </exception>
    public static async Task<MacroTextures> LoadAsync(
        BspData bsp,
        LightGeometry geometry,
        IReadOnlyList<BspEntity> entities,
        string mapName,
        IContentFileSystem content,
        IList<string> warnings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(mapName);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(warnings);

        // The FIRST worldspawn, by exact classname.
        BspEntity? world = null;
        foreach (BspEntity e in entities)
        {
            if (string.Equals(EntityKeys.ValueForKey(e, "classname"), "worldspawn", StringComparison.Ordinal))
            {
                world = e;
                break;
            }
        }

        if (world is null)
        {
            warnings.Add("MaskOnMacroTexture: can't find worldspawn");
            return None;
        }

        Vec3 mins = EntityKeys.GetVectorForKey(world, "world_mins");
        Vec3 maxs = EntityKeys.GetVectorForKey(world, "world_maxs");

        MacroTexture? global = await LoadFileAsync(
            content, $"materials/macro/{mapName}/base.vtf", cancellationToken).ConfigureAwait(false);

        // One load per distinct file name.
        ReadOnlySpan<FaceMacroTextureInfo> infos =
            BspStructView.As<FaceMacroTextureInfo>(bsp[BspLump.FaceMacroTextureInfo]);
        ushort[] ids = new ushort[infos.Length];
        for (int i = 0; i < infos.Length; i++)
        {
            ids[i] = infos[i].MacroTextureNameId;
        }

        MacroTexture?[] perFace = new MacroTexture?[geometry.Faces.Length];
        Dictionary<string, MacroTexture?> lookup = new(StringComparer.Ordinal);
        for (int face = 0; face < perFace.Length && face < ids.Length; face++)
        {
            if (ids[face] == 0xFFFF)
            {
                continue;
            }

            string file = $"materials/{geometry.TexDataName(ids[face])}.vtf";
            if (!lookup.TryGetValue(file, out MacroTexture? texture))
            {
                texture = await LoadFileAsync(content, file, cancellationToken).ConfigureAwait(false);

                // Only a file that LOADED goes in the dictionary, so
                // a missing one is looked up again for every face naming it.
                if (texture is not null)
                {
                    lookup[file] = texture;
                }
            }

            perFace[face] = texture;
        }

        return new MacroTextures(mins, maxs, global, perFace);
    }

    /// <summary>
    /// <c>SampleMacroTexture</c>: the texel over a
    /// world position, as 0..1 colour.
    /// </summary>
    /// <param name="texture">The texture.</param>
    /// <param name="worldPos">The luxel's world position.</param>
    /// <returns>The colour.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="texture"/> is null.</exception>
    /// <remarks>
    /// x maps across the width, y down the height FLIPPED (<c>height - 1 -
    /// iy</c>), each through <c>RemapVal</c> onto <c>[0, size - 0.00001]</c> --
    /// a double narrowed into the float parameter -- truncated to int and
    /// clamped. z is ignored.
    /// </remarks>
    public Vec3 Sample(MacroTexture texture, Vec3 worldPos)
    {
        ArgumentNullException.ThrowIfNull(texture);

        int ix = (int)RemapVal(worldPos.X, WorldMins.X, WorldMaxs.X, 0, (float)(texture.Width - 0.00001));
        int iy = (int)RemapVal(worldPos.Y, WorldMins.Y, WorldMaxs.Y, 0, (float)(texture.Height - 0.00001));
        ix = Math.Clamp(ix, 0, texture.Width - 1);
        iy = texture.Height - 1 - Math.Clamp(iy, 0, texture.Height - 1);

        int at = ((iy * texture.Width) + ix) * 4;
        return new Vec3(
            (float)(texture.Rgba[at] / 255.0),
            (float)(texture.Rgba[at + 1] / 255.0),
            (float)(texture.Rgba[at + 2] / 255.0));
    }

    /// <summary>
    /// <c>ApplyMacroTextures</c>: multiplies a
    /// luxel by the global texture and then by the face's own.
    /// </summary>
    /// <param name="faceNum">The face.</param>
    /// <param name="worldPos">The luxel's world position.</param>
    /// <param name="luxel">The light, scaled in place.</param>
    public void Apply(int faceNum, Vec3 worldPos, ref Vec3 luxel)
    {
        if (Global is not null)
        {
            luxel = Multiply(luxel, Sample(Global, worldPos));
        }

        MacroTexture? own = ForFace(faceNum);
        if (own is not null)
        {
            luxel = Multiply(luxel, Sample(own, worldPos));
        }
    }

    /// <summary><c>RemapVal</c>, in float.</summary>
    /// <param name="val">The value.</param>
    /// <param name="a">Input low.</param>
    /// <param name="b">Input high.</param>
    /// <param name="c">Output low.</param>
    /// <param name="d">Output high.</param>
    /// <returns>The remapped value; <c>val &gt;= b ? d : c</c> when the input range is empty.</returns>
    public static float RemapVal(float val, float a, float b, float c, float d)
    {
        if (a == b)
        {
            return val >= b ? d : c;
        }

        return c + ((d - c) * (val - a) / (b - a));
    }

    private static Vec3 Multiply(Vec3 a, Vec3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

    private static async Task<MacroTexture?> LoadFileAsync(
        IContentFileSystem content, string file, CancellationToken cancellationToken)
    {
        using IMemoryOwner<byte>? bytes = await content.ReadAsync(VPath.Create(file), cancellationToken)
            .ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        // Unserialize or Error(); then ConvertImageFormat(RGBA8888).
        VtfFile vtf = VtfFile.Parse(bytes.Memory.ToArray());
        return new MacroTexture(vtf.Width, vtf.Height, vtf.DecodeToRgba8888(mip: 0, frame: 0, face: 0));
    }
}
