// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers;

/// <summary>
///     The upgrade that converts a logical model's protocol mode from the numeric position of an enum member to
///     the member's name, and the widening of the three columns that hold an identity key or a value qualified by
///     one.
/// </summary>
/// <remarks>
///     Run against rows written in the shapes an installation actually holds, not against a fresh schema: the
///     numeric columns are the one place in this change where a wrong conversion rewrites stored configuration
///     instead of failing, and a fresh database has no row to get wrong.
/// </remarks>
[Collection("PostgresIntegration")]
public sealed class StoreLogicalModelProtocolModeByNameMigrationTests(PostgresContainerFixture fixture)
{
    private const string BeforeWidening = "CoverageGateRecall";
    private const string ConversionMigration = "ProviderAddInArchitecture";

    // The seven wire shapes the integer column could hold, and an eighth value no build ever declared. The
    // eighth is what the conversion has to answer for: it is storable today, because the column is a bare
    // integer with no check constraint.
    // A family none of the shipped add-ins claims. The one migration moves every shipped family and qualifies
    // the logical-model modes on their connections, so a connection outside that set is what keeps this suite
    // measuring the naming conversion and nothing else.
    private const string UnclaimedIdentity = "ContosoGateway";

    // What each stored integer reads as once the migration has run. The conversion gives it a name and the
    // qualification then puts the connection's own family in front of it, so the two host-reserved names are
    // the only ones that come out bare: they belong to no family and nothing qualifies them.
    private static readonly (int Stored, string Expected)[] ProtocolModeRows =
    [
        (0, ProviderDeclaredProtocolModes.Auto),
        (1, UnclaimedIdentity + ":Responses"),
        (2, UnclaimedIdentity + ":ChatCompletions"),
        (3, ProviderDeclaredProtocolModes.Embeddings),
        (4, UnclaimedIdentity + ":AnthropicMessages"),
        (5, UnclaimedIdentity + ":BedrockConverse"),
        (6, UnclaimedIdentity + ":GoogleGenerateContent"),
        (97, UnclaimedIdentity + ":unknown-97"),
    ];

