using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rad.Displacement;

/// <summary>
/// A displacement face's radial accumulation: stock's <c>radial_t</c>
/// (<c>radial.h:37</c>), per luxel a weight and, per bump normal, a weighted
/// light sum.
/// </summary>
public sealed class DispRadialMap
{
    /// <summary>Creates an empty map for a face (<c>AllocateRadial</c>, <c>radial.cpp:259</c>).</summary>
    /// <param name="faceNum">The face.</param>
    /// <param name="width">Luxels across.</param>
    /// <param name="height">Luxels down.</param>
    public DispRadialMap(int faceNum, int width, int height)
    {
        FaceNum = faceNum;
        Width = width;
        Height = height;
        Weight = new float[width * height];
        Light = new LightingValue[BumpBasis.LightmapCount][];
        for (int b = 0; b < Light.Length; b++)
        {
            Light[b] = new LightingValue[width * height];
        }
    }

    /// <summary><c>facenum</c>.</summary>
    public int FaceNum { get; }

    /// <summary><c>w</c>.</summary>
    public int Width { get; }

    /// <summary><c>h</c>.</summary>
    public int Height { get; }

    /// <summary><c>weight[]</c>, one per luxel.</summary>
    public float[] Weight { get; }

    /// <summary><c>light[bump][luxel]</c>.</summary>
    public LightingValue[][] Light { get; }
}

/// <summary>
/// The radial filter for displacement faces: <c>CVRadDispMgr::BuildLuxelRadial</c>,
/// <c>BuildPatchRadial</c> and <c>SampleRadial</c> (<c>vraddisps.cpp:831-1420</c>),
/// which <c>FinalLightFace</c> (<c>radial.cpp:706-780</c>) calls for a
/// displacement in place of the brush-face radial.
/// </summary>
/// <remarks>
/// <para>
/// A brush face's radial spreads each sample over the luxels of its own
/// lightmap grid. A displacement's cannot -- its samples are on a curved
/// surface -- so it works in WORLD space: every luxel gathers every sample of
/// its own face and of the face's neighbours (<c>faceneighbor</c>) within
/// <c>sqrt(SampleRadius2)</c>, found through the
/// <see cref="DispSampleHash.Samples"/> voxels, weighted by
/// <c>(1 - d^2 / r^2) * dot(sampleNormal, luxelNormal)</c> and dropped when the
/// normals are more than ~81 degrees apart (dot &lt; 0.15). The patch radial
/// does the same over leaf patches' TOTAL light (bounce).
/// </para>
/// <para>
/// Every accumulation runs in stock's order -- voxels z, y, x; each voxel's
/// items in insertion order -- so the float sums are stock's. The patch
/// radial's "already seen" test uses a per-call set instead of stock's shared
/// <c>m_IterationKey</c> on each patch (<c>samplehash.cpp:159</c>), which is
/// the same answer without writing shared state from a parallel face loop.
/// </para>
/// </remarks>
public static class DispRadial
{
    /// <summary>The luxel radial's normal cut-off, <c>vraddisps.cpp:861</c>.</summary>
    public const float MinSampleAngle = 0.15f;

    /// <summary>A non-bumped neighbour's weight into a bumped luxel, <c>:891</c>.</summary>
    public const float UnbumpedNeighbourScale = 0.05f;

    /// <summary>
    /// <c>BuildLuxelRadial</c> (<c>vraddisps.cpp:1033</c>) +
    /// <c>RadialLuxelBuild</c> (<c>:1007</c>): the direct-light radial of one
    /// light style.
    /// </summary>
    /// <param name="context">The lit world.</param>
    /// <param name="faceNum">The displacement face.</param>
    /// <param name="styleIndex">The style slot of this face.</param>
    /// <param name="bump">Whether the face is bump-lit.</param>
    /// <returns>The radial.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public static DispRadialMap BuildLuxelRadial(DispRadialContext context, int faceNum, int styleIndex, bool bump)
    {
        ArgumentNullException.ThrowIfNull(context);

        FaceLight fl = context.FaceLights[faceNum]!;
        VradDispSurface surface = context.Displacements.ForFace(context.Geometry, faceNum)!;
        DispRadialMap radial = NewRadial(context.Geometry, faceNum);

        float radius2 = surface.SampleRadius2;
        float radius = (float)Math.Sqrt(radius2);
        int lightStyle = fl.Styles[styleIndex];

        int size = radial.Width * radial.Height;
        for (int i = 0; i < size; i++)
        {
            RadialLuxelAddSamples(context, faceNum, fl.Luxels[i], fl.LuxelNormals[i], radius, radial, i, bump, lightStyle);
        }

        return radial;
    }

