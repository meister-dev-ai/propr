// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.Fixtures;

/// <summary>
///     Binds this assembly's four Postgres collections to the shared container fixture.
/// </summary>
/// <remarks>
///     <para>
///         xUnit runs the classes of one collection one after another. With all Postgres classes in a single
///         collection, that collection took longer than the rest of the assembly. Four collections run in
///         parallel, and each one gets its own <see cref="PostgresContainerFixture" /> instance and therefore its
///         own database. Several tests delete whole tables, so the collections must not share a database.
///         Within a collection the classes still run one after another.
///     </para>
///     <para>
///         A new Postgres test class can join any of the four collections. The classes are distributed so that
///         the collections take about the same time. Place a slow class in the collection with the least work.
///     </para>
///     <para>
///         The definitions are declared per assembly because xUnit resolves a collection definition within the
///         assembly that runs the test. A shared fixture class cannot carry the definition for everyone.
///     </para>
/// </remarks>
[CollectionDefinition("PostgresIntegration1")]
public sealed class PostgresIntegrationCollection1 : ICollectionFixture<PostgresContainerFixture>
{
    // Marker class, no members needed.
}

/// <inheritdoc cref="PostgresIntegrationCollection1" />
[CollectionDefinition("PostgresIntegration2")]
public sealed class PostgresIntegrationCollection2 : ICollectionFixture<PostgresContainerFixture>
{
    // Marker class, no members needed.
}

/// <inheritdoc cref="PostgresIntegrationCollection1" />
[CollectionDefinition("PostgresIntegration3")]
public sealed class PostgresIntegrationCollection3 : ICollectionFixture<PostgresContainerFixture>
{
    // Marker class, no members needed.
}

/// <inheritdoc cref="PostgresIntegrationCollection1" />
[CollectionDefinition("PostgresIntegration4")]
public sealed class PostgresIntegrationCollection4 : ICollectionFixture<PostgresContainerFixture>
{
    // Marker class, no members needed.
}