    [Fact]
    public async Task Upgrading_NamesEveryStoredProtocolModeAndWidensTheVocabularyColumns()
    {
        fixture.SkipIfUnavailable();

        await this.OnAScratchDatabaseAsync(async scratch =>
        {
            var connectionId = Guid.NewGuid();
            var configuredModelId = Guid.NewGuid();
            var maximumLengthIdentity = BuildToken(64);
            var maximumLengthQualifiedValue = $"{BuildToken(64)}:{new string('m', 64)}";

            await using (var beforeUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(beforeUpgrade, BeforeWidening);

                var clientId = await HistoricalSchemaSeed.SeedClientAsync(beforeUpgrade, "Protocol Mode Conversion");
                await SeedConnectionAsync(beforeUpgrade, connectionId, clientId, configuredModelId);

                foreach (var (stored, _) in ProtocolModeRows)
                {
                    await SeedLogicalModelAsync(beforeUpgrade, connectionId, configuredModelId, stored);
                }

                // The pre-migration shapes are what the assertions below are worth anything against, so they are
                // established here rather than assumed.
                Assert.Equal("integer", await ColumnTypeAsync(beforeUpgrade, "ai_logical_models", "protocol_mode"));
                Assert.Equal(
                    "integer",
                    await ColumnTypeAsync(beforeUpgrade, "ai_logical_model_overrides", "protocol_mode"));
                Assert.Equal(50, await ColumnWidthAsync(beforeUpgrade, "ai_connection_profiles", "provider_kind"));
                Assert.Equal(50, await ColumnWidthAsync(beforeUpgrade, "ai_connection_profiles", "auth_mode"));
                Assert.Equal(50, await ColumnWidthAsync(beforeUpgrade, "ai_purpose_bindings", "protocol_mode"));
            }

            await using (var afterUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(afterUpgrade, ConversionMigration);

                Assert.Equal(64, await ColumnWidthAsync(afterUpgrade, "ai_connection_profiles", "provider_kind"));
                Assert.Equal(129, await ColumnWidthAsync(afterUpgrade, "ai_connection_profiles", "auth_mode"));
                Assert.Equal(129, await ColumnWidthAsync(afterUpgrade, "ai_purpose_bindings", "protocol_mode"));
                Assert.Equal(129, await ColumnWidthAsync(afterUpgrade, "ai_logical_models", "protocol_mode"));
                Assert.True(await HasNumericRefusalAsync(afterUpgrade, "ai_logical_models"));
                Assert.True(await HasNumericRefusalAsync(afterUpgrade, "ai_logical_model_overrides"));
                Assert.Equal(
                    129,
                    await ColumnWidthAsync(afterUpgrade, "ai_logical_model_overrides", "protocol_mode"));

                // The credential column was widened to unbounded text when the credential work shipped, and
                // nothing in this change may put a bound back on it.
                Assert.Equal("text", await ColumnTypeAsync(afterUpgrade, "ai_connection_profiles", "protected_secret"));

                // Every seeded row carries the mode it carried before, by name.
                foreach (var (stored, expected) in ProtocolModeRows)
                {
                    Assert.Equal(expected, await ReadStoredModeAsync(afterUpgrade, "ai_logical_models", stored));
                    Assert.Equal(
                        expected,
                        await ReadStoredModeAsync(afterUpgrade, "ai_logical_model_overrides", stored));
                }

                // The widened columns hold what they were widened for.
                await afterUpgrade.Database.ExecuteSqlRawAsync(
                    "UPDATE ai_connection_profiles SET provider_kind = {0}, auth_mode = {1} WHERE id = {2};",
                    maximumLengthIdentity,
                    maximumLengthQualifiedValue,
                    connectionId);
                await afterUpgrade.Database.ExecuteSqlRawAsync(
                    "UPDATE ai_purpose_bindings SET protocol_mode = {0} WHERE connection_profile_id = {1};",
                    maximumLengthQualifiedValue,
                    connectionId);

                Assert.Equal(
                    maximumLengthIdentity,
                    await ScalarAsync(
                        afterUpgrade,
                        "SELECT provider_kind FROM ai_connection_profiles WHERE id = @id",
                        new NpgsqlParameter("id", connectionId)));
                Assert.Equal(
                    maximumLengthQualifiedValue,
                    await ScalarAsync(
                        afterUpgrade,
                        "SELECT auth_mode FROM ai_connection_profiles WHERE id = @id",
                        new NpgsqlParameter("id", connectionId)));
                Assert.Equal(
                    maximumLengthQualifiedValue,
                    await ScalarAsync(
                        afterUpgrade,
                        "SELECT protocol_mode FROM ai_purpose_bindings WHERE connection_profile_id = @id",
                        new NpgsqlParameter("id", connectionId)));
            }
        });
    }

