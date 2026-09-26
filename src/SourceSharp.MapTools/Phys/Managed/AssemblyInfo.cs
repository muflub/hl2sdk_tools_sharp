//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

// The managed collision cooker's building blocks (qhull, IVP's builders) are internal: they are an
// implementation of ICollisionCooker, not API. The unit tier tests them directly, bit for bit
// against goldens cut from the native library.
[assembly: InternalsVisibleTo("SourceSharp.Tests")]
