//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rad.Final;

/// <summary>
/// Everything <see cref="FinalLightFace"/> reads, for one pass: the lit world
/// and the switches stock keeps in globals (<c>do_fast</c>, <c>numbounce</c>,
/// <c>bRed2Black</c>).
/// </summary>
public sealed class FinalLightContext
{
    private readonly FaceLightInfo?[] _infos;

    /// <summary>Creates the context.</summary>
    /// <param name="world">The pass, after <see cref="RadWorld.LightFacesAsync"/>.</param>
    /// <param name="macroTextures">The macro textures, or <see cref="MacroTextures.None"/>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The world has no lightmap layout yet.</exception>
    public FinalLightContext(RadWorld world, MacroTextures macroTextures)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(macroTextures);

        if (world.Layout is null)
        {
            throw new ArgumentException("light the faces first: the world has no lightmap layout", nameof(world));
        }

        World = world;
        MacroTextures = macroTextures;
        _infos = new FaceLightInfo?[world.Geometry.Faces.Length];
    }

    /// <summary>The pass.</summary>
    public RadWorld World { get; }

    /// <summary>The layout the bytes are written into.</summary>
    public LightmapLayout Layout => World.Layout!;

    /// <summary><c>ApplyMacroTextures</c>' textures.</summary>
    public MacroTextures MacroTextures { get; }

    /// <summary><c>do_fast</c>: no radial filter, each luxel takes its own sample.</summary>
    public bool Fast => World.Settings.Fast;

    /// <summary><c>numbounce</c>: above zero, style 0 also gets the bounced patch light.</summary>
    public int Bounces => World.Settings.Bounces;

    /// <summary><c>!bRed2Black</c> (<c>-rederrors</c>).</summary>
    public bool RedErrors { get; init; }

    /// <summary>The policy.</summary>
    public ComplianceOptions Compliance => World.Settings.Compliance;

    /// <summary>
    /// What the displacement radials read (<see cref="RadWorld.DisplacementRadialContext"/>,
    /// after <see cref="RadWorld.BuildDisplacementHashAsync"/>), or null for a map
    /// with no displacement faces. A displacement face finished without it is
    /// reported, not written.
    /// </summary>
    public Displacement.DispRadialContext? Displacements { get; init; }

    /// <summary>
    /// <c>InitLightinfo</c> for any face, built once
    /// and shared by every radial that reads it.
    /// </summary>
    /// <param name="faceNum">The face.</param>
    /// <returns>Its luxel frame.</returns>
    /// <remarks>
    /// Built on first use. Two workers may race to build the same face; both
    /// build the identical immutable object from the same inputs, so whichever
    /// store wins is the same answer.
    /// </remarks>
    public FaceLightInfo Info(int faceNum)
    {
        FaceLightInfo? info = Volatile.Read(ref _infos[faceNum]);
        if (info is null)
        {
            info = FaceLightInfo.Build(
                World.Geometry,
                World.Neighbours,
                faceNum,
                World.Patches.FaceOffsets[faceNum],
                World.Settings.SmoothingThreshold);
            Volatile.Write(ref _infos[faceNum], info);
        }

        return info;
    }
}
