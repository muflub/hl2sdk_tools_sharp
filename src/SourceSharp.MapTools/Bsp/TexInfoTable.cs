//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// LUMP_TEXINFO: the <c>texinfo</c> vector,
/// With <c>FindTexInfo</c> and
/// <c>FindOrCreateTexInfo</c> from the reference implementation.
/// </summary>
/// <remarks>
/// <para>
/// Append-only, and its indices are stored on brush sides and on faces, so the
/// order is output. Nothing here sorts, removes or renumbers.
/// </para>
/// <para>
/// The final BSP's TEXINFO lump is NOT this table: vbsp runs
/// <c>CompactTexinfoArray</c> at the end of the compile, which drops the
/// entries no face ended up referencing and remaps the rest ("Reduced 12
/// texinfos to 7" in its own log). That compaction needs faces, so it belongs
/// to a later lane; what this table is, is the thing compaction is a function
/// of.
/// </para>
/// </remarks>
public sealed class TexInfoTable
{
    /// <summary>
    /// The texinfo of a side the BSP has already consumed:
    /// <c>TEXINFO_NODE</c>.
    /// </summary>
    public const int TexInfoNode = -1;

    /// <summary>
    /// The format's ceiling: <c>MAX_MAP_TEXINFO</c>,
    /// </summary>
    /// <remarks>
    /// Not enforced here, because stock does not enforce it here either: the
    /// vector grows freely and the limit is only checked when the BSP is
    /// written. Enforcing it early would fail compiles stock completes.
    /// </remarks>
    public const int MaxMapTexInfo = 12288;

    private readonly List<TexInfo> _texInfo = [];

    // The first index of every distinct entry, by memcmp equality: what the
    // linear scan in Find would return, answered in O(1) (plan 3p). Entries
    // are only ever appended, so an index never goes stale.
    private readonly Dictionary<TexInfo, int> _first = new(new BitwiseComparer());

    /// <summary>How many entries the table holds.</summary>
    public int Count => _texInfo.Count;

    /// <summary>The entries, in insertion order.</summary>
    public IReadOnlyList<TexInfo> TexInfos => _texInfo;

    /// <summary>The entry at an index.</summary>
    /// <param name="index">The texinfo number.</param>
    /// <returns>The entry.</returns>
    public TexInfo this[int index] => _texInfo[index];

    /// <summary>
    /// Finds an identical entry, or -1: <c>FindTexInfo</c>,
    /// </summary>
    /// <param name="search">The entry to look for.</param>
    /// <returns>The index, or -1.</returns>
    /// <remarks>
    /// <para>
    /// Stock compares with <c>memcmp</c> over all 72 bytes, so the match is
    /// BITWISE and has no epsilon anywhere. <see cref="AreIdentical"/> is that
    /// comparison, and it differs from a float <c>==</c> in BOTH directions,
    /// each of which is reachable. <c>+0.0f</c> does not match <c>-0.0f</c>,
    /// where <c>==</c> says they are equal. And an identical NaN DOES match,
    /// where <c>==</c> says it does not — which is what lets the sides of a map
    /// with no <c>lightmapscale</c> keys, whose lightmap rows are infinities
    /// and NaNs, collapse onto one texinfo instead of getting a fresh one each
    /// as a naive <c>==</c> port would give them.
    /// </para>
    /// <para>
    /// Stock scans linearly from index 0 upward, so the FIRST identical entry
    /// wins. Equality is bitwise, so every identical entry is interchangeable
    /// and the first is simply the lowest index ever appended with those
    /// bytes: this answers from an index of first occurrences instead of the
    /// scan (plan 3p; the scan was 8% of a goldrush compile), and returns the
    /// same number the scan would. Unlike the plane table's tolerant match,
    /// no scan order is being reproduced.
    /// </para>
    /// </remarks>
    public int Find(TexInfo search) => _first.TryGetValue(search, out int index) ? index : -1;

    /// <summary>
    /// Finds an identical entry or appends one:
    /// <c>FindOrCreateTexInfo</c>.
    /// </summary>
    /// <param name="search">The entry.</param>
    /// <returns>The entry's index.</returns>
    public int FindOrCreate(TexInfo search)
    {
        int existing = Find(search);
        return existing >= 0 ? existing : Add(search);
    }

    /// <summary>
    /// Appends an entry without looking for a duplicate first.
    /// </summary>
    /// <param name="texInfo">The entry.</param>
    /// <returns>The entry's index.</returns>
    /// <remarks>
    /// <c>CreateBrushVersionOfWorldVertexTransitionMaterial</c> appends
    /// Directly rather than going
    /// through <c>FindOrCreateTexInfo</c>, having already decided that no
    /// duplicate can exist. That path needs this one.
    /// </remarks>
    public int Add(TexInfo texInfo)
    {
        _texInfo.Add(texInfo);
        _first.TryAdd(texInfo, _texInfo.Count - 1);
        return _texInfo.Count - 1;
    }

    /// <summary>
    /// Whether two entries are bit-for-bit identical, as <c>memcmp</c> is.
    /// </summary>
    /// <param name="a">The first entry.</param>
    /// <param name="b">The second entry.</param>
    /// <returns>True when every byte matches.</returns>
    /// <remarks>
    /// Compared as raw bit patterns rather than as floats, which is what makes
    /// <c>+0.0f</c> and <c>-0.0f</c> different and an identical NaN equal to
    /// itself — the opposite of a float comparison in each case.
    /// <c>texinfo_t</c> has no padding — sixteen floats and two ints, all
    /// four-byte — so a field-by-field bit comparison is exactly the 72-byte
    /// <c>memcmp</c>.
    /// </remarks>
    public static bool AreIdentical(TexInfo a, TexInfo b)
    {
        if (a.Flags != b.Flags || a.TexData != b.TexData)
        {
            return false;
        }

        for (int i = 0; i < 8; i++)
        {
            if (BitConverter.SingleToInt32Bits(a.TextureVecsTexelsPerWorldUnits[i]) !=
                BitConverter.SingleToInt32Bits(b.TextureVecsTexelsPerWorldUnits[i]))
            {
                return false;
            }

            if (BitConverter.SingleToInt32Bits(a.LightmapVecsLuxelsPerWorldUnits[i]) !=
                BitConverter.SingleToInt32Bits(b.LightmapVecsLuxelsPerWorldUnits[i]))
            {
                return false;
            }
        }

        return true;
    }

    // memcmp equality (AreIdentical) and a hash over the same bit patterns.
    private sealed class BitwiseComparer : IEqualityComparer<TexInfo>
    {
        public bool Equals(TexInfo x, TexInfo y) => AreIdentical(x, y);

        public int GetHashCode(TexInfo obj)
        {
            HashCode hash = default;
            hash.Add(obj.TexData);
            hash.Add(obj.Flags);
            for (int i = 0; i < 8; i++)
            {
                hash.Add(BitConverter.SingleToInt32Bits(obj.TextureVecsTexelsPerWorldUnits[i]));
                hash.Add(BitConverter.SingleToInt32Bits(obj.LightmapVecsLuxelsPerWorldUnits[i]));
            }

            return hash.ToHashCode();
        }
    }
}
