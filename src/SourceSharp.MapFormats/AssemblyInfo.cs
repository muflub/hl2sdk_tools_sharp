//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

// The deterministic math library's two tiers (Numerics/FastKernels and
// Numerics/ExactMath) are internal: callers use DetMath and DetMathF. The
// suite needs them directly, to measure the fast tier's error bound over
// whole binades and to check each public function's fast answer against the
// exact one. Tests only; the CLI gets no internals.
[assembly: InternalsVisibleTo("SourceSharp.Tests")]
