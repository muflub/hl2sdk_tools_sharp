namespace SourceSharp.MapFormats.Text;

/// <summary>
/// A <c>detail.vbsp</c> detail-prop definition file: the KeyValues schema the
/// reference detail loader reads.
/// </summary>
/// <remarks>
/// <para>
/// Three levels: a detail TYPE, whose name is what a material's
/// <c>%detailtype</c> variable is matched against; GROUPS inside it, whose
/// names are never read and which exist only to carry an <c>alpha</c>; and
/// MODELS inside those.
/// </para>
/// <para>
/// Missing or malformed files are handled in SILENCE -- the reference
/// loader's load branch has no <c>else</c> -- so a typo in the filename
/// produces a map with no detail props and no message at all.
/// </para>
/// </remarks>
public sealed class DetailObjectFile
{
    /// <summary>
    /// The default filename the reference loader falls back to.
    /// </summary>
    /// <remarks>
    /// Overridable per map through <c>worldspawn</c>'s <c>detailvbsp</c> key,
    /// which falls back to this both when the key is empty and when there is
    /// no worldspawn.
    /// </remarks>
    public const string DefaultFileName = "detail.vbsp";

    /// <summary>
    /// The key on <c>worldspawn</c> that names a different file.
    /// </summary>
    public const string WorldspawnOverrideKey = "detailvbsp";

    /// <summary>
    /// The constant that turns a density into a sample count.
    /// </summary>
    /// <remarks>
    /// The reference sample count is <c>area * density * 0.000001</c> -- a
    /// TRUNCATION to int, not a rounding, and the same literal at both the
    /// flat-face and displacement sites.
    /// </remarks>
    public const double DensityToSamples = 0.000001;

    /// <summary>The detail types, in file order.</summary>
    public IList<DetailObjectType> Types { get; } = [];

    /// <summary>The first type with this name, or null.</summary>
    /// <param name="name">The type name, matched without regard to case.</param>
    /// <returns>The type, or null.</returns>
    public DetailObjectType? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Types.FirstOrDefault(
            t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads a detail file from a stream.</summary>
    /// <param name="stream">The bytes of the file, read to its end.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public static async Task<DetailObjectFile> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        KeyValuesDocument document =
            await KeyValuesDocument.ReadAsync(stream, null, cancellationToken).ConfigureAwait(false);

        return FromKeyValues(document);
    }

    /// <summary>Parses a detail file from decoded text.</summary>
    /// <param name="text">The whole file.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static async ValueTask<DetailObjectFile> ParseAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        KeyValuesDocument document =
            await KeyValuesDocument.ParseAsync(text, null, cancellationToken).ConfigureAwait(false);

