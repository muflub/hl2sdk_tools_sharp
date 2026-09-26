//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Materials;

/// <summary>
/// What a material contributes to a brush side: <c>textureref_t</c>'s
/// <c>flags</c> and <c>contents</c>.
/// </summary>
/// <param name="Flags">
/// The <c>SURF_*</c> bits. copies these to the side
/// and copies the side's straight into
/// <c>texinfo_t::flags</c>, with nothing else in vbsp touching a
/// <c>SURF_</c> bit — so this value IS what lands in the BSP's TEXINFO lump.
/// </param>
/// <param name="Contents">
/// The <c>CONTENTS_*</c> bits. assigns these to the
/// side, where the brush's own contents are then folded in — so unlike the
/// flags these are a contribution rather than the final value.
/// </param>
public readonly record struct MaterialSurface(SurfaceFlags Flags, BrushContents Contents);

/// <summary>
/// The command-line switches that change how a material is classified.
/// </summary>
/// <remarks>
/// Four of them, and every default is <c>false</c>
/// — which is what a map compiled
/// without options was built with.
/// </remarks>
public sealed record MaterialCompileOptions
{
    /// <summary>A compile with no relevant switches given.</summary>
    public static MaterialCompileOptions Default { get; } = new();

    /// <summary>
    /// <c>-bumpall</c>: every material gets <c>SURF_BUMPLIGHT</c>
    /// </summary>
    /// <remarks>
    /// Clears it again for a low-end compile, so "the switch was
    /// given" and "the flag was on" are not the same thing.
    /// </remarks>
    public bool BumpAll { get; init; }

    /// <summary>
    /// <c>-lightifmissing</c>: a material whose shader wants no lightmap keeps
    /// its lighting instead of gaining <c>SURF_NOLIGHT</c>
    /// </summary>
    public bool LightIfMissing { get; init; }

    /// <summary>
    /// <c>-nodrawtriggers</c>: a trigger material also gets
    /// <c>SURF_NODRAW</c>.
    /// </summary>
    public bool NodrawTriggers { get; init; }

    /// <summary>
    /// <c>-nowater</c>'s companion: water that is not
    /// <c>%compileKeepLight</c> gains <c>SURF_NOLIGHT</c>
    /// </summary>
    public bool DisableWaterLighting { get; init; }
}

/// <summary>
/// <c>FindMiptex</c>, as a
/// function of a material's facts.
/// </summary>
/// <remarks>
/// <para>
/// The reference surface chain is one long if/else-if chain over the compile variables followed by
/// a block of independent tests, and the SHAPE is the behaviour: the first
/// eleven tests are mutually exclusive, so a material that is both
/// <c>%compileSky</c> and <c>%compileNoDraw</c> is only a sky. Reordering them
/// into something tidier would change which flags a real material gets, so
/// they are in the reference order, with its branches.
/// </para>
/// <para>
/// Pure: it reads no file. Everything it needs is already in the
/// <see cref="MaterialFacts"/>, which is the point of loading those first.
/// </para>
/// </remarks>
public static class MaterialSurfaceClassifier
{
    /// <summary>
    /// Classifies a material the way <c>FindMiptex</c> does.
    /// </summary>
    /// <param name="facts">The material's facts.</param>
    /// <param name="options">The compile's switches.</param>
    /// <returns>The flags and contents the material contributes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="facts"/> is null.</exception>
    public static MaterialSurface Classify(
        MaterialFacts facts,
        MaterialCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(facts);

        MaterialCompileOptions settings = options ?? MaterialCompileOptions.Default;

        if (!facts.Found)
        {
            // FindOriginalMaterial returned
            // MATERIAL_NOT_FOUND, so FindMiptex returns index 0 with the
            // zeroed textureref it had just initialised.
            return default;
        }

        SurfaceFlags flags = SurfaceFlags.None;
        BrushContents contents = BrushContents.Empty;
        MaterialCompileFlags compile = facts.CompileFlags;

        bool Has(MaterialCompileFlags flag) => (compile & flag) != 0;

        // -- ONE branch of this chain runs, no more.
        if (Has(MaterialCompileFlags.Sky))
        {
            flags |= SurfaceFlags.Sky | SurfaceFlags.NoLight;
        }
        else if (Has(MaterialCompileFlags.Sky2D))
        {
            flags |= SurfaceFlags.Sky | SurfaceFlags.Sky2D | SurfaceFlags.NoLight;
        }
        else if (Has(MaterialCompileFlags.Hint))
        {
            flags |= SurfaceFlags.NoDraw | SurfaceFlags.NoLight | SurfaceFlags.Hint;
        }
        else if (Has(MaterialCompileFlags.Skip))
        {
            flags |= SurfaceFlags.NoDraw | SurfaceFlags.NoLight | SurfaceFlags.Skip;
        }
        else if (Has(MaterialCompileFlags.Origin))
        {
            contents |= BrushContents.Origin | BrushContents.Detail;
            flags |= SurfaceFlags.NoDraw | SurfaceFlags.NoLight;
        }
        else if (Has(MaterialCompileFlags.Clip))
        {
            contents |= BrushContents.PlayerClip | BrushContents.MonsterClip;
            flags |= SurfaceFlags.NoDraw | SurfaceFlags.NoLight;
        }
        else if (Has(MaterialCompileFlags.PlayerClip))
        {
            contents |= BrushContents.PlayerClip;
            flags |= SurfaceFlags.NoDraw | SurfaceFlags.NoLight;
        }
        else if (Has(MaterialCompileFlags.NpcClip))
        {
            contents |= BrushContents.MonsterClip;
            flags |= SurfaceFlags.NoDraw | SurfaceFlags.NoLight;
        }
        else if (Has(MaterialCompileFlags.NoChop))
        {
            flags |= SurfaceFlags.NoChop;
        }
        else if (Has(MaterialCompileFlags.Trigger))
        {
            flags |= SurfaceFlags.NoLight | SurfaceFlags.Trigger;

            if (settings.NodrawTriggers)
            {
                flags |= SurfaceFlags.NoDraw;
            }
        }
        else if (Has(MaterialCompileFlags.NoLight) && !Has(MaterialCompileFlags.Water))
        {
            // %compileNoLight is ignored on water, which
            // has its own lighting rule further down.
            flags |= SurfaceFlags.NoLight;
        }
        else
        {
            (flags, contents) = ClassifyRendered(facts, settings, flags, contents);
        }

        return new MaterialSurface(flags, contents);
    }

