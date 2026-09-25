using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// One sample's accumulated light: <c>LightingValue_t</c>.
/// </summary>
/// <remarks>
/// <para>
/// A colour and a "direct sun amount". The sun amount is accumulated by every
/// light add and, in this drop of the SDK, READ BY NOTHING:
/// <c>grep -rn m_flDirectSunAmount src/utils/vrad</c> finds only the struct's
/// own methods and the two <c>AddLight</c> calls in the reference implementation. It is
/// carried anyway so a consumer that wants it (Mapbase's sun-shadow lightmaps
/// read it) does not have to re-derive the four-lane rule that decides it.
/// </para>
/// <para>
/// All arithmetic is in the C++ operand order. <c>AddLight(amount, color)</c>
/// is <c>VectorMA(m_vecLighting, amount, color, m_vecLighting)</c>, which is
/// <c>lighting + amount * color</c> per component.
/// </para>
/// </remarks>
public struct LightingValue
{
    /// <summary><c>m_vecLighting</c>: linear light, 0..255 scale per unit intensity.</summary>
    public Vec3 Lighting;

    /// <summary><c>m_flDirectSunAmount</c>.</summary>
    public float DirectSunAmount;

    /// <summary>Creates a value.</summary>
    /// <param name="lighting">The colour.</param>
    /// <param name="directSunAmount">The sun amount.</param>
    public LightingValue(Vec3 lighting, float directSunAmount)
    {
        Lighting = lighting;
        DirectSunAmount = directSunAmount;
    }

    /// <summary><c>Intensity</c>: the plain sum of the three channels.</summary>
    /// <returns><c>x + y + z</c>, left to right.</returns>
    public readonly float Intensity() => Lighting.X + Lighting.Y + Lighting.Z;

    /// <summary><c>AddLight(amount, color, sun)</c>.</summary>
    /// <param name="amount">The scale on <paramref name="color"/>.</param>
    /// <param name="color">The light's intensity.</param>
    /// <param name="sunAmount">Added to the sun amount unscaled.</param>
    public void AddLight(float amount, Vec3 color, float sunAmount)
    {
        Lighting = new Vec3(
            Lighting.X + (amount * color.X),
            Lighting.Y + (amount * color.Y),
            Lighting.Z + (amount * color.Z));
        DirectSunAmount += sunAmount;
    }

    /// <summary><c>AddLight(const LightingValue_t&amp;)</c>.</summary>
    /// <param name="other">The value to add.</param>
    public void AddLight(in LightingValue other)
    {
        Lighting += other.Lighting;
        DirectSunAmount += other.DirectSunAmount;
    }

    /// <summary><c>AddWeighted(src, weight)</c>: <c>this += weight * src</c>.</summary>
    /// <param name="other">The value to add.</param>
    /// <param name="weight">Its weight.</param>
    public void AddWeighted(in LightingValue other, float weight)
    {
        Lighting += weight * other.Lighting;
        DirectSunAmount += weight * other.DirectSunAmount;
    }

    /// <summary><c>Scale(s)</c>.</summary>
    /// <param name="scale">The factor.</param>
    public void Scale(float scale)
    {
        Lighting *= scale;
        DirectSunAmount *= scale;
    }
}
