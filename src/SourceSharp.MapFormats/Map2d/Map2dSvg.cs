//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

namespace SourceSharp.MapFormats.Map2d;

/// <summary>
/// An SVG preview of a level's map: the same data as the <c>.map2d</c>,
/// drawn for people and web pages (the rooms design, 18.3 and D31).
/// </summary>
/// <remarks>
/// <para>
/// <b>What it draws.</b> Each placement's floors as filled paths (an outer
/// ring and its holes, even-odd), lower bands first and darker, so a
/// gallery reads over the floor below it; the doors as segments, open ones
/// green and closed ones red; each marker as a dot with its kind (and its
/// label, when it has one) beside it; and each placement's label at its
/// cell's centre. The drawing is in world units with +y up: a group flips
/// the y axis, and text is placed outside it so it is not mirrored.
/// </para>
/// <para>
/// <b>Why it is text built by hand.</b> The preview must be the same bytes
/// for the same map on every machine (the facts hold it), so numbers are
/// written in the invariant culture with a fixed format, and nothing
/// depends on a library's choices. It is a preview, not a format: the game
/// reads the <c>.map2d</c>.
/// </para>
/// </remarks>
public static class Map2dSvg
{
    /// <summary>The margin around the extent, in units.</summary>
    public const int Margin = 32;

    /// <summary>The preview of a level's map.</summary>
    /// <param name="level">The map.</param>
    /// <returns>The SVG document, LF line ends.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="level"/> is null.</exception>
    public static string Write(Map2dLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);
        Map2dExtent e = level.Extent;
        long minX = (long)e.MinX - Margin, minY = (long)e.MinY - Margin;
        long width = (long)e.MaxX - e.MinX + (2 * Margin), height = (long)e.MaxY - e.MinY + (2 * Margin);
        long flip = (long)e.MinY + e.MaxY;
        int lowest = level.Rings.Length == 0 ? 0 : level.Rings.Min(r => r.ZLow);
        int highest = level.Rings.Length == 0 ? 0 : level.Rings.Max(r => r.ZLow);

        StringBuilder svg = new();
        svg.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{minX} {minY} {width} {height}\" width=\"{width}\" height=\"{height}\">\n");
        svg.Append("<style>.floor{stroke:#222;stroke-width:2;fill-rule:evenodd}.open{stroke:#2a2;stroke-width:4}.closed{stroke:#c22;stroke-width:4}"
            + ".marker{fill:#14c}text{font:12px sans-serif;fill:#111}</style>\n");
        svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{minX}\" y=\"{minY}\" width=\"{width}\" height=\"{height}\" fill=\"#fff\"/>\n");
        svg.Append(CultureInfo.InvariantCulture, $"<g transform=\"matrix(1 0 0 -1 0 {flip})\">\n");

        // Floors: each outer ring with the holes after it, lowest band first.
        List<(int ZLow, int Order, StringBuilder Path)> floors = [];
        StringBuilder? path = null;
        foreach (Map2dRing ring in level.Rings)
        {
            if (!ring.IsHole || path is null)
            {
                path = new StringBuilder();
                floors.Add((ring.ZLow, floors.Count, path));
            }

            path.Append(path.Length == 0 ? "M" : " M");
            for (int p = 0; p < ring.Points.Length; p++)
            {
                path.Append(CultureInfo.InvariantCulture, $"{(p == 0 ? string.Empty : " L")}{ring.Points[p].X} {ring.Points[p].Y}");
            }

            path.Append(" Z");
        }

        foreach ((int zLow, _, StringBuilder d) in floors.OrderBy(f => f.ZLow).ThenBy(f => f.Order))
        {
            int shade = highest == lowest ? 200 : 150 + (int)(80L * (zLow - lowest) / (highest - lowest));
            svg.Append(CultureInfo.InvariantCulture, $"<path class=\"floor\" fill=\"rgb({shade},{shade},{Math.Min(255, shade + 20)})\" d=\"{d}\"/>\n");
        }

        foreach (Map2dDoor door in level.Doors)
        {
            svg.Append(CultureInfo.InvariantCulture,
                $"<line class=\"{(door.Open ? "open" : "closed")}\" x1=\"{Num(door.X0)}\" y1=\"{Num(door.Y0)}\" x2=\"{Num(door.X1)}\" y2=\"{Num(door.Y1)}\"/>\n");
        }

        foreach (Map2dMarker marker in level.Markers)
        {
            svg.Append(CultureInfo.InvariantCulture, $"<circle class=\"marker\" cx=\"{Num(marker.X)}\" cy=\"{Num(marker.Y)}\" r=\"8\"/>\n");
        }

        svg.Append("</g>\n");

        // Text outside the flipped group, its y turned by hand.
        foreach (Map2dMarker marker in level.Markers)
        {
            string text = marker.Label.Length == 0 ? marker.Kind : $"{marker.Kind}: {marker.Label}";
            svg.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{Num(marker.X + 10)}\" y=\"{Num(flip - marker.Y + 4)}\">{Escape(text)}</text>\n");
        }

        if (level.CellSize > 0)
        {
            foreach (Map2dRoom room in level.Rooms.Where(r => r.Label.Length > 0))
            {
                double cx = (room.CellX + 0.5) * level.CellSize;
                double cy = (room.CellY + 0.5) * level.CellSize;
                svg.Append(CultureInfo.InvariantCulture,
                    $"<text x=\"{Num(cx)}\" y=\"{Num(flip - cy)}\" text-anchor=\"middle\">{Escape(room.Label)}</text>\n");
            }
        }

        svg.Append("</svg>\n");
        return svg.ToString();
    }

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}