        return FromKeyValues(document);
    }

    /// <summary>Builds the schema from an already-parsed KeyValues tree.</summary>
    /// <param name="document">The parsed file.</param>
    /// <returns>The detail definitions.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <exception cref="DetailObjectFileException">
    /// A <c>sprite</c> value does not carry five numbers, or its texture size
    /// is zero.
    /// </exception>
    public static DetailObjectFile FromKeyValues(KeyValuesDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        DetailObjectFile file = new();
        KeyValuesNode? root = document.Root;

        if (root is null)
        {
            return file;
        }

        foreach (KeyValuesNode typeNode in root.Children)
        {
            // A top-level LEAF key is silently ignored. Only sections become
            // detail types.
            if (!typeNode.IsSection)
            {
                continue;
            }

            // The type's NAME is the key, and density is read HERE,
            // at the type level, not per group.
            DetailObjectType type = new(typeNode.Name, typeNode.GetFloat("density", 0.0f));

            foreach (KeyValuesNode groupNode in typeNode.Children)
            {
                // Again, only sections.
                if (!groupNode.IsSection)
                {
                    continue;
                }

                type.Groups.Add(ParseGroup(groupNode));
            }

            // Groups are kept sorted by ASCENDING alpha, inserted
            // after the first group more transparent than this one. A stable
            // insertion sort, so groups of equal alpha keep file order.
            SortGroupsByAlpha(type.Groups);

            file.Types.Add(type);
        }

        return file;
    }

    private static DetailObjectGroup ParseGroup(KeyValuesNode groupNode)
    {
        // alpha defaults to 1 when the key is absent.
        DetailObjectGroup group = new(groupNode.GetFloat("alpha", 1.0f));

        float totalAmount = 0f;

        foreach (KeyValuesNode modelNode in groupNode.Children)
        {
            // Only sections are models.
            if (!modelNode.IsSection)
            {
                continue;
            }

            DetailObjectModel model = ParseModel(modelNode, ref totalAmount);
            group.Models.Add(model);
        }

        // The amounts are renormalised ONLY if the running total exceeds 1.
        // When it is under 1 the remainder is empty space: SelectDetail
        // rolls a random number and returns -1 -- emit nothing --
        // when it falls past the last model's cumulative amount.
        if (totalAmount > 1.0f)
        {
            for (int i = 0; i < group.Models.Count; i++)
            {
                group.Models[i] = group.Models[i] with
                {
                    CumulativeAmount = group.Models[i].CumulativeAmount / totalAmount,
                };
            }
        }

        return group;
    }

    private static DetailObjectModel ParseModel(KeyValuesNode node, ref float totalAmount)
    {
        // "amount" is a RUNNING SUM, so the stored value is a
        // cumulative distribution point and not the per-model weight.
        float amount = node.GetFloat("amount", 1.0f) + totalAmount;
        totalAmount = amount;

        string? modelName = node.GetString("model");

        // "model" WINS. When it is present the whole sprite branch is never
        // entered, so sprite, spritesize, sway and the shape keys in the same
        // block are not read at all.
        if (modelName is not null)
        {
            return new DetailObjectModel(
                Type: DetailPropType.Model,
                ModelName: modelName,
                CumulativeAmount: amount,
                Upright: node.GetInt("upright", 0) != 0,
                MinCosAngle: CosineOfDegrees(node.GetFloat("minAngle", 180f)),
                MaxCosAngle: CosineOfDegrees(node.GetFloat("maxAngle", 180f)),
                Orientation: node.GetInt("detailOrientation", 0),
                SwayAmount: 0,
                ShapeAngle: 0,
                ShapeSize: 0,
                RandomScaleStdDev: 0f);
        }

        // sprite_shape selects the procedural type, case
        // insensitively; ANY other non-empty string, and an absent key, mean a
        // plain card sprite.
        string? shape = node.GetString("sprite_shape");
        DetailPropType type = shape switch
        {
            not null when string.Equals(shape, "cross", StringComparison.OrdinalIgnoreCase) =>
                DetailPropType.ShapeCross,
            not null when string.Equals(shape, "tri", StringComparison.OrdinalIgnoreCase) =>
                DetailPropType.ShapeTri,
            _ => DetailPropType.Sprite,
        };

        // ALL FIVE sprite fields are mandatory despite the defaults below, and
        // a texture size of zero is fatal too. Only checked when a "sprite"
        // key is present at all.
        string? sprite = node.GetString("sprite");
        if (sprite is not null)
        {
            double[] numbers = ScanNumbers(sprite, 5);
            if (numbers.Length != 5 || numbers[4] == 0)
            {
                throw new DetailObjectFileException(
                    $"Invalid arguments to \"sprite\" in {DefaultFileName} (model {modelName})!");
            }
        }

        // Both sway and shape_size are clamped to 0..1 and quantised by
        // multiplying by 255 and truncating.
        float sway = Math.Clamp(node.GetFloat("sway", 0.0f), 0.0f, 1.0f);
        float shapeSize = Math.Clamp(node.GetFloat("shape_size", 0.0f), 0.0f, 1.0f);

        return new DetailObjectModel(
            Type: type,
            ModelName: null,
            CumulativeAmount: amount,
            Upright: node.GetInt("upright", 0) != 0,
            MinCosAngle: CosineOfDegrees(node.GetFloat("minAngle", 180f)),
            MaxCosAngle: CosineOfDegrees(node.GetFloat("maxAngle", 180f)),
            Orientation: node.GetInt("detailOrientation", 0),
            SwayAmount: (byte)(255.0 * sway),

            // shape_angle is read as an INT with NO CLAMP and stored in an
            // unsigned char, so 360 wraps to 104.
            ShapeAngle: (byte)node.GetInt("shape_angle", 0),
            ShapeSize: (byte)(255.0 * shapeSize),
            RandomScaleStdDev: node.GetFloat("spriterandomscale", 0.0f));
    }

    private static float CosineOfDegrees(float degrees) =>
        // cos(angle * pi / 180). The default of
        // 180 degrees gives -1, which restricts nothing.
        (float)Math.Cos(degrees * Math.PI / 180.0);

    private static void SortGroupsByAlpha(IList<DetailObjectGroup> groups)
    {
        // The reference placement scan goes BACKWARDS for the first group
        // strictly less transparent and inserts after it. That is a stable
        // insertion sort on ascending alpha.
        List<DetailObjectGroup> sorted = [];

        foreach (DetailObjectGroup group in groups)
        {
            int index = sorted.Count;
            while (index > 0 && sorted[index - 1].Alpha > group.Alpha)
            {
                index--;
            }

            sorted.Insert(index, group);
        }

        groups.Clear();
        foreach (DetailObjectGroup group in sorted)
        {
            groups.Add(group);
        }
    }

    private static double[] ScanNumbers(string text, int maximum)
    {
        List<double> values = [];
        int index = 0;

        while (values.Count < maximum)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            int start = index;
            if (index < text.Length && (text[index] == '+' || text[index] == '-'))
            {
                index++;
            }

            int digits = 0;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                index++;
                digits++;
            }

            if (index < text.Length && text[index] == '.')
            {
                index++;
                while (index < text.Length && char.IsAsciiDigit(text[index]))
                {
                    index++;
                    digits++;
                }
            }

            if (digits == 0)
            {
                break;
            }

            values.Add(double.Parse(
                text[start..index],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture));
        }

        return [.. values];
    }
}