    private static (SurfaceFlags Flags, BrushContents Contents) ClassifyRendered(
        MaterialFacts facts,
        MaterialCompileOptions settings,
        SurfaceFlags flags,
        BrushContents contents)
    {
        MaterialCompileFlags compile = facts.CompileFlags;

        bool Has(MaterialCompileFlags flag) => (compile & flag) != 0;

        // -- independent tests, in order.
        if (Has(MaterialCompileFlags.Ladder))
        {
            contents |= BrushContents.Ladder;
        }

        if (Has(MaterialCompileFlags.NoPortal))
        {
            flags |= SurfaceFlags.NoPortal;
        }

        if (Has(MaterialCompileFlags.PassBullets))
        {
            contents &= ~BrushContents.Solid;
            contents |= BrushContents.Grate;
        }

        if (settings.BumpAll || facts.NeedsBumpedLightmaps)
        {
            flags |= SurfaceFlags.BumpLight;
        }

        if (facts.NeedsLightmap)
        {
            flags &= ~SurfaceFlags.NoLight;
        }
        else if (!settings.LightIfMissing)
        {
            flags |= SurfaceFlags.NoLight;
        }

        if (Has(MaterialCompileFlags.NoDraw))
        {
            flags |= SurfaceFlags.NoDraw | SurfaceFlags.NoLight;
        }

        if (Has(MaterialCompileFlags.Invisible))
        {
            contents &= ~BrushContents.Solid;
            contents |= BrushContents.Grate;
            flags |= SurfaceFlags.NoDraw | SurfaceFlags.NoLight;
        }

        bool checkWindow = true;

        if (Has(MaterialCompileFlags.NonSolid))
        {
            // Assignment, not an OR: everything set above is discarded.
            contents = BrushContents.Opaque;
            checkWindow = false;
        }

        if (Has(MaterialCompileFlags.BlockLos))
        {
            // Also an assignment, so %compileBlockLOS beats %compileNonsolid
            // when a material sets both.
            contents = BrushContents.BlockLos;
            checkWindow = false;
        }

        if (Has(MaterialCompileFlags.Detail))
        {
            contents |= BrushContents.Detail;
        }

        bool keepLighting = Has(MaterialCompileFlags.KeepLight);

        if (Has(MaterialCompileFlags.Water))
        {
            contents &= ~(BrushContents.Solid | BrushContents.Detail);
            contents |= BrushContents.Water;
            flags |= SurfaceFlags.Warp | SurfaceFlags.NoShadows | SurfaceFlags.NoDecals;

            if (settings.DisableWaterLighting && !keepLighting)
            {
                flags |= SurfaceFlags.NoLight;
            }
        }

        // Precedence and all. The reference expression is
        //   !bKeepLighting && water5 || unlit12
        // and && binds tighter than ||, so %compileKeepLight does NOT protect
        // an UnlitGeneric material -- only a water one. Written out here so
        // the asymmetry is visible rather than inherited from an operator
        // table.
        string shader = facts.ShaderName;

        if ((!keepLighting && StartsWith(shader, "water")) || StartsWith(shader, "UnlitGeneric"))
        {
            flags |= SurfaceFlags.NoLight;
        }

        if (Has(MaterialCompileFlags.Slime))
        {
            contents &= ~(BrushContents.Solid | BrushContents.Detail);
            contents |= BrushContents.Slime;
            flags |= SurfaceFlags.NoDecals;
        }

        if (checkWindow && facts.Opacity != MaterialOpacity.Opaque)
        {
            if ((contents & (BrushContents.Grate | BrushContents.Water)) == 0)
            {
                contents |= BrushContents.Window;
            }

            contents &= ~BrushContents.Solid;

            if (facts.Opacity == MaterialOpacity.Translucent)
            {
                flags |= SurfaceFlags.Trans;
            }
        }

        if ((flags & SurfaceFlags.NoLight) != 0)
        {
            flags &= ~SurfaceFlags.BumpLight;
        }

        return (flags, contents);
    }

    private static bool StartsWith(string shader, string prefix) =>
        shader.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
