//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// One line of a <c>.prt</c> file: a winding and the two clusters it separates.
/// </summary>
/// <param name="Cluster0">
/// The first cluster on the line. vbsp writes whichever of the portal's two
/// nodes agrees with the winding's own normal, so this is not simply
/// <c>nodes[0]</c>.
/// </param>
/// <param name="Cluster1">The second cluster on the line.</param>
/// <param name="Points">The winding, in the order written.</param>
/// <remarks>
/// A FILE portal, not a memory portal. vvis turns each of these into two; see
/// <see cref="PortalFile.ToMemoryPortals"/>.
/// </remarks>
public sealed record FilePortal(int Cluster0, int Cluster1, IReadOnlyList<Vec3> Points);
