namespace SourceSharp.MapTools.Disp;

/// <summary>
/// A vertex's position in a displacement's square grid: <c>CVertIndex</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The components are short, and that is load bearing rather than thrift.</b>
/// <c>RotateVertIncrement</c> negates an
/// increment, <c>CDispSubEdgeIterator::Start</c> steps an index one increment
/// BEFORE the first vertex, and
/// <c>WrapVertIndex</c> is handed indices
/// outside the grid on purpose. Negative and out-of-range values are part of
/// the arithmetic, so this is a signed pair and not an unsigned one; 16 bits is
/// simply what stock chose, and a displacement side is at most 17 posts.
/// </para>
/// <para>
/// A struct rather than a class, because stock copies these by value
/// everywhere and the identity of one never matters.
/// </para>
/// </remarks>
/// <param name="X">The column, 0..sideLength-1 for an in-range index.</param>
/// <param name="Y">The row.</param>
public readonly record struct VertIndex(short X, short Y)
{
    /// <summary>The index stock uses to mean "no vertex": (-1, -1).</summary>
    /// <remarks>
    /// <c>CVertInfo::CVertInfo</c> fills the dependency arrays with it
    /// And <c>CVertDependency::IsValid</c>
    /// tests <b>only x</b> against -1, which is
    /// why nothing may construct a (-1, y) index and expect it to be treated
    /// as real.
    /// </remarks>
    public static VertIndex Invalid => new(-1, -1);

    /// <summary>Builds one from ints, which is what every caller has.</summary>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    /// <remarks>
    /// Unchecked, like the C++ narrowing it replaces: <c>CVertIndex(int, int)</c>
    /// does not exist in stock, but every construction site passes an int
    /// expression that C++ narrows silently.
    /// </remarks>
    public VertIndex(int x, int y)
        : this(unchecked((short)x), unchecked((short)y))
    {
    }

    /// <summary>The component at <paramref name="i"/>: 0 is x, 1 is y.</summary>
    /// <param name="i">0 or 1.</param>
    /// <returns>That component.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="i"/> is not 0 or 1.
    /// </exception>
    /// <remarks>
    /// Stock reaches the components through <c>operator[]</c> by casting
    /// <c>this</c> to <c>short*</c>, which is what
    /// lets <c>g_EdgeDims</c> name a dimension by number. That indexing is the
    /// whole neighbour system's vocabulary, so it is kept — as a switch, since
    /// the layout pun is not something to reproduce.
    /// </remarks>
    public short this[int i] => i switch
    {
        0 => X,
        1 => Y,
        _ => throw new ArgumentOutOfRangeException(
            nameof(i), i, "a vertex index has two components, 0 (x) and 1 (y)."),
    };

    /// <summary>This index with one component replaced.</summary>
    /// <param name="i">0 for x, 1 for y.</param>
    /// <param name="value">The new value.</param>
    /// <returns>A new index; this one is unchanged.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="i"/> is not 0 or 1.
    /// </exception>
    public VertIndex With(int i, int value) => i switch
    {
        0 => new VertIndex(value, Y),
        1 => new VertIndex(X, value),
        _ => throw new ArgumentOutOfRangeException(
            nameof(i), i, "a vertex index has two components, 0 (x) and 1 (y)."),
    };

    /// <summary>Componentwise addition.</summary>
    /// <param name="a">The left index.</param>
    /// <param name="b">The right index.</param>
    /// <returns>The sum.</returns>
    public static VertIndex operator +(VertIndex a, VertIndex b) =>
        new(a.X + b.X, a.Y + b.Y);

    /// <summary>Componentwise subtraction.</summary>
    /// <param name="a">The left index.</param>
    /// <param name="b">The right index.</param>
    /// <returns>The difference.</returns>
    public static VertIndex operator -(VertIndex a, VertIndex b) =>
        new(a.X - b.X, a.Y - b.Y);

    /// <summary>
    /// This index offset by <paramref name="offset"/> scaled by
    /// <paramref name="multiplier"/>.
    /// </summary>
    /// <param name="offset">The direction, usually a unit step.</param>
    /// <param name="multiplier">How far to step.</param>
    /// <returns>The offset index.</returns>
    /// <remarks><c>BuildOffsetVertIndex</c>.</remarks>
    public VertIndex Offset(VertIndex offset, int multiplier) =>
        new(X + (offset.X * multiplier), Y + (offset.Y * multiplier));

    /// <inheritdoc />
    public override string ToString() => $"({X}, {Y})";
}
