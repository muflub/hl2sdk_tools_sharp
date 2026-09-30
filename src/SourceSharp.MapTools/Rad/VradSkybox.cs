//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// A 3D skybox that is not in the map being lit, but that the map's sky
/// rays must be recast into as if it were: another map's geometry, moved
/// into this map's frame, and its <c>sky_camera</c>s.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> A room library lights each room alone, sealed, at
/// pack time (the rooms design, section 9), but a level carries the
/// library's skybox below its grid (4.12), and vrad of the level recasts
/// every sky ray that reaches a sky face into that skybox: a ray from a
/// sample at <c>p</c> that leaves through the sky continues from
/// <c>camera + p / scale</c> in the skybox, and whatever it meets there
/// shadows the sun and the sky ambient too. The room's own map has no
/// skybox and no camera, so without this its bake saw an empty sky where
/// the level sees the skybox's geometry.
/// </para>
/// <para>
/// <b>What a run does with it.</b> The skybox map's shadow casters are
/// loaded as vrad loads any map's (brushes, sky faces, displacements,
/// static props, with the run's own switches and <c>noshadow</c> list),
/// each vertex moved by <see cref="Move"/>, and appended to the map's own
/// casters, so the tracer the run builds holds both; its static props'
/// trace ids are renumbered from <see cref="PropIndexBase"/>, past every
/// prop of the lit map, so a prop that skips itself never skips a skybox
/// prop by accident. The cameras join the map's own
/// (<see cref="SkyCameras"/>) standing in no area, so every area of the
/// lit map recasts into them, as every area of a level but the skybox's
/// own does. Everything else in the run is the lit map's alone.
/// </para>
/// <para>
/// <b>The move is exact.</b> <see cref="Move"/> adds a translation and
/// then applies a quarter turn about +z (<see cref="BakeFrame.ToRoom(Vec3, int)"/>),
/// so for the whole-unit offsets a level's grid places things at, the
/// skybox's triangles land where a level's own compile would put them
/// relative to the room, bit for bit. The camera's origin is given
/// already moved, because the recast scales the sample's position by
/// <c>1 / scale</c> about the frame's origin, and a room lit in its own
/// frame needs a camera shifted by its placement's turned offset over the
/// scale for the recast to land where the level's does (the rooms code
/// that makes one says how).
/// </para>
/// <para>
/// Only a room library's bake sets it (<see cref="VradContext.Skybox"/>);
/// <c>ssmap vrad</c> never does, so a map's own compile is unchanged.
/// </para>
/// </remarks>
internal sealed class VradSkybox
{
    /// <summary>Makes the skybox a run recasts into.</summary>
    /// <param name="map">The skybox's compiled map, in its own frame; read, never changed.</param>
    /// <param name="offset">What <see cref="Move"/> adds to a skybox point before its turn.</param>
    /// <param name="turns">The quarter turns <see cref="Move"/> takes a point through, as <see cref="BakeFrame.ToRoom(Vec3, int)"/> takes them.</param>
    /// <param name="cameras">The skybox's cameras, moved into the lit map's frame, in the skybox's entity order.</param>
    /// <param name="propIndexBase">The first static prop index the skybox's props are renumbered from.</param>
    public VradSkybox(BspData map, Vec3 offset, int turns, SkyCamera[] cameras, int propIndexBase)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(cameras);
        ArgumentOutOfRangeException.ThrowIfNegative(propIndexBase);
        Map = map;
        Offset = offset;
        Turns = turns & 3;
        Cameras = cameras;
        PropIndexBase = propIndexBase;
    }

    /// <summary>The skybox's compiled map.</summary>
    public BspData Map { get; }

    /// <summary>The translation <see cref="Move"/> adds first.</summary>
    public Vec3 Offset { get; }

    /// <summary>The quarter turns <see cref="Move"/> applies second.</summary>
    public int Turns { get; }

    /// <summary>The cameras, in the lit map's frame, each in no area (-1).</summary>
    public SkyCamera[] Cameras { get; }

    /// <summary>The static prop index the skybox's first prop takes in the lit run's trace ids.</summary>
    public int PropIndexBase { get; }

    /// <summary>A point of the skybox's map in the lit map's frame.</summary>
    /// <param name="point">The point, skybox-local.</param>
    /// <returns>The point where the lit run traces it.</returns>
    public Vec3 Move(Vec3 point) => BakeFrame.ToRoom(point + Offset, Turns);

    /// <summary>
    /// The lit map's casters with the skybox's appended: every skybox
    /// triangle moved, its static prop ids renumbered, its texture-shadow
    /// material dropped (the index names the skybox run's table, not the lit
    /// map's, so a skybox triangle is traced by its coverage alone).
    /// </summary>
    /// <param name="own">The lit map's casters.</param>
    /// <param name="skybox">The skybox's casters, as loaded from <see cref="Map"/>.</param>
    /// <returns>Both, the lit map's first and unchanged.</returns>
    public ShadowCasterSet AppendTo(ShadowCasterSet own, ShadowCasterSet skybox)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(skybox);
        ReadOnlySpan<TracedTriangle> mine = own.Triangles;
        ReadOnlySpan<TracedTriangle> theirs = skybox.Triangles;
        TracedTriangle[] triangles = new TracedTriangle[mine.Length + theirs.Length];
        float[] coverage = new float[triangles.Length];
        int[] materials = new int[triangles.Length];
        mine.CopyTo(triangles);
        own.Coverage.CopyTo(coverage);
        own.MaterialIndices.CopyTo(materials);
        for (int i = 0; i < theirs.Length; i++)
        {
            TracedTriangle t = theirs[i];
            int id = t.Id;
            if (TraceId.IsStaticProp(id))
            {
                int index = checked(PropIndexBase + TraceId.PropIndex(id));
                if (index > TraceId.PropIndexMask)
                {
                    throw new InvalidOperationException($"the skybox's static prop {TraceId.PropIndex(id)} would be prop {index} of the lit run, past what a trace id holds");
                }

                id = (id & ~TraceId.PropIndexMask) | index;
            }

            triangles[mine.Length + i] = new TracedTriangle(id, Move(t.V0), Move(t.V1), Move(t.V2), t.Flags);
            coverage[mine.Length + i] = skybox.Coverage[i];
            materials[mine.Length + i] = -1;
        }

        return own.WithTriangles(triangles, coverage, materials);
    }
}
