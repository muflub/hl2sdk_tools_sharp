//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp.Structs;

using SourceSharp.MapTools.Bsp;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// The level's switchable light styles (the rooms design, section 8, and
/// finding 8): one style from 32 per distinct light name over the whole
/// level, where each room's compile numbered its own lights from 32.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> vbsp gives every named <c>light*</c> entity (but
/// <c>light_dynamic</c>) a switchable style, 32 plus the index of its name
/// among the map's light names in first-seen order, and writes it into the
/// entity's <c>style</c> key; vrad bakes that light into its own lightmap
/// pages, tagged with the style, and the game switches the style by the
/// light's name. Rooms compile alone, so two rooms with one named light each
/// both give theirs style 32: linked side by side, switching one light
/// switches both. The link renumbers exactly as vbsp numbers the flattened
/// level: over the linked entity lump in its order, one style per distinct
/// name (case-sensitive, as vbsp compares), a name several placements
/// share (a global name) sharing one style. The entity keys take the new
/// style here; the faces' styles and the world lights' take it from
/// <see cref="Remap"/>, per placement, when the level is lit.
/// </para>
/// <para>
/// A level whose rooms name no light, or whose one room with named lights
/// comes first, keeps every style it had, so such a level links to the
/// bytes it did before the renumbering.
/// </para>
/// </remarks>
internal sealed class LevelLightStyles
{
    /// <summary>The first switchable style.</summary>
    public const int FirstSwitchable = 32;

    private readonly List<string> _names = [];
    private readonly Dictionary<(int Placement, int Style), int> _map = [];

    /// <summary>How many switchable names the level has.</summary>
    public int Count => _names.Count;

    /// <summary>
    /// Renumbers the named lights among <paramref name="entities"/>, in their
    /// order, rewriting each one's <c>style</c> key and recording, per
    /// placement, which of its room's styles became which.
    /// </summary>
    /// <param name="entities">The level's entities in lump order after the worldspawn, each with its placement (-1 for the library's).</param>
    /// <exception cref="LinkException">
    /// More than <see cref="Bsp.Write.WriteLimits.MaxSwitchedLights"/> names,
    /// or one room style that would take two level styles in one placement.
    /// </exception>
    public void Renumber(IReadOnlyList<(BspEntity Entity, int Placement)> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        foreach ((BspEntity entity, int placement) in entities)
        {
            string className = entity.ClassName;
            if (className.Length < 5
                || !className.AsSpan(0, 5).Equals("light", StringComparison.OrdinalIgnoreCase)
                || string.Equals(className, "light_dynamic", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string name = entity.Get("targetname") ?? string.Empty;
            if (name.Length == 0)
            {
                continue;
            }

            int j = _names.IndexOf(name);
            if (j < 0)
            {
                if (_names.Count == Bsp.Write.WriteLimits.MaxSwitchedLights)
                {
                    throw new LinkException(
                        $"the level names more than {Bsp.Write.WriteLimits.MaxSwitchedLights} switched lights ({name} is one too many);"
                        + $" a map holds at most {Bsp.Write.WriteLimits.MaxSwitchedLights}.");
                }

                j = _names.Count;
                _names.Add(name);
            }

            int style = FirstSwitchable + j;
            int index = entity.Pairs.FindLastIndex(p => string.Equals(p.Key, "style", StringComparison.OrdinalIgnoreCase));
            if (index >= 0
                && int.TryParse(entity.Pairs[index].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int old)
                && old >= FirstSwitchable
                && placement >= 0)
            {
                if (_map.TryGetValue((placement, old), out int held) && held != style)
                {
                    throw new LinkException(
                        $"placement {placement}'s light style {old} names two lights, {_names[held - FirstSwitchable]} and {name}, in the linked level.");
                }

                _map[(placement, old)] = style;
            }

            string value = style.ToString(CultureInfo.InvariantCulture);
            if (index >= 0)
            {
                entity.Pairs[index] = new BspKeyValue(entity.Pairs[index].Key, value);
            }
            else
            {
                entity.Pairs.Add(new BspKeyValue("style", value));
            }
        }
    }

    /// <summary>
    /// The level's style for one of a placement's room styles: a switchable
    /// style its named light took (<see cref="Renumber"/>), else the style
    /// itself (0, the preset styles 1 to 31, and 255 for "none").
    /// </summary>
    /// <param name="placement">The placement, by index into the layout.</param>
    /// <param name="style">The room's style.</param>
    /// <returns>The level's style.</returns>
    public int Remap(int placement, int style) =>
        style >= FirstSwitchable && style != 255 && _map.TryGetValue((placement, style), out int level) ? level : style;
}
