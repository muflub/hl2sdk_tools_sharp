//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Diagnostics;

namespace SourceSharp.MapTools.Bsp.Detail;

/// <summary><c>DETAIL_PROP_TYPE_*</c>.</summary>
public enum DetailModelType : byte
{
    /// <summary>A studio model.</summary>
    Model = 0,

    /// <summary>A camera-facing card.</summary>
    Sprite = 1,

    /// <summary>Two crossed cards.</summary>
    ShapeCross = 2,

    /// <summary>Three cards fanned in a triangle.</summary>
    ShapeTri = 3,
}

/// <summary>One model entry of a group: <c>DetailModel_t</c>.</summary>
public sealed class DetailModel
{
    /// <summary><c>m_ModelName</c>: the <c>model</c> key, or null for a sprite.</summary>
    public string? ModelName { get; set; }

    /// <summary><c>m_Amount</c>: the running total of <c>amount</c>, renormalised when it passed 1.</summary>
    public float Amount { get; set; }

    /// <summary><c>m_MinCosAngle</c>, raised to <see cref="MaxCosAngle"/> when below it.</summary>
    public float MinCosAngle { get; set; }

    /// <summary><c>m_MaxCosAngle</c>.</summary>
    public float MaxCosAngle { get; set; }

    /// <summary><c>MODELFLAG_UPRIGHT</c>: a random yaw instead of conforming to the surface.</summary>
    public bool Upright { get; set; }

    /// <summary><c>m_Orientation</c>: <c>detailOrientation</c>.</summary>
    public int Orientation { get; set; }

    /// <summary><c>m_Type</c>.</summary>
    public DetailModelType Type { get; set; }

    /// <summary><c>m_Pos[2]</c>: upper-left and lower-right card corners.</summary>
    public (float X, float Y)[] Pos { get; } = new (float, float)[2];

    /// <summary><c>m_Tex[2]</c>: upper-left and lower-right texture coordinates.</summary>
    public (float X, float Y)[] Tex { get; } = new (float, float)[2];

    /// <summary><c>m_flRandomScaleStdDev</c>: <c>spriterandomscale</c>.</summary>
    public float RandomScaleStdDev { get; set; }

    /// <summary><c>m_ShapeSize</c>.</summary>
    public byte ShapeSize { get; set; }

    /// <summary><c>m_ShapeAngle</c>.</summary>
    public byte ShapeAngle { get; set; }

    /// <summary><c>m_SwayAmount</c>.</summary>
    public byte SwayAmount { get; set; }
}

/// <summary>One alpha band of a detail type: <c>DetailObjectGroup_t</c>.</summary>
public sealed class DetailGroup
{
    /// <summary><c>m_Alpha</c>.</summary>
    public float Alpha { get; set; }

    /// <summary><c>m_Models</c>.</summary>
    public List<DetailModel> Models { get; } = [];
}

/// <summary>One detail type: <c>DetailObject_t</c>.</summary>
public sealed class DetailType
{
    /// <summary><c>m_Name</c>, matched against <c>%detailtype</c> CASE-SENSITIVELY (a <c>CUtlSymbol</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary><c>m_Density</c>.</summary>
    public float Density { get; set; }

    /// <summary><c>m_Groups</c>, ascending alpha.</summary>
    public List<DetailGroup> Groups { get; } = [];
}

/// <summary>
/// <c>s_DetailObjectDict</c>: <c>detail.vbsp</c> as vbsp's own parser reads it
/// (<c>ParseDetailObjectFile</c>/<c>ParseDetailGroup</c>.
/// </summary>
/// <remarks>
/// <para>
/// A separate port from MapFormats' <c>DetailObjectFile</c>, which is a
/// reader of the file's meaning and differs from the compiler in four
/// observable ways, each reproduced here:
/// </para>
/// <list type="number">
/// <item><description>
/// A group of EQUAL alpha is inserted BEFORE the existing ones
/// (the backward scan stops at the first STRICTLY smaller
/// alpha and inserts after it), so equal-alpha groups end up in reverse file
/// order — and <c>SelectGroup</c> picks the LAST, so this decides which group
/// a flat face draws from.
/// </description></item>
/// <item><description>
/// <c>minAngle</c> below <c>maxAngle</c> is clamped.
/// </description></item>
/// <item><description>
/// A sprite's card and texture rectangles, which the lump's
/// sprite dictionary is made of.
/// </description></item>
/// <item><description>
/// Type names match case-sensitively (<c>CUtlSymbol</c>'s table is built
/// Case-sensitive).
/// </description></item>
/// </list>
/// </remarks>
public sealed class DetailDictionary
{
    /// <summary>The types, in file order.</summary>
    public List<DetailType> Types { get; } = [];

