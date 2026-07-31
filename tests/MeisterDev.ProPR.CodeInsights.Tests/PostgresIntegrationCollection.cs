// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.CodeInsights.Tests;

/// <summary>
///     Binds this assembly's "PostgresIntegration" collection to the shared container fixture.
/// </summary>
/// <remarks>
///     Declared here as well as in Infrastructure.Tests because xUnit resolves a collection definition within
///     the assembly running the test. The fixture is shared as code, not as a running database: this assembly
///     starts its own container.
/// </remarks>
[CollectionDefinition("PostgresIntegration")]
public sealed class PostgresIntegrationCollection : ICollectionFixture<PostgresContainerFixture>
{
    // Marker class, no members needed.
}
