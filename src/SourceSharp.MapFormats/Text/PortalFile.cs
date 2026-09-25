using System.Globalization;
using System.Text;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The <c>.prt</c> portal file: vbsp's <c>WritePortalFile</c>
/// (<c>src/utils/vbsp/prtfile.cpp</c>) and vvis's <c>LoadPortals</c>
/// (<c>src/utils/vvis/vvis.cpp:407-563</c>).
/// </summary>
/// <remarks>
/// <para>
/// This is the whole interface between the two compilers, and it is a text
/// file, which is why it is in this namespace rather than with the BSP lumps.
/// One file portal describes a two-sided opening between two clusters;
/// <see cref="ToMemoryPortals"/> performs the doubling vvis does at load.
/// </para>
/// <para>
/// Only <c>PRT1</c> exists. <c>PRT2</c> and the <c>PRT1-AM</c> of other
/// branches are not handled anywhere in this tree -- the only two occurrences
/// of the magic are <c>src/utils/vvis/vis.h:17</c> and
/// <c>src/utils/vbsp/prtfile.cpp:21</c>, both <c>"PRT1"</c>.
/// </para>
/// </remarks>
public sealed class PortalFile
{
    /// <summary>
    /// The magic, <c>PORTALFILE</c> (<c>src/utils/vvis/vis.h:17</c>).
    /// </summary>
    public const string Magic = "PRT1";

    /// <summary>
    /// <c>MAX_PORTALS</c> (<c>src/utils/vvis/vis.h:15</c>).
    /// </summary>
    /// <remarks>
    /// This is the limit on MEMORY portals, and the check is
    /// <c>g_numportals * 2 &gt;= MAX_PORTALS</c>
    /// (<c>src/utils/vvis/vvis.cpp:472</c>) -- so the largest FILE portal count
    /// that loads is 32767, one less than the half you would expect, because
    /// the comparison is <c>&gt;=</c> rather than <c>&gt;</c>.
    /// </remarks>
    public const int MaxMemoryPortals = 65536;

    /// <summary>
    /// The largest accepted portal count in the header: 32767.
    /// </summary>
    public const int MaxFilePortals = (MaxMemoryPortals / 2) - 1;

    /// <summary>
    /// <c>MAX_POINTS_ON_WINDING</c> (<c>src/utils/vvis/vis.h:28</c>). The check
    /// is <c>&gt;</c> (<c>vvis.cpp:505</c>), so exactly 64 points is legal.
    /// </summary>
    public const int MaxPointsOnWinding = 64;

    /// <summary>The cluster count from the header.</summary>
    public int ClusterCount { get; set; }

    /// <summary>The portals, in file order.</summary>
    public IList<FilePortal> Portals { get; } = [];

