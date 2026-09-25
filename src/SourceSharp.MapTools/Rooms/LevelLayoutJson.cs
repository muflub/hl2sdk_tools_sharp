using System.Text;
using System.Text.Json;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// The JSON shape of a <see cref="LevelLayout"/> — the text <c>ssmap link</c>
/// reads as its <c>layout.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written over <see cref="JsonDocument"/> (the BCL's reader), not
/// reflection serialisation: the library carries no source-generated context
/// for the layout records, and a reflection contract would pin the records'
/// property names as a file format without saying so. This type IS the
/// format's definition — its field names are the contract, and a refused file
/// gets the exact shape in its message.
/// </para>
/// <para>
/// The shape mirrors the records one field at a time:
/// <code>
/// { "name": "hubline", "cellSize": 256, "kit": { "width": 96, "height": 96, "depth": 16 },
///   "rooms": [
///     { "room": "hub", "cellX": 0, "cellY": 0, "rotation": 0,
///       "joints": [ { "socket": "PositiveX", "neighborSocket": "NegativeX" } ],
///       "capped": [ "NegativeX", "NegativeY" ] } ] }
/// </code>
/// <c>cellSize</c> and <c>kit</c> are optional: a layout usually restates the
/// library's own grid, and <see cref="LevelLayout.CellSize"/> and
/// <see cref="LevelLayout.Kit"/> must match the library anyway —
/// <see cref="RoomLibrary.Add(RoomObject)"/> and
/// <see cref="LevelLinker.LinkAsync"/> refuse a mismatch loudly either way.
/// When a file omits them, <see cref="Parse"/> returns the layout with the
/// grid fields as NaN sentinels and <see cref="HasGrid"/> says so; the caller
/// fills them from the library it loaded. A sentinel that reaches
/// <see cref="LevelLayout.Validate"/> fails there — nobody links with a
/// missing grid.
/// </para>
/// </remarks>
public static class LevelLayoutJson
{
    private const string NameKey = "name";
    private const string CellSizeKey = "cellSize";
    private const string KitKey = "kit";
    private const string WidthKey = "width";
    private const string HeightKey = "height";
    private const string DepthKey = "depth";
    private const string RoomsKey = "rooms";
    private const string RoomKey = "room";
    private const string CellXKey = "cellX";
    private const string CellYKey = "cellY";
    private const string RotationKey = "rotation";
    private const string JointsKey = "joints";
    private const string SocketKey = "socket";
    private const string NeighborSocketKey = "neighborSocket";
    private const string CappedKey = "capped";

