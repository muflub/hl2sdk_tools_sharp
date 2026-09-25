using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Diagnostics;

namespace SourceSharp.MapTools.Disp;

/// <summary>
/// Reads a VMF <c>dispinfo</c> chunk: the <c>LoadDisp*Callback</c> family.
/// </summary>
/// <remarks>
/// <para>
/// THE ORDER OF THE KEYS MATTERS and this reader honours it. Stock's chunk
/// reader delivers keys and sub-chunks interleaved in file order, and every
/// row parser sizes itself from <c>pMapDispInfo-&gt;power</c> — which the
/// <c>power</c> key set when it went past. Hammer writes <c>power</c> first, so
/// this works; a VMF that put <c>normals</c> before <c>power</c> would have
/// stock read its rows with power 0 and write them 1 per row. That is
/// reproduced here by reading <c>power</c> in a first pass, which is the one
/// deliberate departure — because the array sizes have to be known before the
/// rows are read at all, and a zero-power run would throw rather than quietly
/// scramble.
/// </para>
/// <para>
/// Row keys are <c>row0</c>, <c>row1</c>, ... and their INDEX comes from the
/// key name, not from the order they appear in. Stock parses it with
/// <c>atoi(&amp;szKey[3])</c> and writes at <c>row * nCols</c>, so a file with
/// its rows out of order still lands them correctly, and a file with a row
/// number past the end writes off the end of a fixed array. Here a bad row
/// number is rejected.
/// </para>
/// </remarks>
public static class VmfDisplacementReader
{
    /// <summary>The VMF chunk name.</summary>
    public const string DispInfoChunk = "dispinfo";

    /// <summary>The per-vertex field direction rows.</summary>
    public const string NormalsChunk = "normals";

    /// <summary>The per-vertex distance rows.</summary>
    public const string DistancesChunk = "distances";

    /// <summary>The per-vertex world-space offset rows.</summary>
    public const string OffsetsChunk = "offsets";

    /// <summary>The per-vertex blend alpha rows.</summary>
    public const string AlphasChunk = "alphas";

    /// <summary>The per-triangle tag rows.</summary>
    public const string TriangleTagsChunk = "triangle_tags";

    /// <summary><c>COREDISPTRI_TAG_WALKABLE</c>.</summary>
    public const int CoreTagWalkable = 1 << 0;

    /// <summary><c>COREDISPTRI_TAG_FORCE_WALKABLE_BIT</c>.</summary>
    public const int CoreTagForceWalkableBit = 1 << 1;

    /// <summary><c>COREDISPTRI_TAG_FORCE_WALKABLE_VAL</c>.</summary>
    public const int CoreTagForceWalkableVal = 1 << 2;

    /// <summary><c>COREDISPTRI_TAG_BUILDABLE</c>.</summary>
    public const int CoreTagBuildable = 1 << 3;

    /// <summary><c>COREDISPTRI_TAG_FORCE_BUILDABLE_BIT</c>.</summary>
    public const int CoreTagForceBuildableBit = 1 << 4;

    /// <summary><c>COREDISPTRI_TAG_FORCE_BUILDABLE_VAL</c>.</summary>
    public const int CoreTagForceBuildableVal = 1 << 5;

    /// <summary>
    /// Reads one <c>dispinfo</c> chunk.
    /// </summary>
    /// <param name="chunk">The chunk.</param>
    /// <returns>The displacement it describes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="chunk"/> is null.</exception>
    /// <exception cref="MapCompileException">
    /// The chunk has no usable <c>power</c>, or a row key names a row outside
    /// the grid.
    /// </exception>
    public static MapDisplacement Read(VmfChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        int power = VmfValue.ParseInt(chunk.GetValue("power"));

        if (power < PowerInfo.MinMapDispPower || power > PowerInfo.MaxMapDispPower)
        {
            throw new MapCompileException(
                $"dispinfo has power {power}; the format allows "
                + $"{PowerInfo.MinMapDispPower} to {PowerInfo.MaxMapDispPower}.");
        }

        MapDisplacement disp = new(power);

        foreach (VmfNode node in chunk.Children)
        {
            if (node is VmfKey key)
            {
                ApplyKey(disp, key.Name, key.Value);
                continue;
            }

            if (node is not VmfChunk child)
            {
                continue;
            }

            if (Is(child, NormalsChunk))
            {
                ReadVectorRows(child, power, disp.FieldVectors);
            }
            else if (Is(child, DistancesChunk))
            {
                ReadFloatRows(child, power, disp.FieldDistances);
            }
            else if (Is(child, OffsetsChunk))
            {
                ReadVectorRows(child, power, disp.VectorOffsets);
            }
            else if (Is(child, AlphasChunk))
            {
                ReadFloatRows(child, power, disp.AlphaValues);
            }
            else if (Is(child, TriangleTagsChunk))
            {
                ReadTriangleTagRows(child, power, disp.TriangleTags);
            }
        }

        return disp;
    }

