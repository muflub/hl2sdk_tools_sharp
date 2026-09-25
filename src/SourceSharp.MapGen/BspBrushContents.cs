namespace SourceSharp.MapGen;

/// <summary>
/// The <c>CONTENTS_*</c> bits vbsp gave a brush entity's brushes, read straight
/// off a compiled map.
///
/// <para>
/// WHY THIS EXISTS. <see cref="BspEntities"/> answers "is this entity in the
/// map"; it cannot answer "and is its brush the shape the game will ask it to
/// be". Those are different questions and the second one shipped wrong for a
/// whole phase: <c>ss_sandbox</c>'s two dust volumes were textured with
/// <c>TOOLS/TOOLSINVISIBLE</c>, whose <c>%compileInvisible</c> clears
/// <c>CONTENTS_SOLID</c> and sets <c>CONTENTS_GRATE</c>, so the
/// dust volume's particle spawner tested
/// <c>GetPointContents_Collideable(...) &amp; CONTENTS_SOLID</c>
/// and was false for every point inside the brush. 4,050 spawn calls, 40,500 point
/// tests, zero motes — and every layer above the map was working. Only the
/// compiled brush's own contents word could tell that apart from a broken
/// query, which is what this reads.
/// </para>
///
/// <para>
/// HOW A BRUSH ENTITY'S BRUSHES ARE FOUND. A brush entity carries
/// <c>"model" "*N"</c>; submodel N in the model lump gives a headnode; the node
/// tree's leaves carry ranges into the leaf-brush lump; each of those is an
/// index into the brush lump, whose third int is the contents word. Five lumps
/// and no geometry: the shape is never reconstructed, only the classification
/// vbsp wrote down.
/// </para>
///
/// <para>
/// The one version-dependent field is the leaf. Before BSP v20 a
/// leaf record carried an inline ambient-lighting cube and was 56 bytes;
/// from v20 it is 32. A game loading the map decides by the LEAF LUMP'S OWN version field
/// rather than the file's (<c>version == 0</c> is the old shape), and so does
/// this — then checks the lump divides exactly by the size it chose, because a
/// stride that is wrong by 24 bytes still parses and answers confident
/// nonsense.
/// </para>
/// </summary>
public static class BspBrushContents
{
    /// <summary>The four bytes every Source .bsp starts with.</summary>
    private const int VbspIdent = ('P' << 24) | ('S' << 16) | ('B' << 8) | 'V';

    private const int HeaderLumps = 64;

    private const int LumpNodes = 5;
    private const int LumpLeafs = 10;
    private const int LumpModels = 14;
    private const int LumpLeafBrushes = 17;
    private const int LumpBrushes = 18;

    private const int NodeStride = 32;
    private const int ModelStride = 48;
    private const int BrushStride = 12;
    private const int LeafBrushStride = 2;

    private const int LeafStrideModern = 32;
    private const int LeafStrideWithAmbientCube = 56;

    /// <summary>Byte offset of a leaf's first-leaf-brush/count pair.</summary>
    private const int LeafBrushRangeOffset = 24;

    /// <summary>
    /// <c>CONTENTS_SOLID</c>. Kept here as a literal because the point of the
    /// test that reads it is to pin the number the game relies on, and a symbol
    /// imported from whatever header declares it would move with that header.
    /// </summary>
    public const int ContentsSolid = 0x1;

    /// <summary><c>CONTENTS_GRATE</c> — what <c>%compileInvisible</c> leaves behind.</summary>
    public const int ContentsGrate = 0x8;

    /// <summary>One brush of a submodel: its index in the brush lump and its contents word.</summary>
    public readonly record struct Brush(int Index, int Contents);

