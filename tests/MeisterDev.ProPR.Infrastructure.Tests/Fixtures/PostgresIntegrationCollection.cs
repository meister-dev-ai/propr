// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.Fixtures;

/// <summary>
///     Binds this assembly's "PostgresIntegration" collection to the shared container fixture.
/// </summary>
/// <remarks>
///     Declared per assembly on purpose: xUnit resolves a collection definition within the assembly that runs
///     the test, so a shared fixture class cannot carry the definition for everyone. The cost of a second test
///     assembly using Postgres is therefore a second container, not a shared one.
/// </remarks>
[CollectionDefinition("PostgresIntegration")]
public sealed class PostgresIntegrationCollection : ICollectionFixture<PostgresContainerFixture>
{
    // Marker class, no members needed.
}
