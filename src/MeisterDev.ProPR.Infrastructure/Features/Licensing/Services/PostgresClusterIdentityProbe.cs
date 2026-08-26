// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Data.Common;
using System.Globalization;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Services;

/// <summary>
///     Reads the cluster's system identifier and the current database's name and object identifier from
///     PostgreSQL.
/// </summary>
/// <param name="dbContext">Supplies the connection the reads run on.</param>
/// <param name="logger">Receives why an optional component came back absent.</param>
public sealed partial class PostgresClusterIdentityProbe(
    MeisterProPRDbContext dbContext,
    ILogger<PostgresClusterIdentityProbe> logger) : IDatabaseClusterIdentityProbe
{
    private const string PostgresProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <inheritdoc />
    public async Task<DatabaseClusterIdentity> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!string.Equals(dbContext.Database.ProviderName, PostgresProviderName, StringComparison.Ordinal))
        {
            return DatabaseClusterIdentity.Unknown;
        }

        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;

        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var (databaseName, databaseOid) = await ReadDatabaseAsync(connection, cancellationToken)
                .ConfigureAwait(false);

            return new DatabaseClusterIdentity(
                await this.ReadSystemIdentifierAsync(connection, cancellationToken).ConfigureAwait(false),
                databaseName,
                databaseOid);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Reads the cluster's system identifier.
    ///     <para>
    ///         The component is optional, so every way the read can fail records it as absent rather than
    ///         failing the observation: the privilege to execute the function can be taken away, a managed
    ///         PostgreSQL service may withhold it, a build may not carry the function at all, and a statement
    ///         can time out. The database name and object identifier have already been read at this point, and
    ///         letting one optional component throw would discard them.
    ///     </para>
    ///     <para>
    ///         The profile hashes absence as such, so an installation whose answer here changes records one
    ///         change rather than looking like a different installation from then on. The log names the SQL
    ///         state, which is what separates a refused privilege from the other reasons.
    ///     </para>
    /// </summary>
    private async Task<string?> ReadSystemIdentifierAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        object? value;

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT system_identifier FROM pg_control_system()";

            value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception)
        {
            LogClusterIdentifierNotRead(logger, exception.SqlState, exception.MessageText);
            return null;
        }

        if (value is long systemIdentifier)
        {
            return systemIdentifier.ToString(CultureInfo.InvariantCulture);
        }

        // Separated from the refusal above so a diagnostic can tell "not permitted" from "not the shape this
        // build expects", which need different answers.
        LogClusterIdentifierUnexpectedShape(logger, value?.GetType().FullName ?? "null");

        return null;
    }

    private static async Task<(string? Name, long? Oid)> ReadDatabaseAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT datname, oid FROM pg_database WHERE datname = current_database()";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (null, null);
        }

        // The oid type comes back as an unsigned 32-bit value, which the profile carries as a plain number.
        return (reader.GetString(0), Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture));
    }
}