    /// <summary>
    /// Parses a layout's JSON text.
    /// </summary>
    /// <param name="json">The whole file.</param>
    /// <returns>
    /// The layout, unvalidated — <see cref="LevelLayout.Validate"/> is the
    /// link's business. If the file omitted the grid, the layout's
    /// <see cref="LevelLayout.CellSize"/> and <see cref="LevelLayout.Kit"/>
    /// are NaN sentinels (see <see cref="HasGrid"/>).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="LinkException">The JSON is malformed or names a field this shape does not have.</exception>
    public static LevelLayout Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new LinkException($"layout.json is not JSON: {exception.Message}");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new LinkException($"layout.json is {root.ValueKind}, expected an object.");
            }

            string? name = null;
            float? cellSize = null;
            SocketKit? kit = null;
            RoomInstance[]? rooms = null;

            foreach (JsonProperty property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case NameKey:
                        name = ReadString(property.Value, NameKey);
                        break;
                    case CellSizeKey:
                        cellSize = ReadSingle(property.Value, CellSizeKey);
                        break;
                    case KitKey:
                        kit = ReadKit(property.Value);
                        break;
                    case RoomsKey:
                        rooms = ReadRooms(property.Value);
                        break;
                    default:
                        throw new LinkException($"layout.json has an unknown field \"{property.Name}\".");
                }
            }

            if (name is null || rooms is null)
            {
                List<string> missing = [];
                if (name is null)
                {
                    missing.Add(NameKey);
                }

                if (rooms is null)
                {
                    missing.Add(RoomsKey);
                }

                throw new LinkException($"layout.json is missing {string.Join(", ", missing)}.");
            }

            return new LevelLayout(
                name,
                cellSize ?? float.NaN,
                kit ?? new SocketKit(float.NaN, float.NaN, float.NaN),
                rooms);
        }
    }

    /// <summary>Whether the file stated the grid, or the library's own is meant.</summary>
    /// <param name="layout">A layout from <see cref="Parse"/>.</param>
    /// <returns>True when both <c>cellSize</c> and <c>kit</c> were given.</returns>
    public static bool HasGrid(LevelLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return !float.IsNaN(layout.CellSize) && !float.IsNaN(layout.Kit.Width);
    }

    /// <summary>Writes a layout's JSON text, the shape <see cref="Parse"/> reads.</summary>
    /// <param name="layout">The layout.</param>
    /// <returns>The file text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is null.</exception>
    public static string Write(LevelLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString(NameKey, layout.Name);
            writer.WriteNumber(CellSizeKey, layout.CellSize);
            writer.WriteStartObject(KitKey);
            writer.WriteNumber(WidthKey, layout.Kit.Width);
            writer.WriteNumber(HeightKey, layout.Kit.Height);
            writer.WriteNumber(DepthKey, layout.Kit.Depth);
            writer.WriteEndObject();
            writer.WriteStartArray(RoomsKey);
            foreach (RoomInstance instance in layout.Rooms)
            {
                writer.WriteStartObject();
                writer.WriteString(RoomKey, instance.Placement.Room);
                writer.WriteNumber(CellXKey, instance.Placement.CellX);
                writer.WriteNumber(CellYKey, instance.Placement.CellY);
                writer.WriteNumber(RotationKey, instance.Placement.Rotation);
                writer.WriteStartArray(JointsKey);
                foreach ((string socket, string neighbour) in instance.Joints)
                {
                    writer.WriteStartObject();
                    writer.WriteString(SocketKey, socket);
                    writer.WriteString(NeighborSocketKey, neighbour);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteStartArray(CappedKey);
                foreach (string socket in instance.Capped)
                {
                    writer.WriteStringValue(socket);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static RoomInstance[] ReadRooms(JsonElement rooms)
    {
        if (rooms.ValueKind != JsonValueKind.Array)
        {
            throw new LinkException($"layout.json {RoomsKey} is not an array.");
        }

        List<RoomInstance> list = [];
        int index = 0;
        foreach (JsonElement room in rooms.EnumerateArray())
        {
            if (room.ValueKind != JsonValueKind.Object)
            {
                throw new LinkException($"layout.json room {index} is not an object.");
            }

            string? roomName = null;
            int cellX = 0, cellY = 0, rotation = 0;
            bool seenX = false, seenY = false;
            (string, string)[] joints = [];
            string[] capped = [];
            foreach (JsonProperty member in room.EnumerateObject())
            {
                switch (member.Name)
                {
                    case RoomKey:
                        roomName = ReadString(member.Value, $"{RoomsKey}[{index}].{RoomKey}");
                        break;
                    case CellXKey:
                        cellX = ReadInt32(member.Value, $"{RoomsKey}[{index}].{CellXKey}");
                        seenX = true;
                        break;
                    case CellYKey:
                        cellY = ReadInt32(member.Value, $"{RoomsKey}[{index}].{CellYKey}");
                        seenY = true;
                        break;
                    case RotationKey:
                        rotation = ReadInt32(member.Value, $"{RoomsKey}[{index}].{RotationKey}");
                        break;
                    case JointsKey:
                        joints = ReadJoints(member.Value, index);
                        break;
                    case CappedKey:
                        capped = ReadStringArray(member.Value, $"{RoomsKey}[{index}].{CappedKey}");
                        break;
                    default:
                        throw new LinkException(
                            $"layout.json room {index} has an unknown field \"{member.Name}\".");
                }
            }

            if (roomName is null || !seenX || !seenY)
            {
                throw new LinkException(
                    $"layout.json room {index} needs {RoomKey}, {CellXKey} and {CellYKey}.");
            }

            list.Add(new RoomInstance(new RoomPlacement(roomName, cellX, cellY, rotation), joints, capped));
            index++;
        }

        return [.. list];
    }

    private static (string, string)[] ReadJoints(JsonElement joints, int roomIndex)
    {
        if (joints.ValueKind != JsonValueKind.Array)
        {
            throw new LinkException($"layout.json room {roomIndex} {JointsKey} is not an array.");
        }

        List<(string, string)> list = [];
        int joint = 0;
        foreach (JsonElement edge in joints.EnumerateArray())
        {
            if (edge.ValueKind != JsonValueKind.Object)
            {
                throw new LinkException(
                    $"layout.json room {roomIndex} joint {joint} is not an object; it names both sockets.");
            }

            string? socket = null;
            string? neighbour = null;
            foreach (JsonProperty member in edge.EnumerateObject())
            {
                switch (member.Name)
                {
                    case SocketKey:
                        socket = ReadString(member.Value, $"{RoomsKey}[{roomIndex}].{JointsKey}[{joint}].{SocketKey}");
                        break;
                    case NeighborSocketKey:
                        neighbour = ReadString(
                            member.Value, $"{RoomsKey}[{roomIndex}].{JointsKey}[{joint}].{NeighborSocketKey}");
                        break;
                    default:
                        throw new LinkException(
                            $"layout.json room {roomIndex} joint {joint} has an unknown field \"{member.Name}\".");
                }
            }

            if (socket is null || neighbour is null)
            {
                throw new LinkException(
                    $"layout.json room {roomIndex} joint {joint} names both of its sockets.");
            }

            list.Add((socket, neighbour));
            joint++;
        }

        return [.. list];
    }

    private static string[] ReadStringArray(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new LinkException($"layout.json field \"{name}\" is not an array.");
        }

        List<string> values = [];
        foreach (JsonElement element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new LinkException($"layout.json field \"{name}\" has a non-string element {element}.");
            }

            values.Add(element.GetString()!);
        }

        return [.. values];
    }

    private static SocketKit ReadKit(JsonElement kit)
    {
        if (kit.ValueKind != JsonValueKind.Object)
        {
            throw new LinkException($"layout.json {KitKey} is not an object.");
        }

        float width = 0f, height = 0f, depth = 0f;
        bool seenWidth = false, seenHeight = false, seenDepth = false;
        foreach (JsonProperty member in kit.EnumerateObject())
        {
            switch (member.Name)
            {
                case WidthKey:
                    width = ReadSingle(member.Value, WidthKey);
                    seenWidth = true;
                    break;
                case HeightKey:
                    height = ReadSingle(member.Value, HeightKey);
                    seenHeight = true;
                    break;
                case DepthKey:
                    depth = ReadSingle(member.Value, DepthKey);
                    seenDepth = true;
                    break;
                default:
                    throw new LinkException($"layout.json kit has an unknown field \"{member.Name}\".");
            }
        }

        if (!seenWidth || !seenHeight || !seenDepth)
        {
            throw new LinkException($"layout.json {KitKey} needs width, height and depth.");
        }

        return new SocketKit(width, height, depth);
    }

    private static string ReadString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new LinkException($"layout.json field \"{name}\" is not a string.");
        }

        return value.GetString() ?? throw new LinkException($"layout.json field \"{name}\" is null.");
    }

    private static float ReadSingle(JsonElement value, string name)
    {
        if (!value.TryGetSingle(out float number))
        {
            throw new LinkException($"layout.json field \"{name}\" is not a number.");
        }

        return number;
    }

    private static int ReadInt32(JsonElement value, string name)
    {
        if (!value.TryGetInt32(out int number))
        {
            throw new LinkException($"layout.json field \"{name}\" is not an integer.");
        }

        return number;
    }
}