    /// <summary>Reads a portal file from a stream.</summary>
    /// <param name="stream">The bytes of the file, read to its end.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="InvalidPortalFileException">The file is malformed.</exception>
    public static async Task<PortalFile> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return await ParseAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses a portal file already in memory.</summary>
    /// <param name="bytes">The whole file.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="InvalidPortalFileException">The file is malformed.</exception>
    public static ValueTask<PortalFile> ParseAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default) =>
        ParseAsync(Encoding.Latin1.GetString(bytes.Span), cancellationToken);

    /// <summary>Parses a portal file from decoded text.</summary>
    /// <param name="text">The whole file.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="InvalidPortalFileException">The file is malformed.</exception>
    public static ValueTask<PortalFile> ParseAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            return ValueTask.FromResult(Parse(text, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return ValueTask.FromCanceled<PortalFile>(cancellationToken);
        }
    }

    /// <summary>Writes the file in vbsp's exact framing.</summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="lineEnding">Which line ending to emit.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when everything is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public async Task WriteAsync(
        Stream stream,
        PortalLineEnding lineEnding = PortalLineEnding.Lf,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] bytes = ToBytes(lineEnding);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The file's bytes, exactly as <see cref="WriteAsync"/> would write them.
    /// </summary>
    /// <param name="lineEnding">Which line ending to emit.</param>
    /// <returns>The encoded file.</returns>
    /// <remarks>
    /// <para>
    /// The header is three separate <c>fprintf</c>s, one value to a line
    /// (<c>prtfile.cpp:356-358</c>).
    /// </para>
    /// <para>
    /// Each portal is <c>"%i %i %i "</c> -- note the trailing space
    /// (<c>prtfile.cpp:69</c>) -- then one <c>(x y z ) </c> per point
    /// (<c>prtfile.cpp:76-83</c>: an open paren, three numbers EACH with a
    /// trailing space, then a close paren and another space), then a newline
    /// (<c>prtfile.cpp:84</c>). So a line ends with <c>") \n"</c>: space, then
    /// the terminator.
    /// </para>
    /// </remarks>
    public byte[] ToBytes(PortalLineEnding lineEnding = PortalLineEnding.Lf)
    {
        string newline = lineEnding == PortalLineEnding.CrLf ? "\r\n" : "\n";
        StringBuilder output = new();

        output.Append(Magic).Append(newline);
        output.Append(ClusterCount.ToString(CultureInfo.InvariantCulture)).Append(newline);
        output.Append(Portals.Count.ToString(CultureInfo.InvariantCulture)).Append(newline);

        foreach (FilePortal portal in Portals)
        {
            output.Append(portal.Points.Count.ToString(CultureInfo.InvariantCulture)).Append(' ');
            output.Append(portal.Cluster0.ToString(CultureInfo.InvariantCulture)).Append(' ');
            output.Append(portal.Cluster1.ToString(CultureInfo.InvariantCulture)).Append(' ');

            foreach (Vec3 point in portal.Points)
            {
                output.Append('(');
                AppendFloat(output, point.X);
                AppendFloat(output, point.Y);
                AppendFloat(output, point.Z);
                output.Append(") ");
            }

            output.Append(newline);
        }

        return Encoding.Latin1.GetBytes(output.ToString());
    }

    /// <summary>
    /// Performs vvis's doubling: each file portal becomes two memory portals.
    /// </summary>
    /// <returns>
    /// Two entries per file portal, in file order: the forward portal first,
    /// then the backward one.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the rule the rest of vis is written against
    /// (<c>src/utils/vvis/vvis.cpp:484-486</c>, "each file portal is split into
    /// two memory portals"), and neither half is what the file literally says.
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// The plane is COMPUTED from the winding, never read: <c>v1 = p[2]-p[1]</c>,
    /// <c>v2 = p[0]-p[1]</c>, <c>normal = cross(v2, v1)</c>, normalised, and
    /// <c>dist = dot(p[0], normal)</c> (<c>vvis.cpp:61-71,531</c>).
    /// </description></item>
    /// <item><description>
    /// FORWARD (<c>vvis.cpp:533-542</c>): filed under <c>leafnums[0]</c>,
    /// winding in the order read, plane FULLY NEGATED -- both the normal and
    /// the distance -- and <c>leaf</c> set to <c>leafnums[1]</c>, the
    /// NEIGHBOUR.
    /// </description></item>
    /// <item><description>
    /// BACKWARD (<c>vvis.cpp:544-557</c>): filed under <c>leafnums[1]</c>,
    /// winding REVERSED (<c>points[numpoints-1-j]</c>), plane NOT negated, and
    /// <c>leaf</c> set to <c>leafnums[0]</c>.
    /// </description></item>
    /// </list>
    /// </remarks>
    public IReadOnlyList<MemoryPortal> ToMemoryPortals()
    {
        List<MemoryPortal> result = new(Portals.Count * 2);

        foreach (FilePortal portal in Portals)
        {
            (Vec3 normal, float distance) = PlaneFromWinding(portal.Points);

            result.Add(new MemoryPortal(
                OwningCluster: portal.Cluster0,
                Leaf: portal.Cluster1,
                Normal: -normal,
                Distance: -distance,
                Points: [.. portal.Points],
                IsOriginalWinding: true));

            Vec3[] reversed = new Vec3[portal.Points.Count];
            for (int i = 0; i < reversed.Length; i++)
            {
                reversed[i] = portal.Points[portal.Points.Count - 1 - i];
            }

            result.Add(new MemoryPortal(
                OwningCluster: portal.Cluster1,
                Leaf: portal.Cluster0,
                Normal: normal,
                Distance: distance,
                Points: reversed,
                IsOriginalWinding: false));
        }

        return result;
    }

    /// <summary>
    /// <c>PlaneFromWinding</c> (<c>src/utils/vvis/vvis.cpp:61-71</c>).
    /// </summary>
    /// <param name="points">The winding, which must have at least three points.</param>
    /// <returns>The plane's normal and distance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="points"/> is null.</exception>
    /// <exception cref="InvalidPortalFileException">
    /// There are fewer than three points. The C++ reads <c>points[0..2]</c>
    /// unconditionally and has no such check -- a winding of two points is
    /// undefined behaviour there.
    /// </exception>
    public static (Vec3 Normal, float Distance) PlaneFromWinding(IReadOnlyList<Vec3> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count < 3)
        {
            throw new InvalidPortalFileException(
                $"a portal winding needs at least 3 points to define a plane, got {points.Count}");
        }

        // vvis.cpp:66-67 -- v1 = p[2] - p[1] and v2 = p[0] - p[1], then
        // CrossProduct(v2, v1). The operand order is the answer's sign.
        Vec3 v1 = points[2] - points[1];
        Vec3 v2 = points[0] - points[1];
        (Vec3 normal, _) = Vec3.Cross(v2, v1).Normalise();
        float distance = Vec3.Dot(points[0], normal);

        return (normal, distance);
    }

    private static void AppendFloat(StringBuilder output, float value)
    {
        // WriteFloat, prtfile.cpp:33-39. RoundInt is floor(v + 0.5f)
        // (src/public/mathlib/mathlib.h:432-435), which is round-half-UP and
        // not .NET's round-half-to-even -- so -0.5 becomes 0, not -0 or -1.
        double rounded = Math.Floor(value + 0.5f);

        if (Math.Abs(value - rounded) < 0.001)
        {
            output.Append(((int)rounded).ToString(CultureInfo.InvariantCulture)).Append(' ');
        }
        else
        {
            output.Append(CFormat.FormatF6(value)).Append(' ');
        }
    }

    private static PortalFile Parse(string text, CancellationToken cancellationToken)
    {
        PortalScanner scanner = new(text);

        // vvis.cpp:464 -- ONE fscanf of "%79s\n%i\n%i\n". The newlines in a
        // scanf format are ordinary whitespace directives, so the three fields
        // may be separated by any whitespace at all; the conventional layout is
        // not enforced.
        if (!scanner.TryReadWord(79, out string magic) ||
            !scanner.TryReadInt(out int clusterCount) ||
            !scanner.TryReadInt(out int portalCount))
        {
            throw new InvalidPortalFileException("failed to read header");
        }

        // vvis.cpp:466 -- stricmp, so "prt1" loads. The writer only ever emits
        // upper case (prtfile.cpp:21,356).
        if (!string.Equals(magic, Magic, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidPortalFileException("not a portal file");
        }

        // vvis.cpp:472 -- the check is on the DOUBLED count and is >=.
        if (portalCount * 2 >= MaxMemoryPortals)
        {
            throw new InvalidPortalFileException(
                $"the map overflows the max portal count ({portalCount} of max {MaxMemoryPortals / 2})!");
        }

        PortalFile file = new() { ClusterCount = clusterCount };

        for (int i = 0; i < portalCount; i++)
        {
            if ((i & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // vvis.cpp:502 -- "%i %i %i ", in the order count, leaf, leaf.
            if (!scanner.TryReadInt(out int pointCount) ||
                !scanner.TryReadInt(out int leaf0) ||
                !scanner.TryReadInt(out int leaf1))
            {
                throw new InvalidPortalFileException($"reading portal {i}");
            }

            if (pointCount > MaxPointsOnWinding)
            {
                throw new InvalidPortalFileException($"portal {i} has too many points");
            }

            // vvis.cpp:507-508 -- the bound is (unsigned)leaf > portalclusters,
            // NOT >=, so leafnum == clusterCount passes here and then indexes
            // one past the end of the leaf array at vvis.cpp:534. Negative
            // values become huge unsigned values and ARE rejected. Reproduced
            // exactly, including the off-by-one, because a map that stock loads
            // must load here.
            if ((uint)leaf0 > (uint)clusterCount || (uint)leaf1 > (uint)clusterCount)
            {
                throw new InvalidPortalFileException($"reading portal {i}");
            }

            List<Vec3> points = new(Math.Max(0, pointCount));
            for (int j = 0; j < pointCount; j++)
            {
                // vvis.cpp:522 -- "(%lf %lf %lf ) ". Read as DOUBLE and then
                // narrowed per component at vvis.cpp:525-526, which is the
                // whole reason an integer-spelled and a float-spelled
                // coordinate give bit-identical floats.
                if (!scanner.TryReadPoint(out double x, out double y, out double z))
                {
                    throw new InvalidPortalFileException($"reading portal {i}");
                }

                points.Add(new Vec3((float)x, (float)y, (float)z));
            }

            file.Portals.Add(new FilePortal(leaf0, leaf1, points));
        }

        // vvis.cpp:562 closes the file without checking it was consumed, so
        // trailing text is silently ignored. So is it here.
        return file;
    }

    /// <summary>
    /// The <c>fscanf</c> subset the portal reader uses: words, integers and the
    /// parenthesised triple.
    /// </summary>
    private sealed class PortalScanner(string text)
    {
        private readonly string _text = text;
        private int _position;

        public bool TryReadWord(int maximum, out string word)
        {
            SkipWhitespace();

            int start = _position;
            while (_position < _text.Length && !char.IsWhiteSpace(_text[_position]) &&
                   _position - start < maximum)
            {
                _position++;
            }

            word = _text[start.._position];
            return word.Length > 0;
        }

        public bool TryReadInt(out int value)
        {
            SkipWhitespace();

            int start = _position;
            if (_position < _text.Length && (_text[_position] == '+' || _text[_position] == '-'))
            {
                _position++;
            }

            int digits = _position;
            while (_position < _text.Length && char.IsAsciiDigit(_text[_position]))
            {
                _position++;
            }

            if (_position == digits)
            {
                _position = start;
                value = 0;
                return false;
            }

            // %i in C stops at the first non-digit; out of range is undefined,
            // and glibc leaves the destination alone. Failing the read is the
            // defensible reading and is what the error path expects.
            return int.TryParse(
                _text[start.._position], NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out value);
        }

        public bool TryReadPoint(out double x, out double y, out double z)
        {
            x = y = z = 0;
            SkipWhitespace();

            // The '(' is a hard literal in the format string.
            if (_position >= _text.Length || _text[_position] != '(')
            {
                return false;
            }

            _position++;

            if (!TryReadDouble(out x) || !TryReadDouble(out y) || !TryReadDouble(out z))
            {
                return false;
            }

            // " ) " -- whitespace directives match zero or more, so only the
            // ')' itself must be present.
            SkipWhitespace();
            if (_position >= _text.Length || _text[_position] != ')')
            {
                return false;
            }

            _position++;
            return true;
        }

        private bool TryReadDouble(out double value)
        {
            SkipWhitespace();

            int start = _position;
            if (_position < _text.Length && (_text[_position] == '+' || _text[_position] == '-'))
            {
                _position++;
            }

            int digits = 0;
            while (_position < _text.Length && char.IsAsciiDigit(_text[_position]))
            {
                _position++;
                digits++;
            }

            if (_position < _text.Length && _text[_position] == '.')
            {
                _position++;
                while (_position < _text.Length && char.IsAsciiDigit(_text[_position]))
                {
                    _position++;
                    digits++;
                }
            }

            if (digits == 0)
            {
                _position = start;
                value = 0;
                return false;
            }

            if (_position < _text.Length && (_text[_position] == 'e' || _text[_position] == 'E'))
            {
                int mark = _position;
                _position++;
                if (_position < _text.Length && (_text[_position] == '+' || _text[_position] == '-'))
                {
                    _position++;
                }

                int exponentDigits = _position;
                while (_position < _text.Length && char.IsAsciiDigit(_text[_position]))
                {
                    _position++;
                }

                if (_position == exponentDigits)
                {
                    _position = mark;
                }
            }

            return double.TryParse(
                _text[start.._position], NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);
        }

        private void SkipWhitespace()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
            {
                _position++;
            }
        }
    }
}