    /// <summary>
    /// <c>s_DetailObjectDict.Find</c>: the first type of exactly this name.
    /// </summary>
    /// <param name="name">The <c>%detailtype</c> value.</param>
    /// <returns>The index, or -1.</returns>
    public int Find(string name) => Types.FindIndex(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// <c>ParseDetailObjectFile</c> over a parsed file: every sectioned key of
    /// the root is a type, every sectioned key of a type a group.
    /// </summary>
    /// <param name="root">The file's first root.</param>
    /// <returns>The dictionary.</returns>
    /// <exception cref="MapCompileException">A malformed <c>sprite</c> key: fatal in stock.</exception>
    public static DetailDictionary Parse(KeyValuesNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        DetailDictionary dictionary = new();
        foreach (KeyValuesNode typeNode in root.Children)
        {
            if (typeNode.Children.Count == 0)
            {
                continue;
            }

            DetailType type = new() { Name = typeNode.Name, Density = typeNode.GetFloat("density", 0.0f) };
            dictionary.Types.Add(type);

            foreach (KeyValuesNode groupNode in typeNode.Children)
            {
                if (groupNode.Children.Count > 0)
                {
                    ParseGroup(type, groupNode);
                }
            }
        }

        return dictionary;
    }

    private static void ParseGroup(DetailType type, KeyValuesNode groupNode)
    {
        float alpha = groupNode.GetFloat("alpha", 1.0f);

        int i = type.Groups.Count;
        while (--i >= 0)
        {
            if (alpha > type.Groups[i].Alpha)
            {
                break;
            }
        }

        DetailGroup group = new() { Alpha = alpha };
        type.Groups.Insert(i + 1, group);

        float total = 0.0f;
        foreach (KeyValuesNode node in groupNode.Children)
        {
            if (node.Children.Count == 0)
            {
                continue;
            }

            DetailModel model = new();
            group.Models.Add(model);

            string? modelName = StockKeyValues.GetString(node, "model");
            if (modelName is not null)
            {
                model.ModelName = modelName;
                model.Type = DetailModelType.Model;
            }
            else
            {
                string? sprite = StockKeyValues.GetString(node, "sprite");
                if (sprite is not null)
                {
                    ParseSprite(model, node, sprite);
                }

                // Neither "model" nor "sprite": stock leaves m_Type and the
                // rectangles uninitialised and PlaceDetail's default case
                // treats it as a sprite. A zeroed card is the defined version.
                else
                {
                    model.Type = DetailModelType.Sprite;
                }
            }

            model.Amount = node.GetFloat("amount", 1.0f) + total;
            total = model.Amount;

            model.Upright = node.GetInt("upright", 0) != 0;

            // cos(minAngle * M_PI / 180.f): float times a double constant, in
            // double, stored as float.
            float minAngle = node.GetFloat("minAngle", 180f);
            float maxAngle = node.GetFloat("maxAngle", 180f);
            model.MinCosAngle = (float)Math.Cos(minAngle * Math.PI / 180.0f);
            model.MaxCosAngle = (float)Math.Cos(maxAngle * Math.PI / 180.0f);
            model.Orientation = node.GetInt("detailOrientation", 0);

            if (model.MinCosAngle < model.MaxCosAngle)
            {
                model.MinCosAngle = model.MaxCosAngle;
            }
        }

        if (total > 1.0f)
        {
            foreach (DetailModel model in group.Models)
            {
                model.Amount /= total;
            }
        }
    }

    /// <summary>Fills a model's sprite fields from its key block.</summary>
    private static void ParseSprite(DetailModel model, KeyValuesNode node, string sprite)
    {
        string? shape = StockKeyValues.GetString(node, "sprite_shape");
        model.Type = shape is null ? DetailModelType.Sprite
            : string.Equals(shape, "cross", StringComparison.OrdinalIgnoreCase) ? DetailModelType.ShapeCross
            : string.Equals(shape, "tri", StringComparison.OrdinalIgnoreCase) ? DetailModelType.ShapeTri
            : DetailModelType.Sprite;

        float x = 0, y = 0, width = 64, height = 64, textureSize = 512;
        float[] values = [x, y, width, height, textureSize];
        int valid = ScanFloats(sprite, values);
        (x, y, width, height, textureSize) = (values[0], values[1], values[2], values[3], values[4]);

        if (valid != 5 || textureSize == 0)
        {
            throw new MapCompileException(
                $"{SurfaceContentDiagnostics.DetailInvalidSprite}: Invalid arguments to \"sprite\" in detail.vbsp (model {model.ModelName})!");
        }

        model.Tex[0] = ((x + 0.5f) / textureSize, (y + 0.5f) / textureSize);
        model.Tex[1] = ((x + width - 0.5f) / textureSize, (y + height - 0.5f) / textureSize);

        model.Pos[0] = (-10, 20);
        model.Pos[1] = (10, 0);

        string? spriteSize = StockKeyValues.GetString(node, "spritesize");
        if (spriteSize is not null)
        {
            // sscanf into the SAME four locals: a short string keeps the
            // sprite key's values for the fields it does not reach.
            float[] size = [x, y, width, height];
            ScanFloats(spriteSize, size);
            (x, y, width, height) = (size[0], size[1], size[2], size[3]);

            float ox = width * x;
            float oy = height * y;

            model.Pos[0] = (-ox, height - oy);
            model.Pos[1] = (width - ox, -oy);
        }

        model.RandomScaleStdDev = node.GetFloat("spriterandomscale", 0.0f);

        // clamp(float, 0.0, 1.0) is a double; 255.0 * it, truncated.
        float sway = (float)Math.Clamp((double)node.GetFloat("sway", 0.0f), 0.0, 1.0);
        model.SwayAmount = (byte)(255.0 * sway);

        model.ShapeAngle = unchecked((byte)node.GetInt("shape_angle", 0));

        float shapeSize = (float)Math.Clamp((double)node.GetFloat("shape_size", 0.0f), 0.0, 1.0);
        model.ShapeSize = (byte)(255.0 * shapeSize);
    }

    /// <summary>
    /// <c>sscanf(s, "%f %f ...")</c>: whitespace-separated floats into the
    /// array, stopping at the first that does not parse; the rest keep their
    /// values.
    /// </summary>
    /// <returns>How many were assigned.</returns>
    internal static int ScanFloats(string text, float[] into)
    {
        int position = 0;
        int count = 0;

        while (count < into.Length)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }

            int start = position;
            if (position < text.Length && (text[position] == '+' || text[position] == '-'))
            {
                position++;
            }

            int digits = 0;
            while (position < text.Length && char.IsAsciiDigit(text[position]))
            {
                position++;
                digits++;
            }

            if (position < text.Length && text[position] == '.')
            {
                position++;
                while (position < text.Length && char.IsAsciiDigit(text[position]))
                {
                    position++;
                    digits++;
                }
            }

            if (digits == 0)
            {
                break;
            }

            if (position < text.Length && (text[position] == 'e' || text[position] == 'E'))
            {
                int save = position;
                position++;
                if (position < text.Length && (text[position] == '+' || text[position] == '-'))
                {
                    position++;
                }

                int expDigits = 0;
                while (position < text.Length && char.IsAsciiDigit(text[position]))
                {
                    position++;
                    expDigits++;
                }

                if (expDigits == 0)
                {
                    position = save;
                }
            }

            into[count++] = float.Parse(text[start..position], NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        return count;
    }
}