/// <summary>
/// The JSON shape of a <see cref="RoomDefinition"/> — the roomdef sidecar
/// <c>ssmap room</c> reads beside the room's VMF (and the manifest's
/// definition half, in the shape <see cref="RoomObjectStore"/> persists).
/// </summary>
/// <remarks>
/// A room's VMF carries no <see cref="RoomDefinition"/>: the definition is
/// what the room's <em>author</em> claims the room is — its name, its grid,
/// its kit, which faces are sockets — and every claim is checked by
/// <see cref="RoomCompiler"/> against the compile, not inferred from it.
/// The sidecar is how that claim travels with the file:
/// <code>
/// { "name": "hub", "cellSize": 256, "kit": { "width": 96, "height": 96, "depth": 16 },
///   "sockets": [ { "facing": "PositiveX", "name": "PositiveX" } ] }
/// </code>
/// Same hand-written reader as <see cref="LevelLayoutJson"/>, same refusal
/// style: an unknown or missing field names itself.
/// </remarks>
public static class RoomDefinitionJson
{
    private const string NameKey = "name";
    private const string CellSizeKey = "cellSize";
    private const string KitKey = "kit";
    private const string WidthKey = "width";
    private const string HeightKey = "height";
    private const string DepthKey = "depth";
    private const string SocketsKey = "sockets";
    private const string FacingKey = "facing";
    private const string SocketNameKey = "name";

