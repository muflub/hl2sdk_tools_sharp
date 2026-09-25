using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rad.Props;

/// <summary>
/// The part of a <c>directlight_t</c> that prop lighting reads:
/// the <c>dworldlight_t</c>, its PVS row, and the fade and cap distances.
/// </summary>
/// <remarks>
/// A value the direct-lighting lane's <c>DirectLight</c> can be projected onto,
/// or -- for a map stock vrad has already lit -- reconstructed from
/// <c>LUMP_WORLDLIGHTS</c> by <see cref="PropLights.FromWorldLights"/>.
/// </remarks>
public sealed record PropLight
{
    /// <summary><c>light.type</c>.</summary>
    public EmitType Type { get; init; }

    /// <summary><c>light.style</c>.</summary>
    public int Style { get; init; }

    /// <summary><c>light.origin</c>.</summary>
    public Vec3 Origin { get; init; }

    /// <summary><c>light.intensity</c> in vrad's 0..255 scale (the lump holds it divided by 255).</summary>
    public Vec3 Intensity { get; init; }

    /// <summary><c>light.normal</c>.</summary>
    public Vec3 Normal { get; init; }

    /// <summary><c>light.stopdot</c>.</summary>
    public float StopDot { get; init; }

    /// <summary><c>light.stopdot2</c>.</summary>
    public float StopDot2 { get; init; }

    /// <summary><c>light.exponent</c>.</summary>
    public float Exponent { get; init; }

    /// <summary><c>light.constant_attn</c>.</summary>
    public float ConstantAttn { get; init; }

    /// <summary><c>light.linear_attn</c>.</summary>
    public float LinearAttn { get; init; }

    /// <summary><c>light.quadratic_attn</c>.</summary>
    public float QuadraticAttn { get; init; }

    /// <summary><c>m_flStartFadeDistance</c>.</summary>
    public float StartFadeDistance { get; init; }

    /// <summary><c>m_flEndFadeDistance</c>; -1 (no fade) unless the entity set one.</summary>
    public float EndFadeDistance { get; init; } = -1.0f;

    /// <summary><c>m_flCapDist</c>; 1e22 unless the entity set one.</summary>
    public float CapDist { get; init; } = 1.0e22f;

    /// <summary><c>pvs</c>: which clusters can see the light.</summary>
    public byte[] Pvs { get; init; } = [];
}

/// <summary>Builds <see cref="PropLight"/>s.</summary>
public static class PropLights
{
    /// <summary><c>(float)(1.0 / 255.0)</c>, the export scale.</summary>
    private const float ExportScale = (float)(1.0 / 255.0);

    /// <summary>
    /// <c>activelights</c> as the compile that is running holds them: the
    /// direct lights <see cref="RadWorld"/> built, in list order.
    /// </summary>
    /// <param name="lights"><see cref="DirectLightSet.Active"/>.</param>
    /// <returns>One prop light per direct light, with every field it reads carried over.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lights"/> is null.</exception>
    /// <remarks>
    /// This is the path a compile takes. <see cref="FromWorldLights"/> exists
    /// for reading a map someone else lit, where the list survives only as the
    /// exported lump and its intensities have to be inverted.
    /// </remarks>
    public static IReadOnlyList<PropLight> FromDirectLights(IReadOnlyList<DirectLight> lights)
    {
        ArgumentNullException.ThrowIfNull(lights);
        List<PropLight> result = new(lights.Count);
        foreach (DirectLight dl in lights)
        {
            result.Add(new PropLight
            {
                Type = dl.Type,
                Style = dl.Style,
                Origin = dl.Origin,
                Intensity = dl.Intensity,
                Normal = dl.Normal,
                StopDot = dl.StopDot,
                StopDot2 = dl.StopDot2,
                Exponent = dl.Exponent,
                ConstantAttn = dl.ConstantAttn,
                LinearAttn = dl.LinearAttn,
                QuadraticAttn = dl.QuadraticAttn,
                StartFadeDistance = dl.StartFadeDistance,
                EndFadeDistance = dl.EndFadeDistance,
                CapDist = dl.CapDist,
                Pvs = dl.Pvs,
            });
        }

        return result;
    }

