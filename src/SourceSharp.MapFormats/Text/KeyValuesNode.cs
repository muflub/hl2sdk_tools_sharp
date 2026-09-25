using System.Globalization;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// One node of a KeyValues tree: a port of the parts of <c>KeyValues</c>
/// (<c>src/tier1/KeyValues.cpp</c>) that the text format needs.
/// </summary>
/// <remarks>
/// <para>
/// A node is either a SECTION -- it has children and no value -- or a SCALAR,
/// which has a value and no children. The C++ represents both with one class
/// and distinguishes them by <c>m_pSub</c> being non-null; the serialiser tests
/// exactly that (<c>KeyValues.cpp:859</c>), and the consequence is worth
/// stating because it is a data-loss case: a node that somehow has BOTH loses
/// its value on save.
/// </para>
/// <para>
/// Duplicate names are kept, never merged. <c>RecursiveLoadFromBuffer</c> says
/// so at <c>KeyValues.cpp:2467-2469</c>: "this could potentially cause some
/// duplication, but that's what we want sometimes". A surfaceproperties
/// manifest is a list of repeated <c>file</c> keys and would collapse to one
/// entry under any merging model.
/// </para>
/// </remarks>
public sealed class KeyValuesNode
{
    /// <summary>
    /// <c>KEYVALUES_TOKEN_SIZE</c> (<c>src/tier1/KeyValues.cpp:44</c>): the
    /// shared token buffer, so a token keeps 4095 characters.
    /// </summary>
    /// <remarks>
    /// Overflow is asymmetric in the C++ and both halves are reproduced where
    /// they are observable: a quoted token is truncated SILENTLY
    /// (<c>src/tier1/utlbuffer.cpp:783-794</c>) while an unquoted one reports
    /// <c>" ReadToken overflow"</c> once and then truncates
    /// (<c>KeyValues.cpp:606-614</c>).
    /// </remarks>
    public const int MaxTokenLength = 4096;

    /// <summary>Creates a node.</summary>
    /// <param name="name">The key name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public KeyValuesNode(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <summary>
    /// The key name, with its original casing. Lookups ignore case -- names are
    /// interned through a case-insensitive symbol table
    /// (<c>vstdlib/KeyValuesSystem.cpp:233,238</c>) -- but the spelling is kept
    /// for output.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// The scalar value, or null when this node is a section.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>The child nodes, in file order.</summary>
    public IList<KeyValuesNode> Children { get; } = [];

    /// <summary>
    /// True when this node was written as a <c>{ }</c> block, whether or not
    /// anything is inside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tracked explicitly rather than inferred from
    /// <see cref="Children"/> being non-empty, because the C++ distinguishes
    /// the two and the difference is observable. An EMPTY block has
    /// <c>m_iDataType == TYPE_NONE</c> and <c>m_pSub == NULL</c>: the patch
    /// machinery's subkey case tests the DATA TYPE
    /// (<c>materialsystem/cmaterial.cpp:3286-3297</c>) and so still recurses
    /// into it, which is the only way the
    /// <c>__vmtpatchdummy</c> at <c>:3302-3307</c> can ever be reached. A
    /// model that called an empty block a scalar would never produce one.
    /// </para>
    /// <para>
    /// The serialiser goes the other way and tests <c>m_pSub</c>
    /// (<c>KeyValues.cpp:859</c>), so an empty section writes NOTHING at all --
    /// it falls to the <c>default: break;</c> at <c>:959-960</c>. Two different
    /// tests over the same node, and this port needs both.
    /// </para>
    /// </remarks>
    public bool IsBlock { get; set; }

    /// <summary>
    /// True when this node acts as a section: it was written as a block, or it
    /// has children.
    /// </summary>
    /// <remarks>
    /// <c>SaveKeyToFile</c> tests <c>m_pSub</c> first
    /// (<c>KeyValues.cpp:859-862</c>), so a node with both children and a value
    /// writes as a section and the value is LOST.
    /// </remarks>
    public bool IsSection => IsBlock || Children.Count > 0;

