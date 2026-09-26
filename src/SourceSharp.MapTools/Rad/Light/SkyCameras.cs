//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>One <c>sky_camera</c>: <c>sky_camera_t</c>.</summary>
/// <param name="Origin">The camera's origin, inside the 3D skybox.</param>
/// <param name="SkyToWorld">The entity's <c>scale</c>.</param>
/// <param name="WorldToSky"><c>1.0f / scale</c>.</param>
/// <param name="Area">The area the camera stands in, or -1.</param>
public readonly record struct SkyCamera(Vec3 Origin, float SkyToWorld, float WorldToSky, int Area);

/// <summary>
/// <c>ProcessSkyCameras</c>: the 3D skyboxes a sky ray
/// may continue into.
/// </summary>
/// <remarks>
/// <para>
/// A sky ray from a sample that hits a sky face has seen the 2D sky. If the
/// sample's area has NO sky camera of its own, and the map has any, the ray
/// is re-cast from inside each 3D skybox -- start scaled by the camera's
/// <c>1/scale</c> about its origin -- and whatever it hits there occludes the
/// sun too. An area that holds a camera is the
/// skybox itself and does not recurse.
/// </para>
/// </remarks>
public sealed class SkyCameras
{
    private readonly SkyCamera[] _cameras;
    private readonly int[] _areaCamera;

    private SkyCameras(SkyCamera[] cameras, int[] areaCamera)
    {
        _cameras = cameras;
        _areaCamera = areaCamera;
    }

    /// <summary>The cameras, in entity order.</summary>
    public ReadOnlySpan<SkyCamera> Cameras => _cameras;

    /// <summary>A map with no sky cameras.</summary>
    public static SkyCameras None { get; } = new([], []);

    /// <summary>
    /// <c>area_sky_cameras[area]</c>: the camera standing in an area, or -1.
    /// </summary>
    /// <param name="area">The area.</param>
    /// <returns>The camera index, or -1 (also for an area out of range).</returns>
    public int CameraInArea(int area) =>
        (uint)area < (uint)_areaCamera.Length ? _areaCamera[area] : -1;

    /// <summary>The number of areas the table covers.</summary>
    public int AreaCount => _areaCamera.Length;

    /// <summary>Builds the table.</summary>
    /// <param name="entities">The map's entities, in file order.</param>
    /// <param name="tree">The map's BSP, for <c>PointLeafnum</c>.</param>
    /// <param name="leaves">The leaves, for their areas.</param>
    /// <param name="areaCount"><c>numareas</c>.</param>
    /// <returns>The cameras.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// The classname test is <c>stricmp</c> -- case-insensitive, unlike the
    /// light entities' <c>strncmp</c>. A camera with <c>scale &lt;= 0</c> is
    /// ignored outright. When two cameras share an area
    /// the LATER one wins the area slot, but both still recurse from every
    /// camera-less area.
    /// </remarks>
    public static SkyCameras Build(
        IReadOnlyList<BspEntity> entities,
        CompiledBspTree tree,
        ReadOnlySpan<LeafInfo> leaves,
        int areaCount)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(tree);

        int[] areaCamera = new int[Math.Max(areaCount, 0)];
        Array.Fill(areaCamera, -1);
        List<SkyCamera> cameras = [];

        foreach (BspEntity entity in entities)
        {
            string name = EntityKeys.ValueForKey(entity, "classname");
            if (!string.Equals(name, "sky_camera", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Vec3 origin = EntityKeys.GetVectorForKey(entity, "origin");
            int leaf = tree.LeafFromPoint(origin);
            int area = leaf >= 0 && leaf < leaves.Length ? leaves[leaf].Area : -1;
            float scale = EntityKeys.FloatForKey(entity, "scale");

            if (scale > 0.0f)
            {
                if (area >= 0 && area < areaCamera.Length)
                {
                    areaCamera[area] = cameras.Count;
                }

                cameras.Add(new SkyCamera(origin, scale, 1.0f / scale, area));
            }
        }

        return new SkyCameras([.. cameras], areaCamera);
    }
}