    /// <summary>
    /// Reconstructs <c>activelights</c> from a stock-lit map's world-light lump.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="mode">Which pass.</param>
    /// <param name="ambiguousIntensities">
    /// How many intensity channels had two 0..255 values exporting to the same
    /// float, where the smaller was taken.
    /// </param>
    /// <returns>The lights, in <c>activelights</c> order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bsp"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// SOUND BECAUSE THE LUMP IS THE LIST: <c>ExportDirectLightsToWorldLights</c>
    /// Walks <c>activelights</c> head to tail and
    /// writes one record per light. The PVS is rebuilt as <c>AllocDLight</c>
    /// does -- <c>SetDLightVis(dl, dl-&gt;light.cluster)</c> --
    /// plus, for the sky light, every leaf holding a sky face
    /// (<c>BuildVisForLightEnvironment</c>). Fade and cap
    /// distances are not in the lump; entity keys set them, and the defaults
    /// (no fade, no cap) are taken here.
    /// </para>
    /// <para>
    /// THE INTENSITY IS INVERTED, not read: the lump holds
    /// <c>fl(intensity * fl(1/255))</c>. Where the multiply is injective the
    /// original float is recovered exactly; where two originals round to the
    /// same export (a mantissa within 0.4 % of the top of its binade) the
    /// smaller is taken and <paramref name="ambiguousIntensities"/> counts it.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<PropLight> FromWorldLights(BspData bsp, LightingMode mode, out int ambiguousIntensities)
    {
        ArgumentNullException.ThrowIfNull(bsp);

        ReadOnlySpan<DWorldLight> lights = BspStructView.As<DWorldLight>(
            bsp[mode == LightingMode.Hdr ? BspLump.WorldLightsHdr : BspLump.WorldLights]);
        LightVisibility vis = LightVisibility.Load(bsp);
        int rowBytes = Math.Max(vis.RowBytes, 1);

        byte[]? skyRow = null;
        List<PropLight> result = new(lights.Length);
        ambiguousIntensities = 0;

        for (int i = 0; i < lights.Length; i++)
        {
            ref readonly DWorldLight wl = ref lights[i];
            byte[] pvs = new byte[rowBytes];
            vis.GetVisCache(wl.Cluster, pvs);

            if (wl.Type == (int)EmitType.SkyLight || wl.Type == (int)EmitType.SkyAmbient)
            {
                skyRow ??= SkyLeafRows(bsp, vis, rowBytes);
                for (int b = 0; b < rowBytes; b++)
                {
                    pvs[b] |= skyRow[b];
                }
            }

            (float ix, bool ax) = Invert(wl.Intensity.X);
            (float iy, bool ay) = Invert(wl.Intensity.Y);
            (float iz, bool az) = Invert(wl.Intensity.Z);
            ambiguousIntensities += (ax ? 1 : 0) + (ay ? 1 : 0) + (az ? 1 : 0);

            result.Add(new PropLight
            {
                Type = (EmitType)wl.Type,
                Style = wl.Style,
                Origin = wl.Origin,
                Intensity = new Vec3(ix, iy, iz),
                Normal = wl.Normal,
                StopDot = wl.StopDot,
                StopDot2 = wl.StopDot2,
                Exponent = wl.Exponent,
                ConstantAttn = wl.ConstantAttn,
                LinearAttn = wl.LinearAttn,
                QuadraticAttn = wl.QuadraticAttn,
                Pvs = pvs,
            });
        }

        return result;
    }

    /// <summary>The 0..255 intensity that exported to <paramref name="exported"/>.</summary>
    /// <param name="exported">The lump's value.</param>
    /// <returns>The preimage, and whether there was more than one.</returns>
    public static (float Value, bool Ambiguous) Invert(float exported)
    {
        float guess = exported * 255.0f;
        float best = float.NaN;
        int hits = 0;
        float x = guess;
        for (int k = 0; k < 4; k++)
        {
            x = MathF.BitDecrement(x);
        }

        for (int k = 0; k < 9; k++, x = MathF.BitIncrement(x))
        {
            if (x * ExportScale == exported)
            {
                if (hits == 0)
                {
                    best = x;
                }

                hits++;
            }
        }

        return hits == 0 ? (guess, false) : (best, hits > 1);
    }

    /// <summary>
    /// The OR of the PVS rows of every leaf holding a sky face
    /// (<c>BuildVisForLightEnvironment</c>'s first loop).
    /// </summary>
    private static byte[] SkyLeafRows(BspData bsp, LightVisibility vis, int rowBytes)
    {
        DLeaf[] leaves = AmbientScene.ReadLeaves(bsp);
        ReadOnlySpan<ushort> leafFaces = BspStructView.As<ushort>(bsp[BspLump.LeafFaces]);
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        ReadOnlySpan<TexInfo> texInfo = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);

        byte[] merged = new byte[rowBytes];
        byte[] row = new byte[rowBytes];
        for (int leaf = 0; leaf < leaves.Length; leaf++)
        {
            for (int k = 0; k < leaves[leaf].NumLeafFaces; k++)
            {
                int face = leafFaces[leaves[leaf].FirstLeafFace + k];
                if ((texInfo[faces[face].TexInfo].Flags & RayAmbientLighting.SurfSky) != 0)
                {
                    vis.GetVisCache(leaves[leaf].Cluster, row);
                    for (int b = 0; b < rowBytes; b++)
                    {
                        merged[b] |= row[b];
                    }

                    break;
                }
            }
        }

        return merged;
    }
}