    private static void RadialLuxelAddSamples(
        DispRadialContext context,
        int faceNum,
        Vec3 luxelPt,
        Vec3 luxelNormal,
        float radius,
        DispRadialMap radial,
        int radialIndex,
        bool bump,
        int lightStyle)
    {
        // :932-944. 1/64 is exact, so the multiply is the divide.
        const float ooVoxelSize = 1.0f / VoxelKey.VoxelSize;
        Span<int> vMin = stackalloc int[3];
        Span<int> vMax = stackalloc int[3];
        for (int axis = 0; axis < 3; axis++)
        {
            vMin[axis] = (int)((luxelPt[axis] - radius) * ooVoxelSize);
            vMax[axis] = (int)((luxelPt[axis] + radius) * ooVoxelSize) + 1;
        }

        Span<LightingValue> sampleLight = stackalloc LightingValue[BumpBasis.LightmapCount];
        LightGeometry geometry = context.Geometry;
        float radius2 = radius * radius;

        // :947-949. Each loop runs to vMax INCLUSIVE.
        for (int z = vMin[2]; z < vMax[2] + 1; z++)
        {
            for (int y = vMin[1]; y < vMax[1] + 1; y++)
            {
                for (int x = vMin[0]; x < vMax[0] + 1; x++)
                {
                    foreach (SampleHandle handle in context.Hash.Samples.Find(new VoxelKey(x, y, z)))
                    {
                        int neighbourFace = handle.Face;
                        if (!IsNeighbour(context.Neighbours, faceNum, neighbourFace))
                        {
                            continue;
                        }

                        FaceLight nfl = context.FaceLights[neighbourFace]!;

                        // :974-987. The neighbour's slot holding this style.
                        int neighbourStyle = -1;
                        for (int k = 0; k < LightConstants.MaxLightmaps; k++)
                        {
                            if (nfl.Styles[k] == lightStyle)
                            {
                                neighbourStyle = k;
                                break;
                            }
                        }

                        if (neighbourStyle == -1)
                        {
                            continue;
                        }

                        bool neighbourBump = IsBumped(geometry, neighbourFace);
                        GetSampleLight(nfl, neighbourStyle, neighbourBump, handle.Sample, sampleLight);
                        ref readonly LightSample s = ref nfl.Samples[handle.Sample];
                        AddSampleLightToRadial(
                            s.Position, s.Normal, sampleLight, radius2, luxelPt, luxelNormal,
                            radial, radialIndex, bump, neighbourBump);
                    }
                }
            }
        }
    }

    /// <summary><c>GetSampleLight</c> (<c>vraddisps.cpp:831</c>).</summary>
    private static void GetSampleLight(FaceLight fl, int styleIndex, bool bumped, int sample, Span<LightingValue> light)
    {
        if (bumped)
        {
            for (int b = 0; b < BumpBasis.LightmapCount; b++)
            {
                light[b] = fl.LightFor(styleIndex, b)![sample];
            }
        }
        else
        {
            light[0] = fl.LightFor(styleIndex, 0)![sample];
        }
    }

    /// <summary>
    /// <c>AddSampleLightToRadial</c> (<c>vraddisps.cpp:855</c>): one sample's
    /// weighted light into one luxel.
    /// </summary>
    /// <param name="samplePos">The sample's position.</param>
    /// <param name="sampleNormal">Its normal.</param>
    /// <param name="sampleLight">Its light, per bump normal (only [0] when not bumped).</param>
    /// <param name="sampleRadius2">The squared influence radius.</param>
    /// <param name="luxelPos">The luxel.</param>
    /// <param name="luxelNormal">Its normal.</param>
    /// <param name="radial">The accumulation.</param>
    /// <param name="radialIndex">The luxel's index.</param>
    /// <param name="bumped">Whether the luxel's face is bumped.</param>
    /// <param name="neighbourBumped">Whether the sample's face is bumped.</param>
    /// <exception cref="ArgumentNullException"><paramref name="radial"/> is null.</exception>
    public static void AddSampleLightToRadial(
        Vec3 samplePos,
        Vec3 sampleNormal,
        ReadOnlySpan<LightingValue> sampleLight,
        float sampleRadius2,
        Vec3 luxelPos,
        Vec3 luxelNormal,
        DispRadialMap radial,
        int radialIndex,
        bool bumped,
        bool neighbourBumped)
    {
        ArgumentNullException.ThrowIfNull(radial);

        float angle = Vec3.Dot(sampleNormal, luxelNormal);
        if (angle < MinSampleAngle)
        {
            return;
        }

        float dist = (samplePos - luxelPos).Length();
        float dist2 = dist * dist;
        float influence = 1.0f - (dist2 / sampleRadius2);
        if (influence <= 0.0f)
        {
            return;
        }

        influence *= angle;

        if (bumped)
        {
            if (neighbourBumped)
            {
                for (int b = 0; b < BumpBasis.LightmapCount; b++)
                {
                    radial.Light[b][radialIndex].AddWeighted(sampleLight[b], influence);
                }
            }
            else
            {
                influence *= UnbumpedNeighbourScale;
                for (int b = 0; b < BumpBasis.LightmapCount; b++)
                {
                    radial.Light[b][radialIndex].AddWeighted(sampleLight[0], influence);
                }
            }

            radial.Weight[radialIndex] += influence;
        }
        else
        {
            radial.Light[0][radialIndex].AddWeighted(sampleLight[0], influence);
            radial.Weight[radialIndex] += influence;
        }
    }