    /// <summary>Parses a definition's JSON text.</summary>
    /// <param name="json">The whole file.</param>
    /// <returns>The definition, unvalidated — <see cref="RoomDefinition.Validate"/> is the compiler's business.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="LinkException">The JSON is malformed or names a field this shape does not have.</exception>
    public static RoomDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new LinkException($"roomdef is not JSON: {exception.Message}");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new LinkException($"roomdef is {root.ValueKind}, expected an object.");
            }

            string? name = null;
            float? cellSize = null;
            SocketKit? kit = null;
            RoomSocket[]? sockets = null;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case NameKey:
                        name = ReadStringValue(property.Value, NameKey);
                        break;
                    case CellSizeKey:
                        cellSize = ReadSingleValue(property.Value, CellSizeKey);
                        break;
                    case KitKey:
                        kit = ReadKitValue(property.Value);
                        break;
                    case SocketsKey:
                        sockets = ReadSocketsValue(property.Value);
                        break;
                    default:
                        throw new LinkException($"roomdef has an unknown field \"{property.Name}\".");
                }
            }

            if (name is null || cellSize is null || kit is null || sockets is null)
            {
                throw new LinkException(
                    "roomdef needs name, cellSize, kit { width, height, depth } and sockets [ { facing, name } ].");
            }

            return new RoomDefinition(name, cellSize.Value, kit.Value, sockets);
        }
    }

    /// <summary>Writes a definition's JSON text, the shape <see cref="Parse"/> reads.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The file text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    public static string Write(RoomDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString(NameKey, definition.Name);
            writer.WriteNumber(CellSizeKey, definition.CellSize);
            writer.WriteStartObject(KitKey);
            writer.WriteNumber(WidthKey, definition.Kit.Width);
            writer.WriteNumber(HeightKey, definition.Kit.Height);
            writer.WriteNumber(DepthKey, definition.Kit.Depth);
            writer.WriteEndObject();
            writer.WriteStartArray(SocketsKey);
            foreach (RoomSocket socket in definition.Sockets)
            {
                writer.WriteStartObject();
                writer.WriteNumber(FacingKey, (int)socket.Facing);
                writer.WriteString(SocketNameKey, socket.Name);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static RoomSocket[] ReadSocketsValue(JsonElement sockets)
    {
        if (sockets.ValueKind != JsonValueKind.Array)
        {
            throw new LinkException($"roomdef {SocketsKey} is not an array.");
        }

        List<RoomSocket> list = [];
        foreach (JsonElement socket in sockets.EnumerateArray())
        {
            if (socket.ValueKind != JsonValueKind.Object)
            {
                throw new LinkException("a roomdef socket is not an object.");
            }

            int facing = -1;
            string? socketName = null;
            foreach (JsonProperty member in socket.EnumerateObject())
            {
                switch (member.Name)
                {
                    case FacingKey:
                        if (!member.Value.TryGetInt32(out int raw) || !Enum.IsDefined((RoomFacing)raw))
                        {
                            throw new LinkException($"roomdef socket facing {member.Value} is not a facing.");
                        }

                        facing = raw;
                        break;
                    case SocketNameKey:
                        socketName = ReadStringValue(member.Value, SocketNameKey);
                        break;
                    default:
                        throw new LinkException($"a roomdef socket has an unknown field \"{member.Name}\".");
                }
            }

            if (facing < 0 || socketName is null)
            {
                throw new LinkException("a roomdef socket needs both facing and name.");
            }

            list.Add(new RoomSocket((RoomFacing)facing, socketName));
        }

        return [.. list];
    }

    private static SocketKit ReadKitValue(JsonElement kit)
    {
        if (kit.ValueKind != JsonValueKind.Object)
        {
            throw new LinkException($"roomdef {KitKey} is not an object.");
        }

        float width = 0f, height = 0f, depth = 0f;
        bool seenWidth = false, seenHeight = false, seenDepth = false;
        foreach (JsonProperty member in kit.EnumerateObject())
        {
            switch (member.Name)
            {
                case WidthKey:
                    width = ReadSingleValue(member.Value, WidthKey);
                    seenWidth = true;
                    break;
                case HeightKey:
                    height = ReadSingleValue(member.Value, HeightKey);
                    seenHeight = true;
                    break;
                case DepthKey:
                    depth = ReadSingleValue(member.Value, DepthKey);
                    seenDepth = true;
                    break;
                default:
                    throw new LinkException($"roomdef kit has an unknown field \"{member.Name}\".");
            }
        }

        if (!seenWidth || !seenHeight || !seenDepth)
        {
            throw new LinkException($"roomdef {KitKey} needs width, height and depth.");
        }

        return new SocketKit(width, height, depth);
    }

    private static string ReadStringValue(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new LinkException($"roomdef field \"{name}\" is not a string.");
        }

        return value.GetString() ?? throw new LinkException($"roomdef field \"{name}\" is null.");
    }

    private static float ReadSingleValue(JsonElement value, string name)
    {
        if (!value.TryGetSingle(out float number))
        {
            throw new LinkException($"roomdef field \"{name}\" is not a number.");
        }

        return number;
    }
}
