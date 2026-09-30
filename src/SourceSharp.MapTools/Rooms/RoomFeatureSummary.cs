//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// What a room's level map section (<c>MAPV</c>) holds, as <c>ssmap rooms</c>
/// lists it.
/// </summary>
/// <param name="Polygons">Its floor polygons, one per connected piece of floor in a height band.</param>
/// <param name="Rings">Its floor rings: every polygon's outer ring and its holes, as the level map file counts them.</param>
/// <param name="Markers">Its authors' markers (<c>info_poi</c> with a <c>map_marker</c>).</param>
/// <param name="Label">Its <c>info_room</c>'s <c>map_label</c>, or empty.</param>
public sealed record RoomMapSummary(int Polygons, int Rings, int Markers, string Label);

/// <summary>
/// The features a room's pack sections say it carries, for <c>ssmap rooms</c>:
/// read from the sections alone, without the room's compile, so a listing
/// stays a few reads of a few bytes a room.
/// </summary>
/// <param name="Displacements">Its displacements (the <c>DISP</c> section's count), 0 when it has none.</param>
/// <param name="WaterVolumes">Its water volumes (the <c>WATR</c> section's water data count), or null when it has no water.</param>
/// <param name="Map">Its level map (<c>MAPV</c>), or null when the pack holds none for it.</param>
public sealed record RoomFeatureSummary(int Displacements, int? WaterVolumes, RoomMapSummary? Map);