    /// <summary>
    /// <c>BuildPatchRadial</c> (<c>vraddisps.cpp:1391</c>) +
    /// <c>RadialPatchBuild</c> (<c>:1356</c>): the bounced-light radial, from
    /// the leaf patches of the face and its neighbours near its luxels.
    /// </summary>
    /// <param name="context">The lit world.</param>
    /// <param name="faceNum">The displacement face.</param>
    /// <param name="bump">Whether the face is bump-lit.</param>
    /// <returns>The radial.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public static DispRadialMap BuildPatchRadial(DispRadialContext context, int faceNum, bool bump)
    {
        ArgumentNullException.ThrowIfNull(context);

        FaceLight fl = context.FaceLights[faceNum]!;
        VradDispSurface surface = context.Displacements.ForFace(context.Geometry, faceNum)!;
        DispRadialMap radial = NewRadial(context.Geometry, faceNum);

        float radius2 = surface.PatchSampleRadius2;
        float radius = (float)Math.Sqrt(radius2);

        // SAMPLEHASH_QUERY_ONCE (vrad.h:53) is defined: one query per face.
        List<int> interesting = GetInterestingPatchesForLuxels(context, faceNum, radius);

        int size = radial.Width * radial.Height;
        Span<Vec3> patchLight = stackalloc Vec3[BumpBasis.LightmapCount];
        LightGeometry geometry = context.Geometry;
        ref readonly TexInfo tex = ref geometry.TexInfos[geometry.Faces[faceNum].TexInfo];
        for (int i = 0; i < size; i++)
        {
            foreach (int p in interesting)
            {
                ref readonly Patch patch = ref context.Patches.At(p);
                bool neighbourBump = IsBumped(geometry, patch.FaceNumber);
                GetPatchLight(patch, bump, patchLight);
                AddPatchLightToRadial(
                    patch.Origin, patch.Normal, patchLight, radius * radius, fl.Luxels[i], fl.LuxelNormals[i],
                    radial, i, bump, neighbourBump, tex, context.StockNormalise);
            }
        }

        return radial;
    }

    /// <summary>
    /// <c>GetInterestingPatchesForLuxels</c> (<c>vraddisps.cpp:1250</c>): every
    /// leaf patch of the face or a neighbour in a voxel within
    /// <paramref name="radius"/> of a luxel, first-seen order.
    /// </summary>
    /// <param name="context">The lit world.</param>
    /// <param name="faceNum">The face.</param>
    /// <param name="radius">The patch radius.</param>
    /// <returns>Patch indices.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <remarks>
    /// The voxels are marked from each luxel's range with an EXCLUSIVE upper
    /// bound (unlike the sample radial's inclusive one), then visited x, y, z --
    /// x outermost, the reverse of the sample radial's order.
    /// </remarks>
    public static List<int> GetInterestingPatchesForLuxels(DispRadialContext context, int faceNum, float radius)
    {
        ArgumentNullException.ThrowIfNull(context);

        Vec3[] luxels = context.FaceLights[faceNum]!.Luxels;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = -float.MaxValue, maxY = -float.MaxValue, maxZ = -float.MaxValue;
        foreach (Vec3 l in luxels)
        {
            // VectorMin / VectorMax: component-wise, `a < b ? a : b`.
            minX = l.X < minX ? l.X : minX;
            minY = l.Y < minY ? l.Y : minY;
            minZ = l.Z < minZ ? l.Z : minZ;
            maxX = l.X > maxX ? l.X : maxX;
            maxY = l.Y > maxY ? l.Y : maxY;
            maxZ = l.Z > maxZ ? l.Z : maxZ;
        }

        Vec3 lMin = new(minX, minY, minZ);
        Vec3 lMax = new(maxX, maxY, maxZ);
        Span<int> allMin = stackalloc int[3];
        Span<int> allSize = stackalloc int[3];
        for (int axis = 0; axis < 3; axis++)
        {
            allMin[axis] = (int)((lMin[axis] - radius) / VoxelKey.VoxelSize);
            int allMax = (int)((lMax[axis] + radius) / VoxelKey.VoxelSize) + 1;
            allSize[axis] = allMax - allMin[axis];
        }

        bool[] bits = new bool[allSize[0] * allSize[1] * allSize[2]];
        Span<int> vMin = stackalloc int[3];
        Span<int> vMax = stackalloc int[3];
        foreach (Vec3 l in luxels)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                vMin[axis] = (int)((l[axis] - radius) / VoxelKey.VoxelSize);
                vMax[axis] = (int)((l[axis] + radius) / VoxelKey.VoxelSize) + 1;
            }

