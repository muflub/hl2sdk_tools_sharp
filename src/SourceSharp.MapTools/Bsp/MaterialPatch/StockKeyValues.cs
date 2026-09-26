//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;
using SourceSharp.MapFormats.Text;

namespace SourceSharp.MapTools.Bsp.MaterialPatch;

/// <summary>
/// The handful of <c>KeyValues</c> behaviours vbsp's material patcher depends
/// on, reproduced on <see cref="KeyValuesNode"/> where the general-purpose
/// Model differs from the reference implementation.
/// </summary>
public static class StockKeyValues
{
    /// <summary>
    /// <c>GetString(key, NULL)</c>: the first child of that name,
    /// case-insensitively, when it holds a value; null when it is absent OR a
    /// section (— a <c>TYPE_NONE</c> key falls
    /// to <c>default: return defaultValue</c>).
    /// </summary>
    /// <param name="node">The section to search.</param>
    /// <param name="key">The key.</param>
    /// <returns>The value, or null.</returns>
    public static string? GetString(KeyValuesNode node, string key)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(key);

        // A section's Value is null, which is exactly TYPE_NONE's answer.
        return node.Find(key)?.Value;
    }

    /// <summary>
    /// <c>SetString</c>: <c>FindKey(key, true)</c> then assign. The FIRST
    /// match is reused whatever it is, so setting a key that already names a
    /// section gives that section a value too (and the writer, which prefers
    /// <c>m_pSub</c>, still writes it as a section); a missing key is
    /// appended at the end.
    /// </summary>
    /// <param name="node">The section.</param>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <returns>The key's node.</returns>
    public static KeyValuesNode SetString(KeyValuesNode node, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        KeyValuesNode? found = node.Find(key);
        if (found is null)
        {
            found = new KeyValuesNode(key);
            node.Children.Add(found);
        }

        found.Value = value;
        return found;
    }

    /// <summary>
    /// <c>FindKey(key, true)</c>: the first match, or a new empty key appended.
    /// </summary>
    /// <param name="node">The section.</param>
    /// <param name="key">The key.</param>
    /// <returns>The key's node.</returns>
    public static KeyValuesNode FindOrCreate(KeyValuesNode node, string key)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(key);

        KeyValuesNode? found = node.Find(key);
        if (found is not null)
        {
            return found;
        }

        found = new KeyValuesNode(key);
        node.Children.Add(found);
        return found;
    }

    /// <summary>
    /// Whether a child is a "true sub key": <c>m_iDataType == TYPE_NONE</c>,
    /// which is what <c>GetFirstTrueSubKey</c> walks.
    /// An EMPTY section is one too; a key with a value is not.
    /// </summary>
    /// <param name="node">The child.</param>
    /// <returns>True for a section, empty or not.</returns>
    public static bool IsTrueSubKey(KeyValuesNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Value is null;
    }

    /// <summary>
    /// <c>RecursiveSaveToFile(buf, 0)</c> into a TEXT <c>CUtlBuffer</c>, with
    /// Every default argument.
    /// </summary>
    /// <param name="root">The root key; always written as a section.</param>
    /// <returns>The text, LF line ends (the pak adds the CRs).</returns>
    /// <remarks>
    /// <para>
    /// The one behaviour that matters and that a general writer gets wrong:
    /// <c>SaveKeyToFile</c> recurses only when <c>m_pSub</c> is set, and
    /// otherwise writes only a <c>TYPE_STRING</c> with a non-empty value. So an
    /// EMPTY section below the root is not written at all, while a section
    /// whose children are all empty sections is written as <c>{ }</c> with
    /// nothing inside. Measured, not inferred: stock's patch of
    /// <c>nature/water_canals_cheap001</c> in <c>l2_cubemap_on_water_and_patch</c>
    /// has an empty <c>"Proxies" { }</c> and no <c>"Water_DX60"</c>, both
    /// created by <c>CreateMaterialPatchRecursive</c> as empty keys.
    /// </para>
    /// <para>
    /// Scalars are indent, quoted name, the four bytes <c>"\t\t"</c>, quoted
    /// value. A double quote in either is escaped; a
    /// backslash is not (<c>m_bHasEscapeSequences</c> is false for a tree the
    /// patcher built).
    /// </para>
    /// </remarks>
    public static string Save(KeyValuesNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        StringBuilder text = new();
        SaveSection(text, root, 0);
        return text.ToString();
    }

    /// <summary><see cref="Save"/> as Latin-1 bytes.</summary>
    /// <param name="root">The root key.</param>
    /// <returns>The bytes.</returns>
    public static byte[] SaveBytes(KeyValuesNode root) => Encoding.Latin1.GetBytes(Save(root));

    private static void SaveSection(StringBuilder text, KeyValuesNode node, int indent)
    {
        text.Append('\t', indent).Append('"').Append(Escape(node.Name)).Append("\"\n");
        text.Append('\t', indent).Append("{\n");

        foreach (KeyValuesNode child in node.Children)
        {
            if (child.Children.Count > 0)
            {
                SaveSection(text, child, indent + 1);
                continue;
            }

            if (!string.IsNullOrEmpty(child.Value))
            {
                text.Append('\t', indent + 1)
                    .Append('"').Append(Escape(child.Name)).Append("\"\t\t\"")
                    .Append(Escape(child.Value)).Append("\"\n");
            }
        }

        text.Append('\t', indent).Append("}\n");
    }

    private static string Escape(string text) => text.Replace("\"", "\\\"", StringComparison.Ordinal);
}
