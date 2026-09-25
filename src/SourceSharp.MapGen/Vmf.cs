namespace SourceSharp.MapGen;

/// <summary>A point in map space. Source's units are inches and its Z is up.</summary>
public readonly record struct Point(float X, float Y, float Z)
{
    public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>How a map file spells a position.</summary>
    public override string ToString() => $"{Fmt(X)} {Fmt(Y)} {Fmt(Z)}";

    /// <summary>
    /// Invariant, and without an exponent.
    ///
    /// Both matter: a comma decimal separator produces a file the compilers
    /// silently misread, and "1E-05" is not a number vbsp's parser accepts.
    /// </summary>
    internal static string Fmt(float f)
        => f.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>One face of a brush.</summary>
public sealed class VmfSide
{
    public required Point P0 { get; init; }
    public required Point P1 { get; init; }
    public required Point P2 { get; init; }
    public required string Material { get; set; }
    public required string UAxis { get; init; }
    public required string VAxis { get; init; }
}

/// <summary>A convex brush: the intersection of its faces' half-spaces.</summary>
public sealed class VmfSolid
{
    public List<VmfSide> Sides { get; } = [];
}

/// <summary>A nested chunk of keys and further chunks, in file order.</summary>
public sealed class VmfChunkNode
{
    public required string Name { get; init; }

    public List<KeyValuePair<string, string>> KeyValues { get; } = [];

    public List<VmfChunkNode> Children { get; } = [];
}

/// <summary>One output wired to one input.</summary>
public readonly record struct VmfConnection(
    string Output, string Target, string Input, string Parameter, float Delay, int TimesToFire);

/// <summary>An entity, point or brush.</summary>
public sealed class VmfEntity
{
    public required string ClassName { get; init; }

    /// <summary>Ordered, because a map file is read top to bottom and diffs are read by people.</summary>
    public List<KeyValuePair<string, string>> KeyValues { get; } = [];

    public List<VmfConnection> Connections { get; } = [];

    /// <summary>A brush entity's geometry. Empty for a point entity.</summary>
    public List<VmfSolid> Solids { get; } = [];

    /// <summary>
    /// Other child chunks, written after the solids — Hammer's
    /// <c>overlaytransition</c> block, whose <c>overlaydata</c> children vbsp
    /// turns into water overlays (a handler vbsp registers for every entity).
    /// </summary>
    public List<VmfChunkNode> Chunks { get; } = [];

    public string Name
        => KeyValues.FirstOrDefault(kv => kv.Key == "targetname").Value ?? string.Empty;

    public VmfEntity Set(string key, string value)
    {
        // Last write wins rather than both being kept: two "origin" keys is a
        // map that loads and is subtly in the wrong place.
        int at = KeyValues.FindIndex(kv => kv.Key == key);

        if (at >= 0)
            KeyValues[at] = new(key, value);
        else
            KeyValues.Add(new(key, value));

        return this;
    }

    public VmfEntity Wire(string output, string target, string input,
                          string parameter = "", float delay = 0f, int times = -1)
    {
        Connections.Add(new VmfConnection(output, target, input, parameter, delay, times));
        return this;
    }
}

/// <summary>A whole map, ready to be written out.</summary>
public sealed class VmfMap
{
    public List<KeyValuePair<string, string>> World { get; } = [];
    public List<VmfSolid> WorldSolids { get; } = [];
    public List<VmfEntity> Entities { get; } = [];

    /// <summary>
    /// Builds an axis-aligned box.
    ///
    /// The winding is the part worth being careful about. vbsp takes a face's
    /// normal as (p0 - p1) x (p2 - p1) and treats it as pointing OUT of the
    /// solid, so a face wound the wrong way turns the brush inside out -- which
    /// compiles, and produces a room you fall through.
    /// </summary>
    /// <summary>
    /// A pane: a box drawn on the named face AND the one opposite it, with the
    /// four edges nodraw.
    ///
    /// Both large faces, because a sheet of glass is visible from both sides and
    /// a single-sided one is invisible from behind — which reads as the glass
    /// having disappeared rather than as a one-sided brush.
    ///
    /// The edges are nodraw because they are four units of nothing seen
    /// end-on, and because a brush textured all over is what
    /// func_breakable_surf calls "multiple faces that aren't NODRAW".
    /// </summary>
    public static VmfSolid Pane(Point mins, Point maxs, string material, string face)
    {
        var solid = Box(mins, maxs, NoDraw);

        // ONE face, and that is the whole point of this helper.
        //
        // It used to texture the opposite face as well, which contradicted both
        // its own name and the only thing that uses it. vbsp derives a breakable
        // surface's pane grid from a SINGLE drawn quad and rejects a brush with
        // more than one — it reports that by writing the "error" key, which the
        // sandbox then had to override to get a usable window at all. Two drawn
        // faces also meant nobody could tell which side the glass was actually
        // on, because it appeared to be on both.
        //
        // FACE NAMES THE BOX SIDE, by the coordinate it sits at — the same
        // reading IndexOf has always had. Which side that is VISIBLE from is not
        // worth guessing from the winding: it was established by texturing one
        // face and photographing the result from both sides, and the answer for
        // this brush is written at the call site rather than encoded here, where
        // it would be a rule derived from one measurement.
        solid.Sides[IndexOf(face)].Material = material;

        return solid;
    }

    /// <summary>Which side of <see cref="Box"/> an axis sign names.</summary>
    private static int IndexOf(string face) => face switch
    {
        "+z" => 0, "-z" => 1,
        "+x" => 2, "-x" => 3,
        "+y" => 4, "-y" => 5,
        _ => throw new ArgumentException($"unknown face '{face}'", nameof(face)),
    };

    /// <summary>The tool texture that makes a face invisible and uncompiled.</summary>
    public const string NoDraw = "TOOLS/TOOLSNODRAW";

    public static VmfSolid Box(Point mins, Point maxs, string material, string? topMaterial = null)
    {
        var solid = new VmfSolid();

        // Texture axes must not be parallel to the face normal, so each pair of
        // opposite faces gets the pair that lies in its plane.
        const string alongX = "[1 0 0 0] 0.25";
        const string alongY = "[0 -1 0 0] 0.25";
        const string downZ  = "[0 0 -1 0] 0.25";
        const string plusY  = "[0 1 0 0] 0.25";

        void Side(Point p0, Point p1, Point p2, string u, string v, string mat)
            => solid.Sides.Add(new VmfSide
            {
                P0 = p0, P1 = p1, P2 = p2, Material = mat, UAxis = u, VAxis = v,
            });

        float mnx = mins.X, mny = mins.Y, mnz = mins.Z;
        float mxx = maxs.X, mxy = maxs.Y, mxz = maxs.Z;

        // +Z, then -Z.
        Side(new(mnx, mxy, mxz), new(mxx, mxy, mxz), new(mxx, mny, mxz),
             alongX, alongY, topMaterial ?? material);
        Side(new(mnx, mny, mnz), new(mxx, mny, mnz), new(mxx, mxy, mnz),
             alongX, alongY, material);

        // +X, then -X.
        Side(new(mxx, mxy, mxz), new(mxx, mxy, mnz), new(mxx, mny, mnz),
             plusY, downZ, material);
        Side(new(mnx, mny, mxz), new(mnx, mny, mnz), new(mnx, mxy, mnz),
             plusY, downZ, material);

        // +Y, then -Y.
        Side(new(mxx, mxy, mxz), new(mnx, mxy, mxz), new(mnx, mxy, mnz),
             alongX, downZ, material);
        Side(new(mxx, mny, mnz), new(mnx, mny, mnz), new(mnx, mny, mxz),
             alongX, downZ, material);

        return solid;
    }

    /// <summary>
    /// Writes the map file.
    ///
    /// Every id is unique across the whole document and handed out here rather
    /// than carried on the objects: Hammer requires uniqueness and nothing else,
    /// so generating them at the end keeps the model free of bookkeeping.
    /// </summary>
    public string Write()
    {
        var text = new System.Text.StringBuilder();
        int id = 0;

        text.Append("versioninfo\n{\n")
            .Append("\t\"editorversion\" \"400\"\n")
            .Append("\t\"editorbuild\" \"8075\"\n")
            .Append("\t\"mapversion\" \"1\"\n")
            .Append("\t\"formatversion\" \"100\"\n")
            .Append("\t\"prefab\" \"0\"\n}\n");

        text.Append("visgroups\n{\n}\n");
        text.Append("viewsettings\n{\n\t\"bSnapToGrid\" \"1\"\n\t\"nGridSpacing\" \"16\"\n}\n");

        text.Append("world\n{\n");
        text.Append($"\t\"id\" \"{++id}\"\n");
        text.Append("\t\"mapversion\" \"1\"\n");
        text.Append("\t\"classname\" \"worldspawn\"\n");

        foreach (var kv in World)
            text.Append($"\t\"{kv.Key}\" \"{kv.Value}\"\n");

        foreach (var solid in WorldSolids)
            WriteSolid(text, solid, ref id, 1);

        text.Append("}\n");

        foreach (var entity in Entities)
        {
            text.Append("entity\n{\n");
            text.Append($"\t\"id\" \"{++id}\"\n");
            text.Append($"\t\"classname\" \"{entity.ClassName}\"\n");

            foreach (var kv in entity.KeyValues)
                text.Append($"\t\"{kv.Key}\" \"{kv.Value}\"\n");

            foreach (var solid in entity.Solids)
                WriteSolid(text, solid, ref id, 1);

            foreach (var chunk in entity.Chunks)
                WriteChunk(text, chunk, 1);

            if (entity.Connections.Count > 0)
            {
                text.Append("\tconnections\n\t{\n");

                foreach (var c in entity.Connections)
                {
                    // Hammer joins an output's fields with the escape character,
                    // not a comma -- a comma in a parameter would otherwise end
                    // the field early.
                    text.Append($"\t\t\"{c.Output}\" \"{c.Target}\x1b{c.Input}\x1b{c.Parameter}" +
                                $"\x1b{Point.Fmt(c.Delay)}\x1b{c.TimesToFire}\"\n");
                }

                text.Append("\t}\n");
            }

            text.Append("\teditor\n\t{\n\t\t\"visgroupshown\" \"1\"\n\t\t\"visgroupautoshown\" \"1\"\n\t}\n");
            text.Append("}\n");
        }

        text.Append("cameras\n{\n\t\"activecamera\" \"-1\"\n}\n");
        text.Append("cordon\n{\n\t\"mins\" \"(-1024 -1024 -1024)\"\n\t\"maxs\" \"(1024 1024 1024)\"\n\t\"active\" \"0\"\n}\n");

        return text.ToString();
    }

    private static void WriteChunk(System.Text.StringBuilder text, VmfChunkNode chunk, int depth)
    {
        string tab = new('\t', depth);

        text.Append($"{tab}{chunk.Name}\n{tab}{{\n");

        foreach (var kv in chunk.KeyValues)
            text.Append($"{tab}\t\"{kv.Key}\" \"{kv.Value}\"\n");

        foreach (var child in chunk.Children)
            WriteChunk(text, child, depth + 1);

        text.Append($"{tab}}}\n");
    }

    private static void WriteSolid(System.Text.StringBuilder text, VmfSolid solid,
                                   ref int id, int depth)
    {
        string tab = new('\t', depth);

        text.Append($"{tab}solid\n{tab}{{\n");
        text.Append($"{tab}\t\"id\" \"{++id}\"\n");

        foreach (var side in solid.Sides)
        {
            text.Append($"{tab}\tside\n{tab}\t{{\n");
            text.Append($"{tab}\t\t\"id\" \"{++id}\"\n");
            text.Append($"{tab}\t\t\"plane\" \"({side.P0}) ({side.P1}) ({side.P2})\"\n");
            text.Append($"{tab}\t\t\"material\" \"{side.Material}\"\n");
            text.Append($"{tab}\t\t\"uaxis\" \"{side.UAxis}\"\n");
            text.Append($"{tab}\t\t\"vaxis\" \"{side.VAxis}\"\n");
            text.Append($"{tab}\t\t\"rotation\" \"0\"\n");
            text.Append($"{tab}\t\t\"lightmapscale\" \"16\"\n");
            text.Append($"{tab}\t\t\"smoothing_groups\" \"0\"\n");
            text.Append($"{tab}\t}}\n");
        }

        text.Append($"{tab}\teditor\n{tab}\t{{\n{tab}\t\t\"color\" \"0 180 220\"\n" +
                    $"{tab}\t\t\"visgroupshown\" \"1\"\n{tab}\t\t\"visgroupautoshown\" \"1\"\n{tab}\t}}\n");
        text.Append($"{tab}}}\n");
    }
}