            for (int x = vMin[0]; x < vMax[0]; x++)
            {
                for (int y = vMin[1]; y < vMax[1]; y++)
                {
                    for (int z = vMin[2]; z < vMax[2]; z++)
                    {
                        int bit = ((z - allMin[2]) * (allSize[0] * allSize[1]))
                            + ((y - allMin[1]) * allSize[0])
                            + (x - allMin[0]);
                        bits[bit] = true;
                    }
                }
            }
        }

        List<int> result = [];
        HashSet<int> seen = [];
        for (int x = 0; x < allSize[0]; x++)
        {
            for (int y = 0; y < allSize[1]; y++)
            {
                for (int z = 0; z < allSize[2]; z++)
                {
                    int bit = (z * (allSize[0] * allSize[1])) + (y * allSize[0]) + x;
                    if (!bits[bit])
                    {
                        continue;
                    }

                    VoxelKey key = new(x + allMin[0], y + allMin[1], z + allMin[2]);
                    foreach (int p in context.Hash.Patches.Find(key))
                    {
                        if (!seen.Add(p))
                        {
                            continue;
                        }

                        if (IsNeighbour(context.Neighbours, faceNum, context.Patches.At(p).FaceNumber))
                        {
                            result.Add(p);
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary><c>GetPatchLight</c> (<c>vraddisps.cpp:1089</c>).</summary>
    private static void GetPatchLight(in Patch patch, bool bump, Span<Vec3> light)
    {
        light[0] = patch.TotalLight.Flat;
        if (bump)
        {
            for (int b = 1; b < BumpBasis.LightmapCount; b++)
            {
                light[b] = patch.TotalLight[b];
            }
        }
    }

    /// <summary>
    /// <c>AddPatchLightToRadial</c> (<c>vraddisps.cpp:1108</c>).
    /// </summary>
    /// <param name="patchOrigin">The patch origin.</param>
    /// <param name="patchNormal">Its normal.</param>
    /// <param name="patchLight">Its total light, per bump normal.</param>
    /// <param name="patchRadius2">The squared radius.</param>
    /// <param name="luxelPos">The luxel.</param>
    /// <param name="luxelNormal">Its normal.</param>
    /// <param name="radial">The accumulation.</param>
    /// <param name="radialIndex">The luxel's index.</param>
    /// <param name="bump">Whether the luxel's face is bumped.</param>
    /// <param name="neighbourBump">Whether the patch's face is bumped.</param>
    /// <param name="tex">The luxel face's texinfo.</param>
    /// <param name="stockNormalise">Whether normalises take stock's estimate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="radial"/> is null.</exception>
    /// <remarks>
    /// On a bumped face the luxel normal first goes through
    /// <c>PreGetBumpNormalsForDisp</c>, which may rotate it into the lightmap
    /// frame; the three bump normals <c>GetBumpNormals</c> then computes are
    /// never read (<c>:1133</c>), so they are not computed here.
    /// </remarks>
    public static void AddPatchLightToRadial(
        Vec3 patchOrigin,
        Vec3 patchNormal,
        ReadOnlySpan<Vec3> patchLight,
        float patchRadius2,
        Vec3 luxelPos,
        Vec3 luxelNormal,
        DispRadialMap radial,
        int radialIndex,
        bool bump,
        bool neighbourBump,
        in TexInfo tex,
        bool stockNormalise)
    {
        ArgumentNullException.ThrowIfNull(radial);

        float dist = (patchOrigin - luxelPos).Length();
        float dist2 = dist * dist;
        float influence = 1.0f - (dist2 / patchRadius2);
        if (influence <= 0.0f)
        {
            return;
        }

        if (bump)
        {
            Vec3 normal = DispBumpBasis.PreGetBumpNormals(tex, luxelNormal, stockNormalise).Normal;
            float scale = MaxMacro(0.0f, Vec3.Dot(patchNormal, normal));
            if (neighbourBump)
            {
                float w = influence * scale;
                for (int b = 0; b < BumpBasis.LightmapCount; b++)
                {
                    radial.Light[b][radialIndex].AddWeighted(new LightingValue(patchLight[b], 0f), w);
                }

                radial.Weight[radialIndex] += w;
            }
            else
            {
                float w = influence * scale * UnbumpedNeighbourScale;
                for (int b = 0; b < BumpBasis.LightmapCount; b++)
                {
                    radial.Light[b][radialIndex].AddWeighted(new LightingValue(patchLight[0], 0f), w);
                }

                radial.Weight[radialIndex] += w;
            }
        }
        else
        {
            float scale = MaxMacro(0.0f, Vec3.Dot(patchNormal, luxelNormal));
            influence *= scale;
            radial.Light[0][radialIndex].AddWeighted(new LightingValue(patchLight[0], 0f), influence);
            radial.Weight[radialIndex] += influence;
        }
    }

    /// <summary>
    /// <c>CVRadDispMgr::SampleRadial</c> (<c>vraddisps.cpp:1058</c>): a luxel's
    /// weighted mean, per bump normal.
    /// </summary>
    /// <param name="radial">The radial.</param>
    /// <param name="luxel">The luxel index.</param>
    /// <param name="light">Receives <paramref name="sampleCount"/> values.</param>
    /// <param name="sampleCount">How many bump normals.</param>
    /// <param name="patch">True for the patch radial, where no weight is not an error.</param>
    /// <returns>False when the luxel's base value had no weight (and this is not the patch radial).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="radial"/> is null.</exception>
    public static bool SampleRadial(DispRadialMap radial, int luxel, Span<LightingValue> light, int sampleCount, bool patch)
    {
        ArgumentNullException.ThrowIfNull(radial);

        bool good = true;
        for (int c = 0; c < sampleCount; c++)
        {
            light[c] = default;
            if (radial.Weight[luxel] > 0.0f)
            {
                light[c].AddWeighted(radial.Light[c][luxel], 1.0f / radial.Weight[luxel]);
            }
            else if (!patch && c == 0)
            {
                good = false;
            }
        }

        return good;
    }

    /// <summary><c>IsNeighbor</c> (<c>vraddisps.cpp:910</c>): the face itself or one of its <c>faceneighbor</c> list.</summary>
    /// <param name="neighbours">PairEdges' output.</param>
    /// <param name="face">The face.</param>
    /// <param name="other">The candidate.</param>
    /// <returns>True when <paramref name="other"/> is the face or a neighbour.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="neighbours"/> is null.</exception>
    public static bool IsNeighbour(FaceNeighbours neighbours, int face, int other)
    {
        ArgumentNullException.ThrowIfNull(neighbours);
        if (face == other)
        {
            return true;
        }

        foreach (int n in neighbours.Neighbours(face))
        {
            if (n == other)
            {
                return true;
            }
        }

        return false;
    }

    // The `max` macro: `a > b ? a : b`.
    private static float MaxMacro(float a, float b) => a > b ? a : b;

    private static bool IsBumped(LightGeometry geometry, int face) =>
        (geometry.TexInfos[geometry.Faces[face].TexInfo].Flags & (int)SurfaceFlags.BumpLight) != 0;

    private static DispRadialMap NewRadial(LightGeometry geometry, int faceNum)
    {
        ref readonly DFace face = ref geometry.Faces[faceNum];
        return new DispRadialMap(faceNum, face.LightmapTextureSizeInLuxels[0] + 1, face.LightmapTextureSizeInLuxels[1] + 1);
    }
}

/// <summary>
/// What <see cref="DispRadial"/> reads: the lit world, after bounce and the
/// sample hash.
/// </summary>
/// <param name="Geometry">The map.</param>
/// <param name="Neighbours">PairEdges' output.</param>
/// <param name="Patches">The patches, with their total light.</param>
/// <param name="FaceLights">Every face's facelight.</param>
/// <param name="Displacements">The displacements.</param>
/// <param name="Hash">The sample and patch hashes.</param>
/// <param name="StockNormalise">Whether normalises take stock's estimate.</param>
public sealed record DispRadialContext(
    LightGeometry Geometry,
    FaceNeighbours Neighbours,
    PatchSet Patches,
    IReadOnlyList<FaceLight?> FaceLights,
    VradDisplacements Displacements,
    DispSampleHash Hash,
    bool StockNormalise);
