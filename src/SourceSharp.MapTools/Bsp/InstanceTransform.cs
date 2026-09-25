using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// A <c>matrix3x4_t</c>: a rotation and a translation, in the row-major
/// layout <c>mathlib</c> uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>The minimum this lane needed, not a matrix library.</b> Instance merging
/// is the only thing in the reference implementation that uses one, and it uses exactly five
/// operations: <c>AngleMatrix</c> to build it,
/// <see cref="TransformPoint"/> (<c>VectorTransform</c>),
/// <see cref="RotateVector"/> (<c>VectorRotate</c>),
/// <see cref="TransformBounds"/> (<c>TransformAABB</c>) and
/// <see cref="TransformPlane"/> (<c>MatrixTransformPlane</c>). A later lane
/// that needs more should widen this rather than start a second matrix type.
/// </para>
/// <para>
/// Rows are <c>M[row][column]</c>, column 3 is the translation, and the
/// element order is <c>mathlib</c>'s — which matters, because
/// <c>MatrixTransformPlane</c> reads <c>src[0][3]</c>, <c>src[1][3]</c> and
/// <c>src[2][3]</c> individually rather than through an accessor.
/// </para>
/// </remarks>
public readonly struct InstanceTransform : IEquatable<InstanceTransform>
{
    private readonly float[] _m;

    private InstanceTransform(float[] m) => _m = m;

    /// <summary>The transform that changes nothing.</summary>
    public static InstanceTransform Identity => new(
    [
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 1f, 0f,
    ]);

    /// <summary>One element.</summary>
    /// <param name="row">The row, 0 to 2.</param>
    /// <param name="column">The column, 0 to 3.</param>
    /// <returns>The element.</returns>
    public float this[int row, int column] => (_m ?? Identity._m)[(row * 4) + column];

    /// <summary>
    /// Builds a transform from Hammer angles and a position:
    /// <c>AngleMatrix</c>.
    /// </summary>
    /// <param name="angles">Pitch, yaw and roll in degrees, in that order.</param>
    /// <param name="position">The translation, which becomes column 3.</param>
    /// <returns>The transform.</returns>
    /// <remarks>
    /// The composition is <c>(YAW * PITCH) * ROLL</c>, spelled out element by
    /// element exactly as spells it, and the
    /// angle order in a <c>QAngle</c> is pitch, yaw, roll — so
    /// <c>angles.X</c> is pitch and <c>angles.Y</c> is yaw, not the other way
    /// round.
    /// </remarks>
    public static InstanceTransform FromAngles(Vec3 angles, Vec3 position)
    {
        float pitch = angles.X * (MathF.PI / 180f);
        float yaw = angles.Y * (MathF.PI / 180f);
        float roll = angles.Z * (MathF.PI / 180f);

        float sy = MathF.Sin(yaw);
        float cy = MathF.Cos(yaw);
        float sp = MathF.Sin(pitch);
        float cp = MathF.Cos(pitch);
        float sr = MathF.Sin(roll);
        float cr = MathF.Cos(roll);

        float crcy = cr * cy;
        float crsy = cr * sy;
        float srcy = sr * cy;
        float srsy = sr * sy;

        return new InstanceTransform(
        [
            cp * cy, (sp * srcy) - crsy, (sp * crcy) + srsy, position.X,
            cp * sy, (sp * srsy) + crcy, (sp * crsy) - srcy, position.Y,
            -sp, sr * cp, cr * cp, position.Z,
        ]);
    }

    /// <summary>
    /// Rotates and translates a point: <c>VectorTransform</c>,
    ///.
    /// </summary>
    /// <param name="point">The point.</param>
    /// <returns>The transformed point.</returns>
    public Vec3 TransformPoint(Vec3 point) => new(
        Row3Dot(0, point) + this[0, 3],
        Row3Dot(1, point) + this[1, 3],
        Row3Dot(2, point) + this[2, 3]);

    /// <summary>
    /// Rotates a direction, ignoring the translation:
    /// <c>VectorRotate</c>.
    /// </summary>
    /// <param name="vector">The direction.</param>
    /// <returns>The rotated direction.</returns>
    public Vec3 RotateVector(Vec3 vector) => new(
        Row3Dot(0, vector),
        Row3Dot(1, vector),
        Row3Dot(2, vector));

    /// <summary>
    /// Transforms an axis-aligned box into the smallest axis-aligned box that
    /// contains it: <c>TransformAABB</c>,
    ///.
    /// </summary>
    /// <param name="mins">The box minimum.</param>
    /// <param name="maxs">The box maximum.</param>
    /// <returns>The transformed box.</returns>
    /// <remarks>
    /// Via the centre and the half-extents, with the extents transformed by the
    /// ABSOLUTE values of the matrix rows — which is what makes the result a
    /// bound rather than a rotated box. Not the same as transforming the two
    /// corners.
    /// </remarks>
    public (Vec3 Mins, Vec3 Maxs) TransformBounds(Vec3 mins, Vec3 maxs)
    {
        Vec3 localCenter = (mins + maxs) * 0.5f;
        Vec3 localExtents = maxs - localCenter;

        Vec3 worldCenter = TransformPoint(localCenter);
        Vec3 worldExtents = new(
            Row3DotAbs(0, localExtents),
            Row3DotAbs(1, localExtents),
            Row3DotAbs(2, localExtents));

        return (worldCenter - worldExtents, worldCenter + worldExtents);
    }

    /// <summary>
    /// Transforms a plane: <c>MatrixTransformPlane</c>,
    ///.
    /// </summary>
    /// <param name="plane">The plane.</param>
    /// <returns>The transformed plane.</returns>
    /// <remarks>
    /// The distance is <c>dist * Dot(n', n')</c> plus the translation's
    /// contribution — the self-dot is stock's, and is 1 only when the rotation
    /// is exactly orthonormal, which a float <c>AngleMatrix</c> is not quite.
    /// So the factor is kept rather than dropped as an identity.
    /// </remarks>
    public Plane TransformPlane(Plane plane)
    {
        Vec3 normal = RotateVector(plane.Normal);
        float dist = plane.Dist * Vec3.Dot(normal, normal);
        dist += (normal.X * this[0, 3]) + (normal.Y * this[1, 3]) + (normal.Z * this[2, 3]);
        return new Plane(normal, dist);
    }

    /// <inheritdoc />
    public bool Equals(InstanceTransform other)
    {
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                if (this[i, j] != other[i, j])
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is InstanceTransform other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(this[0, 0], this[1, 1], this[2, 2], this[0, 3], this[1, 3], this[2, 3]);

    /// <summary>Compares two transforms element by element.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns>True when every element matches.</returns>
    public static bool operator ==(InstanceTransform left, InstanceTransform right) =>
        left.Equals(right);

    /// <summary>Compares two transforms element by element.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns>True when anything differs.</returns>
    public static bool operator !=(InstanceTransform left, InstanceTransform right) =>
        !left.Equals(right);

    private float Row3Dot(int row, Vec3 v) =>
        (this[row, 0] * v.X) + (this[row, 1] * v.Y) + (this[row, 2] * v.Z);

    // DotProductAbs takes the absolute value of
    // each PRODUCT, not of each matrix element -- the same answer only while
    // the extents are non-negative, which they are here but which is not what
    // the function says.
    private float Row3DotAbs(int row, Vec3 v) =>
        MathF.Abs(this[row, 0] * v.X) +
        MathF.Abs(this[row, 1] * v.Y) +
        MathF.Abs(this[row, 2] * v.Z);
}