    /// <summary>
    /// The brushes of submodel <paramref name="modelIndex"/>, in ascending
    /// brush-lump order.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The file is not a VBSP, a lump is compressed or out of bounds, a lump's
    /// length is not a whole number of records, or an index leaves its lump.
    /// Every one of those is refused rather than answering an empty list: "this
    /// model has no brushes" is a finding a caller will act on, and it must not
    /// be reachable from a reader that misunderstood the file.
    /// </exception>
    public static IReadOnlyList<Brush> BrushesOfModel(string bspPath, int modelIndex)
    {
        byte[] bytes = File.ReadAllBytes(bspPath);

        if (bytes.Length < 8 + (HeaderLumps * 16))
            throw new InvalidDataException($"{bspPath} is too short to be a .bsp ({bytes.Length} bytes)");

        if (BitConverter.ToInt32(bytes, 0) != VbspIdent)
            throw new InvalidDataException($"{bspPath} does not start with VBSP");

        ReadOnlySpan<byte> models = Lump(bytes, bspPath, LumpModels, out _);
        ReadOnlySpan<byte> nodes = Lump(bytes, bspPath, LumpNodes, out _);
        ReadOnlySpan<byte> leafs = Lump(bytes, bspPath, LumpLeafs, out int leafLumpVersion);
        ReadOnlySpan<byte> leafBrushes = Lump(bytes, bspPath, LumpLeafBrushes, out _);
        ReadOnlySpan<byte> brushes = Lump(bytes, bspPath, LumpBrushes, out _);

        int leafStride = leafLumpVersion == 0 ? LeafStrideWithAmbientCube : LeafStrideModern;

        Divides(bspPath, "model", models.Length, ModelStride);
        Divides(bspPath, "node", nodes.Length, NodeStride);
        Divides(bspPath, "leaf", leafs.Length, leafStride);
        Divides(bspPath, "leafbrush", leafBrushes.Length, LeafBrushStride);
        Divides(bspPath, "brush", brushes.Length, BrushStride);

        int modelCount = models.Length / ModelStride;

        if (modelIndex < 0 || modelIndex >= modelCount)
            throw new InvalidDataException(
                $"{bspPath} has {modelCount} submodel(s); *{modelIndex} is not one of them");

        int headNode = BitConverter.ToInt32(models[((modelIndex * ModelStride) + 36)..]);

        var found = new SortedSet<int>();
        var walked = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(headNode);

        while (pending.Count > 0)
        {
            int at = pending.Pop();

            if (at >= 0)
            {
                if (at >= nodes.Length / NodeStride)
                    throw new InvalidDataException($"{bspPath}: node {at} is outside the node lump");

                // A tree is a tree, but a malformed file need not be: a child
                // that points back at an ancestor would spin here forever.
                if (!walked.Add(at))
                    continue;

                pending.Push(BitConverter.ToInt32(nodes[((at * NodeStride) + 4)..]));
                pending.Push(BitConverter.ToInt32(nodes[((at * NodeStride) + 8)..]));
                continue;
            }

            int leaf = -1 - at;

            if (leaf < 0 || leaf >= leafs.Length / leafStride)
                throw new InvalidDataException($"{bspPath}: leaf {leaf} is outside the leaf lump");

            int first = BitConverter.ToUInt16(leafs[((leaf * leafStride) + LeafBrushRangeOffset)..]);
            int count = BitConverter.ToUInt16(leafs[((leaf * leafStride) + LeafBrushRangeOffset + 2)..]);

            for (int i = 0; i < count; i++)
            {
                int slot = first + i;

                if (slot >= leafBrushes.Length / LeafBrushStride)
                    throw new InvalidDataException(
                        $"{bspPath}: leaf {leaf} names leafbrush {slot}, past the lump");

                found.Add(BitConverter.ToUInt16(leafBrushes[(slot * LeafBrushStride)..]));
            }
        }

        var answer = new List<Brush>(found.Count);

        foreach (int index in found)
        {
            if (index >= brushes.Length / BrushStride)
                throw new InvalidDataException($"{bspPath}: brush {index} is outside the brush lump");

            answer.Add(new Brush(index, BitConverter.ToInt32(brushes[((index * BrushStride) + 8)..])));
        }

        return answer;
    }

    /// <summary>
    /// The brushes of every entity of <paramref name="className"/> that carries
    /// a <c>"model" "*N"</c> key, keyed by the entity's <c>targetname</c> — or
    /// by <c>*N</c> where it has none, so an unnamed volume is still legible.
    /// </summary>
    /// <remarks>
    /// An entity of that classname with NO brush model is a finding in itself
    /// and is reported as an empty brush list rather than skipped: a func_dust
    /// with no shape is exactly the earlier defect 8e-render closed, and it
    /// must not read the same as "no such entity".
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<Brush>> BrushesOfClass(
        string bspPath, string className)
    {
        var answer = new Dictionary<string, IReadOnlyList<Brush>>(StringComparer.Ordinal);

        foreach (DeclaredEntity entity in BspEntities.Read(bspPath))
        {
            if (!string.Equals(entity.ClassName, className, StringComparison.OrdinalIgnoreCase))
                continue;

            entity.KeyValues.TryGetValue("model", out string? model);

            if (model is null || model.Length < 2 || model[0] != '*'
                || !int.TryParse(model[1..], out int index))
            {
                answer[Name(entity, model)] = [];
                continue;
            }

            answer[Name(entity, model)] = BrushesOfModel(bspPath, index);
        }

        return answer;
    }

    private static string Name(DeclaredEntity entity, string? model)
        => !string.IsNullOrEmpty(entity.TargetName) ? entity.TargetName
         : !string.IsNullOrEmpty(model) ? model!
         : entity.ClassName;

    private static ReadOnlySpan<byte> Lump(byte[] bytes, string bspPath, int lump, out int version)
    {
        int at = 8 + (lump * 16);

        int offset = BitConverter.ToInt32(bytes, at);
        int length = BitConverter.ToInt32(bytes, at + 4);
        version = BitConverter.ToInt32(bytes, at + 8);
        int fourCc = BitConverter.ToInt32(bytes, at + 12);

        if (fourCc != 0)
            throw new InvalidDataException(
                $"{bspPath}'s lump {lump} is compressed; this reader handles only plain lumps");

        if (offset < 0 || length < 0 || offset + length > bytes.Length)
            throw new InvalidDataException(
                $"{bspPath}'s lump {lump} is out of bounds (offset {offset}, length {length})");

        return bytes.AsSpan(offset, length);
    }

    private static void Divides(string bspPath, string what, int length, int stride)
    {
        if (length % stride != 0)
            throw new InvalidDataException(
                $"{bspPath}'s {what} lump is {length} bytes, not a whole number of {stride}-byte records");
    }
}