    /// <summary>
    /// <c>LoadDispInfoKeyCallback</c>.
    /// </summary>
    /// <param name="disp">The displacement being filled.</param>
    /// <param name="key">The key name.</param>
    /// <param name="value">Its value.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// <c>power</c> is handled by the caller and is deliberately not here.
    /// <c>elevation</c> and <c>subdiv</c> are in every Hammer-written VMF and
    /// in neither stock's handler list nor this one: <c>elevation</c> is read
    /// only under <c>VSVMFIO</c>, which vbsp is not built with, and
    /// <c>subdiv</c> has no handler at all because Hammer has already baked
    /// subdivision into the field vectors.
    /// </remarks>
    public static void ApplyKey(MapDisplacement disp, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(disp);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        if (Same(key, "uaxis"))
        {
            if (VmfValue.TryParseVector3(value, out Vec3 u))
            {
                disp.UAxis = u;
            }
        }
        else if (Same(key, "vaxis"))
        {
            if (VmfValue.TryParseVector3(value, out Vec3 v))
            {
                disp.VAxis = v;
            }
        }
        else if (Same(key, "startposition"))
        {
            if (VmfValue.TryParseVector3(value, out Vec3 start))
            {
                disp.StartPosition = start;
            }
        }
        else if (Same(key, "flags"))
        {
            disp.Flags = VmfValue.ParseInt(value);
        }
        else if (Same(key, "mintess"))
        {
            disp.MinTess = VmfValue.ParseInt(value);
        }
        else if (Same(key, "smooth"))
        {
            disp.SmoothingAngle = VmfValue.ParseFloat(value);
        }
    }

    /// <summary>
    /// Collapses the VMF's triangle tags to the BSP's:
    /// <c>LoadDispTriangleTagsKeyCallback</c>.
    /// </summary>
    /// <param name="coreTags">The <c>COREDISPTRI_TAG_*</c> word from the file.</param>
    /// <returns>A <c>DISPTRI_TAG_*</c> word.</returns>
    /// <remarks>
    /// <para>
    /// Each of walkable and buildable has three bits in the editor's encoding:
    /// the computed value, a "the author overrode this" bit, and the override's
    /// value. The compile resolves them to one bit each, so the BSP carries the
    /// ANSWER and not the argument.
    /// </para>
    /// <para>
    /// Note what is dropped: <c>DISPTRI_TAG_SURFACE</c> and
    /// <c>DISPTRI_TAG_REMOVE</c> are never set by this path, so every triangle
    /// in a vbsp-written LUMP_DISP_TRIS carries only walkable and buildable.
    /// <c>COREDISPTRI_TAG_FORCE_REMOVE_BIT</c> is read from no VMF and written
    /// to no BSP.
    /// </para>
    /// </remarks>
    public static ushort CollapseTriangleTags(int coreTags)
    {
        bool walkable = (coreTags & CoreTagWalkable) != 0;
        if ((coreTags & CoreTagForceWalkableBit) != 0)
        {
            walkable = (coreTags & CoreTagForceWalkableVal) != 0;
        }

        bool buildable = (coreTags & CoreTagBuildable) != 0;
        if ((coreTags & CoreTagForceBuildableBit) != 0)
        {
            buildable = (coreTags & CoreTagForceBuildableVal) != 0;
        }

        ushort tags = 0;
        if (walkable)
        {
            tags |= (ushort)MapFormats.Bsp.Structs.DispTriTags.Walkable;
        }

        if (buildable)
        {
            tags |= (ushort)MapFormats.Bsp.Structs.DispTriTags.Buildable;
        }

        return tags;
    }

