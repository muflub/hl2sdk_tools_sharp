//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using Xunit;

namespace SourceSharp.Tests;

/// <summary>
/// The facts that time something. xUnit runs a collection with parallelism
/// disabled on its own, after the parallel ones, so a throughput figure is
/// taken on an otherwise idle process rather than beside every other test
/// class.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThroughputCollection
{
    /// <summary>The collection's name, for <see cref="CollectionAttribute"/>.</summary>
    public const string Name = "throughput";
}