    /// <summary>The first child with this name, or null.</summary>
    /// <param name="name">The key name, matched without regard to case.</param>
    /// <returns>The child, or null.</returns>
    public KeyValuesNode? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Children.FirstOrDefault(
            c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every child with this name, in file order.</summary>
    /// <param name="name">The key name, matched without regard to case.</param>
    /// <returns>The matching children.</returns>
    public IEnumerable<KeyValuesNode> FindAll(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Children.Where(
            c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <c>GetString</c>: the first child's value, or a fallback.
    /// </summary>
    /// <param name="name">The key name.</param>
    /// <param name="fallback">What to return when the key is absent.</param>
    /// <returns>The value, or <paramref name="fallback"/>.</returns>
    /// <remarks>
    /// The C++ returns the EMPTY STRING rather than null for an absent key,
    /// which makes a null check against it dead code -- the
    /// <c>pIncludeFileName == NULL</c> guard at
    /// <c>materialsystem/cmaterial.cpp:3443</c> in the 2018 drop never fires.
    /// That behaviour is a property of the call, so the fallback is explicit
    /// here instead.
    /// </remarks>
    public string? GetString(string name, string? fallback = null) =>
        Find(name)?.Value ?? fallback;

    /// <summary>
    /// <c>GetFloat</c>: the first child's value parsed as a float, or a
    /// fallback.
    /// </summary>
    /// <param name="name">The key name.</param>
    /// <param name="fallback">What to return when the key is absent.</param>
    /// <returns>The value, or <paramref name="fallback"/>.</returns>
    public float GetFloat(string name, float fallback = 0f)
    {
        string? text = GetString(name);
        return text is null ? fallback : (float)CFormat.Atof(text);
    }

    /// <summary>
    /// <c>GetInt</c>: the first child's value parsed as an integer, or a
    /// fallback.
    /// </summary>
    /// <param name="name">The key name.</param>
    /// <param name="fallback">What to return when the key is absent.</param>
    /// <returns>The value, or <paramref name="fallback"/>.</returns>
    public int GetInt(string name, int fallback = 0)
    {
        string? text = GetString(name);
        return text is null ? fallback : CFormat.Atoi(text);
    }

    /// <summary>Appends a scalar child.</summary>
    /// <param name="name">The key name.</param>
    /// <param name="value">The value.</param>
    /// <returns>The child that was added.</returns>
    public KeyValuesNode SetString(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        KeyValuesNode? existing = Find(name);
        if (existing is not null && !existing.IsSection)
        {
            existing.Value = value;
            return existing;
        }

        KeyValuesNode child = new(name) { Value = value };
        Children.Add(child);
        return child;
    }

    /// <summary>Appends a scalar child holding an integer.</summary>
    /// <param name="name">The key name.</param>
    /// <param name="value">The value.</param>
    /// <returns>The child that was added.</returns>
    public KeyValuesNode SetInt(string name, int value) =>
        SetString(name, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// <c>FindKey(name, true)</c>: the first child with this name, created as
    /// an empty section if it is not there.
    /// </summary>
    /// <param name="name">The key name.</param>
    /// <returns>The child.</returns>
    /// <remarks>
    /// The creating overload, used by the patch machinery at
    /// <c>materialsystem/cmaterial.cpp:3292</c>. It is the reason a recursive
    /// <c>replace</c> can turn a scalar into a section.
    /// </remarks>
    public KeyValuesNode FindOrCreate(string name)
    {
        KeyValuesNode? existing = Find(name);
        if (existing is not null)
        {
            return existing;
        }

        KeyValuesNode child = new(name) { IsBlock = true };
        Children.Add(child);
        return child;
    }

    /// <summary>A deep copy of this node and everything under it.</summary>
    /// <returns>The copy.</returns>
    public KeyValuesNode Clone()
    {
        KeyValuesNode copy = new(Name) { Value = Value, IsBlock = IsBlock };
        foreach (KeyValuesNode child in Children)
        {
            copy.Children.Add(child.Clone());
        }

        return copy;
    }
}
