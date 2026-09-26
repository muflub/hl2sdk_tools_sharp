//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// <c>TestLine</c> as leaf ambient uses it: how much of
/// the segment from a sample to a surface light is unobstructed.
/// </summary>
/// <remarks>
/// A seam rather than a type because the answer comes from <c>g_RtEnv</c> --
/// brushes, displacements and static props in the KD-tree -- which the direct
/// lighting lane owns. Implementations must be safe to call from many workers
/// at once and must not depend on which worker calls.
/// </remarks>
public interface IAmbientLightVisibility
{
    /// <summary>
    /// For each end, the fraction of the segment from <paramref name="start"/>
    /// that is unobstructed: 0 or 1, or between with texture shadows.
    /// </summary>
    /// <param name="start">The sample.</param>
    /// <param name="ends">The lights.</param>
    /// <param name="fractions">Receives one fraction per end.</param>
    /// <remarks>
    /// A batch because a sample tests every baked light at once; each segment's
    /// answer must not depend on the others in the batch.
    /// </remarks>
    void FractionsVisible(Vec3 start, ReadOnlySpan<Vec3> ends, Span<float> fractions);
}

/// <summary>
/// One work item's ambient-cube computer: <c>ComputeAmbientFromSphericalSamples</c>
/// With every buffer it needs owned here.
/// </summary>
/// <remarks>
/// <para>
/// ONE PER WORK ITEM, never shared between workers: it owns the 162-colour
/// scratch and the <see cref="DispTestedScratch"/> that replaces stock's
/// per-thread <c>s_DispTested[iThread]</c>. Nothing it holds survives from one
/// sample to the next except buffers that every sample fully overwrites, so
/// reusing one instance across a leaf's samples (or across leaves) cannot
/// change an answer.
/// </para>
/// </remarks>
public sealed class AmbientSampler
{
    private readonly AmbientScene _scene;
    private readonly DWorldLight[] _lights;
    private readonly IAmbientLightVisibility? _visibility;
    private readonly ComplianceOptions _compliance;
    private readonly Vec3? _skyAmbient;
    private readonly float _tanTheta;
    private readonly Vec3[] _radColor = new Vec3[VertexNormals.Count];
    private readonly Vec3[] _styleColors = new Vec3[RayAmbientLighting.MaxLightStyles];

    /// <summary>Makes a sampler.</summary>
    /// <param name="scene">The map.</param>
    /// <param name="lights">The classified world lights (not copied; read only).</param>
    /// <param name="visibility">The surface-light visibility, or null.</param>
    /// <param name="compliance">Which defects to reproduce.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public AmbientSampler(
        AmbientScene scene,
        DWorldLight[] lights,
        IAmbientLightVisibility? visibility,
        ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(lights);
        ArgumentNullException.ThrowIfNull(compliance);
        _scene = scene;
        _lights = lights;
        _visibility = visibility;
        _compliance = compliance;
        _skyAmbient = RayAmbientLighting.FindSkyAmbient(scene);

        // Tan(DEG2RAD(7.275)), all float.
        _tanTheta = MathF.Tan(VertexNormals.ConeInnerAngleRadians);
        Displacements = scene.Tracer.Displacements.CreateScratch();

        // The flagged set is fixed once the pass has classified its lights.
        List<int> flagged = [];
        for (int i = 0; i < lights.Length; i++)
        {
            if ((lights[i].Flags & (int)WorldLightFlags.InAmbientCube) != 0)
            {
                flagged.Add(i);
            }
        }

        _flagged = [.. flagged];
        _flaggedOrigins = new Vec3[_flagged.Length];
        for (int i = 0; i < _flagged.Length; i++)
        {
            _flaggedOrigins[i] = lights[_flagged[i]].Origin;
        }

        _fractions = new float[_flagged.Length];
    }

    private readonly int[] _flagged;
    private readonly Vec3[] _flaggedOrigins;
    private readonly float[] _fractions;

    /// <summary>This work item's displacement scratch.</summary>
    public DispTestedScratch Displacements { get; }

    /// <summary>
    /// Builds one ambient cube at a point
    /// (<c>ComputeAmbientFromSphericalSamples</c>).
    /// </summary>
    /// <param name="start">Where to sample.</param>
    /// <param name="cube">Receives the six colours.</param>
    /// <remarks>
    /// ONLY LIGHTSTYLE 0: stock clears element 0 of a 64-wide per-ray array,
    /// fires the ray into it, and reads element 0. The other
    /// styles are written by faces with switchable styles and never read.
    /// </remarks>
    public void ComputeCube(Vec3 start, Span<Vec3> cube)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cube.Length, AmbientCube.Sides);

        ReadOnlySpan<Vec3> anorms = VertexNormals.All;
        Span<Vec3> styles = _styleColors;
        for (int i = 0; i < VertexNormals.Count; i++)
        {
            Vec3 end = start + (anorms[i] * AmbientCube.RayLength);

            styles[0] = Vec3.Zero;
            RayAmbientLighting.Accumulate(_scene, start, end, _tanTheta, _skyAmbient, styles, Displacements);
            _radColor[i] = styles[0];
        }

        AmbientCube.Project(_radColor, cube);

        if (_flagged.Length > 0)
        {
            if (_visibility is null)
            {
                Array.Fill(_fractions, 1.0f);
            }
            else
            {
                _visibility.FractionsVisible(start, _flaggedOrigins, _fractions);
            }

            AmbientCube.AddEmitSurfaceLights(_lights, _flagged, _fractions, start, cube, _compliance);
        }
    }
}
