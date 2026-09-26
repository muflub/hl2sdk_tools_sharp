//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using Xunit;

// Test classes run in parallel, as the compiles they exercise do in the
// service that hosts these libraries: the libraries hold no mutable static
// state, so two tests in one process must not interfere. Tests inside one
// class still run one at a time. Tests that genuinely share something
// process-wide (a loaded native library, a recorded fixture they rewrite)
// go in one named [Collection] so they run serially with each other.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerClass, DisableTestParallelization = false)]