    private static bool Is(VmfChunk chunk, string name) =>
        string.Equals(chunk.Name, name, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The row index a <c>rowN</c> key names, or -1 when the key is not a row.
    /// </summary>
    /// <remarks>
    /// <c>strnicmp(szKey, "row", 3)</c> then <c>atoi(&amp;szKey[3])</c>: the
    /// prefix match is case-insensitive and the remainder goes through
    /// <c>atoi</c>, so <c>ROW3</c> works and <c>rowx</c> is row 0.
    /// </remarks>
    private static int RowIndex(string key) =>
        key.StartsWith("row", StringComparison.OrdinalIgnoreCase)
            ? VmfValue.ParseInt(key[3..])
            : -1;

    private static void ReadFloatRows(VmfChunk chunk, int power, float[] values)
    {
        int columns = (1 << power) + 1;

        foreach (VmfKey key in chunk.Keys)
        {
            int row = RowIndex(key.Name);
            if (row < 0)
            {
                continue;
            }

            CheckRow(chunk.Name, row, columns, values.Length);

            int index = row * columns;
            foreach (Range token in Tokens(key.Value))
            {
                if (index >= values.Length)
                {
                    break;
                }

                values[index++] = VmfValue.ParseFloat(key.Value[token]);
            }
        }
    }

    private static void ReadVectorRows(VmfChunk chunk, int power, Vec3[] values)
    {
        int columns = (1 << power) + 1;

        foreach (VmfKey key in chunk.Keys)
        {
            int row = RowIndex(key.Name);
            if (row < 0)
            {
                continue;
            }

            CheckRow(chunk.Name, row, columns, values.Length);

            int index = row * columns;
            List<Range> tokens = Tokens(key.Value);

            // Stock's loop takes THREE strtok results per step and stops as
            // soon as any of them is null, so a row with a stray extra number
            // drops it rather than shifting the rest.
            for (int t = 0; t + 2 < tokens.Count; t += 3)
            {
                if (index >= values.Length)
                {
                    break;
                }

                values[index++] = new Vec3(
                    VmfValue.ParseFloat(key.Value[tokens[t]]),
                    VmfValue.ParseFloat(key.Value[tokens[t + 1]]),
                    VmfValue.ParseFloat(key.Value[tokens[t + 2]]));
            }
        }
    }

    private static void ReadTriangleTagRows(VmfChunk chunk, int power, ushort[] tags)
    {
        // NOT (1 << power) + 1: a row of triangles is one shorter than a row of
        // vertices, and there are two triangles per column.
        int columns = 1 << power;

        foreach (VmfKey key in chunk.Keys)
        {
            int row = RowIndex(key.Name);
            if (row < 0)
            {
                continue;
            }

            if (row >= columns)
            {
                throw new MapCompileException(
                    $"dispinfo {chunk.Name} has a \"row{row}\" for a power-{power} "
                    + $"displacement, which has {columns} rows of triangles.");
            }

            int index = row * columns * 2;
            foreach (Range token in Tokens(key.Value))
            {
                if (index >= tags.Length)
                {
                    break;
                }

                tags[index++] = CollapseTriangleTags(VmfValue.ParseInt(key.Value[token]));
            }
        }
    }

    private static void CheckRow(string chunkName, int row, int columns, int length)
    {
        if (row * columns >= length)
        {
            throw new MapCompileException(
                $"dispinfo {chunkName} has a \"row{row}\", which starts past the end of a "
                + $"{length}-vertex displacement.");
        }
    }

    /// <summary>
    /// Splits on spaces the way <c>strtok(buf, " ")</c> does: runs of spaces
    /// are one separator and empty tokens do not exist.
    /// </summary>
    private static List<Range> Tokens(string value)
    {
        List<Range> ranges = [];

        int i = 0;
        while (i < value.Length)
        {
            while (i < value.Length && value[i] == ' ')
            {
                i++;
            }

            int start = i;
            while (i < value.Length && value[i] != ' ')
            {
                i++;
            }

            if (i > start)
            {
                ranges.Add(new Range(start, i));
            }
        }

        return ranges;
    }
}
