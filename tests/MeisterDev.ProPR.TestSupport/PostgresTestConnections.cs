// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Npgsql;

namespace MeisterDev.ProPR.TestSupport;

/// <summary>
///     The pool settings that the Postgres fixtures put into every connection string they hand to a test.
/// </summary>
public static class PostgresTestConnections
{
    /// <summary>The number of seconds a pooled connection stays open without use before its pool closes it.</summary>
    public const int IdleLifetimeSeconds = 10;

    /// <summary>
    ///     Returns <paramref name="connectionString" /> with a pool that closes a connection after
    ///     <see cref="IdleLifetimeSeconds" /> seconds without use.
    /// </summary>
    /// <param name="connectionString">The connection string of a test database.</param>
    /// <remarks>
    ///     Npgsql keeps an idle pooled connection open for up to five minutes by default. Entity Framework builds a
    ///     data source with a pool of its own for every connection string, because the vector type mapping is
    ///     configured on the data source, and keeps it for the life of the test process. A test that opened
    ///     several connections at once therefore left them open for minutes after it had finished, and the idle
    ///     connections of all pools added up. With the short lifetime a pool closes the connections that the
    ///     running tests no longer use.
    /// </remarks>
    public static string WithShortIdleLifetime(string connectionString)
    {
        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            ConnectionIdleLifetime = IdleLifetimeSeconds,
            ConnectionPruningInterval = IdleLifetimeSeconds / 2,
        }.ConnectionString;
    }
}
