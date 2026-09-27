//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

// The slab batcher's packing and completion rules are tested with a CPU
// stand-in for the device, which needs its internal seam.
[assembly: InternalsVisibleTo("SourceSharp.Tests")]