    // A build from before the conversion writes an integer parameter into these columns, and PostgreSQL would
    // coerce it to text on assignment: that build would keep running against a converted schema and store '0'
    // where the row means Auto. The check constraint refuses it at the statement instead, and that makes
    // rolling the application back mean running the migration down rather than only redeploying.
    [Fact]
    public async Task AfterUpgrading_AWriteOfTheNumericValueIsRefusedByTheDatabase()
    {
        fixture.SkipIfUnavailable();

        await this.OnAScratchDatabaseAsync(async scratch =>
        {
            var connectionId = Guid.NewGuid();
            var configuredModelId = Guid.NewGuid();

            await using (var beforeUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(beforeUpgrade, BeforeWidening);
                var clientId = await HistoricalSchemaSeed.SeedClientAsync(beforeUpgrade, "Numeric Write Refusal");
                await SeedConnectionAsync(beforeUpgrade, connectionId, clientId, configuredModelId);
                await SeedLogicalModelAsync(beforeUpgrade, connectionId, configuredModelId, 1);
            }

            await using var afterUpgrade = CreateDbContext(scratch);
            await MigrateToAsync(afterUpgrade, ConversionMigration);

            var connection = (NpgsqlConnection)afterUpgrade.Database.GetDbConnection();
            await connection.OpenAsync();

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE ai_logical_models SET protocol_mode = @mode";
                command.Parameters.Add(new NpgsqlParameter("mode", NpgsqlTypes.NpgsqlDbType.Integer) { Value = 0 });

                var refusal = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.CheckViolation, refusal.SqlState);

                // The row still holds the name the conversion wrote.
                await using var read = connection.CreateCommand();
                read.CommandText = "SELECT protocol_mode FROM ai_logical_models LIMIT 1";
                Assert.Equal(UnclaimedIdentity + ":Responses", await read.ExecuteScalarAsync());
            }
            finally
            {
                await connection.CloseAsync();
            }
        });
    }

    [Fact]
    public async Task Reverting_RestoresEveryStoredIntegerIncludingOneNoBuildDeclared()
    {
        fixture.SkipIfUnavailable();

        await this.OnAScratchDatabaseAsync(async scratch =>
        {
            var connectionId = Guid.NewGuid();
            var configuredModelId = Guid.NewGuid();

            await using (var beforeUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(beforeUpgrade, BeforeWidening);
                var clientId = await HistoricalSchemaSeed.SeedClientAsync(beforeUpgrade, "Protocol Mode Revert");
                await SeedConnectionAsync(beforeUpgrade, connectionId, clientId, configuredModelId);

                foreach (var (stored, _) in ProtocolModeRows)
                {
                    await SeedLogicalModelAsync(beforeUpgrade, connectionId, configuredModelId, stored);
                }
            }

            await using (var upgraded = CreateDbContext(scratch))
            {
                await MigrateToAsync(upgraded, ConversionMigration);
            }

            await using (var reverted = CreateDbContext(scratch))
            {
                await MigrateToAsync(reverted, BeforeWidening);

                Assert.Equal("integer", await ColumnTypeAsync(reverted, "ai_logical_models", "protocol_mode"));
                Assert.False(await HasNumericRefusalAsync(reverted, "ai_logical_models"));
                Assert.Equal(50, await ColumnWidthAsync(reverted, "ai_connection_profiles", "provider_kind"));

                // Reverting takes the whole change back, so the credential column returns to the bound it had
                // before it: the widening to unbounded text is part of this migration rather than something that
                // shipped ahead of it.
                Assert.Equal(
                    16000,
                    await ColumnWidthAsync(reverted, "ai_connection_profiles", "protected_secret"));

                foreach (var (stored, _) in ProtocolModeRows)
                {
                    Assert.Equal(stored, await ReadRestoredIntegerAsync(reverted, "ai_logical_models", stored));
                    Assert.Equal(
                        stored,
                        await ReadRestoredIntegerAsync(reverted, "ai_logical_model_overrides", stored));
                }
            }
        });
    }

    [Fact]
    public async Task Reverting_WithANameNoIntegerCanExpress_RefusesAndChangesNothing()
    {
        fixture.SkipIfUnavailable();

        await this.OnAScratchDatabaseAsync(async scratch =>
        {
            var connectionId = Guid.NewGuid();
            var configuredModelId = Guid.NewGuid();
            const string Qualified = "meisterdev/openAi:responses";

            await using (var beforeUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(beforeUpgrade, BeforeWidening);
                var clientId = await HistoricalSchemaSeed.SeedClientAsync(beforeUpgrade, "Protocol Mode Blocked");
                await SeedConnectionAsync(beforeUpgrade, connectionId, clientId, configuredModelId);
                await SeedLogicalModelAsync(beforeUpgrade, connectionId, configuredModelId, 1);
            }

            await using (var upgraded = CreateDbContext(scratch))
            {
                await MigrateToAsync(upgraded, ConversionMigration);

                // A family that has moved onto its declared key writes the qualified spelling, which no integer
                // carries.
                await upgraded.Database.ExecuteSqlRawAsync(
                    "UPDATE ai_logical_models SET protocol_mode = {0};",
                    Qualified);
            }

            await using (var blocked = CreateDbContext(scratch))
            {
                var refusal = await Assert.ThrowsAsync<PostgresException>(() => MigrateToAsync(blocked, BeforeWidening));

                // The refusal names what blocked it, so the operator knows which rows to rewrite.
                Assert.Contains(Qualified, refusal.Message, StringComparison.Ordinal);
            }

            await using (var unchanged = CreateDbContext(scratch))
            {
                // Nothing was converted: the column is still text and still holds the value that blocked the
                // revert.
                Assert.Equal(
                    "character varying",
                    await ColumnTypeAsync(unchanged, "ai_logical_models", "protocol_mode"));
                Assert.Equal(
                    Qualified,
                    await ScalarAsync(unchanged, "SELECT protocol_mode FROM ai_logical_models LIMIT 1"));
            }
        });
    }

    [Fact]
    public async Task WideningDown_WithAStoredValueTooLongForTheOldWidth_RefusesRatherThanTruncating()
    {
        fixture.SkipIfUnavailable();

        await this.OnAScratchDatabaseAsync(async scratch =>
        {
            var connectionId = Guid.NewGuid();
            var configuredModelId = Guid.NewGuid();
            var longIdentity = BuildToken(64);

            await using (var beforeUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(beforeUpgrade, BeforeWidening);
                var clientId = await HistoricalSchemaSeed.SeedClientAsync(beforeUpgrade, "Narrowing Refusal");
                await SeedConnectionAsync(beforeUpgrade, connectionId, clientId, configuredModelId);
            }

            await using (var upgraded = CreateDbContext(scratch))
            {
                await MigrateToAsync(upgraded, ConversionMigration);
                await upgraded.Database.ExecuteSqlRawAsync(
                    "UPDATE ai_connection_profiles SET provider_kind = {0} WHERE id = {1};",
                    longIdentity,
                    connectionId);
            }

            await using (var blocked = CreateDbContext(scratch))
            {
                var refusal = await Assert.ThrowsAsync<PostgresException>(() => MigrateToAsync(blocked, BeforeWidening));
                Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, refusal.SqlState);
            }

            await using (var unchanged = CreateDbContext(scratch))
            {
                // A stored identity is never cut down to fit: the column keeps its width and the value.
                Assert.Equal(64, await ColumnWidthAsync(unchanged, "ai_connection_profiles", "provider_kind"));
                Assert.Equal(
                    longIdentity,
                    await ScalarAsync(
                        unchanged,
                        "SELECT provider_kind FROM ai_connection_profiles WHERE id = @id",
                        new NpgsqlParameter("id", connectionId)));
            }
        });
    }

    private static string BuildToken(int length)
    {
        const string Prefix = "vendor/";
        return Prefix + new string('n', length - Prefix.Length);
    }

    private static async Task SeedConnectionAsync(
        DbContext dbContext,
        Guid connectionId,
        Guid clientId,
        Guid configuredModelId,
        string providerKind = UnclaimedIdentity)
    {
        // The JSON documents travel as parameters: the raw-SQL helper formats the statement, so a brace in the
        // text would be read as a placeholder.
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ai_connection_profiles
                (id, client_id, display_name, provider_kind, base_url, auth_mode, discovery_mode,
                 default_headers, default_query_params, is_active, created_at, updated_at)
            VALUES ({0}, {1}, 'Seeded', {3}, 'https://api.openai.com/v1', 'ApiKey', 'Manual',
                    CAST({2} AS jsonb), CAST({2} AS jsonb), true, now(), now());
            """,
            [connectionId, clientId, "{}", providerKind]);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ai_configured_models
                (id, connection_profile_id, remote_model_id, display_name, operation_kinds,
                 supported_protocol_modes, supports_structured_output, supports_tool_use, source,
                 supports_reasoning, supports_prompt_caching)
            VALUES ({0}, {1}, 'gpt-4o', 'GPT-4o', CAST({2} AS jsonb),
                    CAST({3} AS jsonb), true, true, 'Manual', false, false);
            """,
            [configuredModelId, connectionId, """["Chat"]""", """["Auto","Responses","ChatCompletions"]"""]);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ai_purpose_bindings
                (id, connection_profile_id, configured_model_id, purpose, protocol_mode, is_enabled,
                 created_at, updated_at)
            VALUES ({0}, {1}, {2}, 'ReviewDefault', 'Auto', true, now(), now());
            """,
            [Guid.NewGuid(), connectionId, configuredModelId]);
    }

    // One tenant-catalog entry and one per-client override per protocol value, named after the value so the
    // assertions can find the row they seeded without carrying its id.
    private static async Task SeedLogicalModelAsync(
        DbContext dbContext,
        Guid connectionId,
        Guid configuredModelId,
        int protocolMode)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ai_logical_models
                (id, tenant_id, name, capability, connection_id, configured_model_id, reasoning_effort,
                 protocol_mode, created_at, updated_at)
            VALUES ({0}, (SELECT id FROM tenants LIMIT 1), {1}, 0, {2}, {3}, 0, {4}, now(), now());
            """,
            [Guid.NewGuid(), $"role-{protocolMode}", connectionId, configuredModelId, protocolMode]);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ai_logical_model_overrides
                (id, client_id, name, capability, connection_id, configured_model_id, reasoning_effort,
                 protocol_mode, created_at, updated_at)
            VALUES ({0}, (SELECT id FROM clients LIMIT 1), {1}, 0, {2}, {3}, 0, {4}, now(), now());
            """,
            [Guid.NewGuid(), $"role-{protocolMode}", connectionId, configuredModelId, protocolMode]);
    }

    private static async Task<string?> ReadStoredModeAsync(DbContext dbContext, string table, int seeded)
    {
        return await ScalarAsync(
            dbContext,
            $"SELECT protocol_mode FROM {table} WHERE name = @name",
            new NpgsqlParameter("name", $"role-{seeded}")) as string;
    }

    private static async Task<int?> ReadRestoredIntegerAsync(DbContext dbContext, string table, int seeded)
    {
        return await ScalarAsync(
            dbContext,
            $"SELECT protocol_mode FROM {table} WHERE name = @name",
            new NpgsqlParameter("name", $"role-{seeded}")) as int?;
    }

    private static async Task<bool> HasNumericRefusalAsync(DbContext dbContext, string table)
    {
        var found = await ScalarAsync(
            dbContext,
            """
            SELECT 1 FROM information_schema.table_constraints
             WHERE table_name = @table AND constraint_type = 'CHECK'
               AND constraint_name = 'ck_' || @table || '_protocol_mode_is_a_name'
            """,
            new NpgsqlParameter("table", table));

        return found is not null;
    }

    private static async Task<string?> ColumnTypeAsync(DbContext dbContext, string table, string column)
    {
        return await ScalarAsync(
            dbContext,
            """
            SELECT data_type FROM information_schema.columns
             WHERE table_name = @table AND column_name = @column
            """,
            new NpgsqlParameter("table", table),
            new NpgsqlParameter("column", column)) as string;
    }

    private static async Task<int?> ColumnWidthAsync(DbContext dbContext, string table, string column)
    {
        return await ScalarAsync(
            dbContext,
            """
            SELECT character_maximum_length FROM information_schema.columns
             WHERE table_name = @table AND column_name = @column
            """,
            new NpgsqlParameter("table", table),
            new NpgsqlParameter("column", column)) as int?;
    }

    private static async Task<object?> ScalarAsync(DbContext dbContext, string sql, params NpgsqlParameter[] parameters)
    {
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(parameter);
            }

            var value = await command.ExecuteScalarAsync();
            return value is DBNull ? null : value;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static MeisterProPRDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(connectionString, o => o.UseVector())
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }

    private static Task MigrateToAsync(MeisterProPRDbContext dbContext, string targetMigration)
    {
        return dbContext.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(targetMigration);
    }

    // A database of its own per test, so the historical schema never reaches the shared one and two tests that
    // both migrate backwards do not undo each other.
    private async Task OnAScratchDatabaseAsync(Func<string, Task> body)
    {
        var databaseName = $"propr_protocol_mode_migration_{Guid.NewGuid():N}";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString).ConnectionString;
        var scratch = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }
            .ConnectionString;

        try
        {
            await ExecuteOnServerAsync(adminConnectionString, $"CREATE DATABASE \"{databaseName}\";");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            Skip.If(
                true,
                "Skipping the protocol-mode migration tests because the configured role may not CREATE DATABASE. "
                + "Grant the role CREATEDB, or let the fixture start its own PostgreSQL container.");
            return;
        }

        try
        {
            await body(scratch);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnServerAsync(
                adminConnectionString,
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);");
        }
    }

    private static async Task ExecuteOnServerAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
