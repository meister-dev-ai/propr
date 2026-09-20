// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
///     The upgrade that moves one provider family's rows onto the identity key and the qualified mode names it
///     declares as an add-in.
/// </summary>
/// <remarks>
///     Run against rows in the shapes an installation holds: one connection of the family being moved, one of
///     another family that must be left exactly as it is, and daily usage samples under both spellings, including
///     the pair whose identities the unique index would refuse to merge by a plain update.
/// </remarks>
[Collection("PostgresIntegration")]
public sealed class StoreOpenAiRowsUnderItsDeclaredNamesMigrationTests(PostgresContainerFixture fixture)
{
    private const string BeforeMove = "CoverageGateRecall";
    private const string MoveMigration = "ProviderAddInArchitecture";

    private const string LegacyIdentity = "OpenAi";
    private const string DeclaredIdentity = "meisterdev/openAi";
    private const string OtherIdentity = "ContosoGateway";

    [Fact]
    public async Task Upgrading_NamesTheFamilysRowsAsItDeclaresThemAndLeavesAnotherFamilyAlone()
    {
        fixture.SkipIfUnavailable();

        await this.OnAScratchDatabaseAsync(async scratch =>
        {
            var moved = new SeededConnection();
            var untouched = new SeededConnection();

            await using (var beforeMove = CreateDbContext(scratch))
            {
                await MigrateToAsync(beforeMove, BeforeMove);
                var clientId = await HistoricalSchemaSeed.SeedClientAsync(beforeMove, "OpenAI Identity Move");

                await SeedConnectionAsync(beforeMove, moved, clientId, LegacyIdentity, "ApiKey", "Responses");

                // A family none of the shipped add-ins claims. It holds the credential shape this one also
                // declares, which is what the identity on the row is there to tell apart, and the one migration
                // moves every shipped family at once so an unclaimed identity is what proves the scoping.
                await SeedConnectionAsync(beforeMove, untouched, clientId, OtherIdentity, "ApiKey", "AnthropicMessages");
                await SeedAllowListAsync(beforeMove, LegacyIdentity, OtherIdentity);
            }

            await using (var afterMove = CreateDbContext(scratch))
            {
                await MigrateToAsync(afterMove, MoveMigration);

                Assert.Equal(DeclaredIdentity, await IdentityAsync(afterMove, moved));
                Assert.Equal($"{DeclaredIdentity}:ApiKey", await AuthModeAsync(afterMove, moved));
                Assert.Equal(
                    $"{DeclaredIdentity}:Responses",
                    await BoundProtocolModeAsync(afterMove, moved));

                // The order the operator chose survives, and the two shapes no family owns stay unqualified.
                Assert.Equal(
                    $"Auto|{DeclaredIdentity}:Responses|Embeddings",
                    await ListedProtocolModesAsync(afterMove, moved));

                // A family that has not moved is not touched by another family's migration.
                Assert.Equal(OtherIdentity, await IdentityAsync(afterMove, untouched));
                Assert.Equal("ApiKey", await AuthModeAsync(afterMove, untouched));
                Assert.Equal("AnthropicMessages", await BoundProtocolModeAsync(afterMove, untouched));
                Assert.Equal("Auto|AnthropicMessages|Embeddings", await ListedProtocolModesAsync(afterMove, untouched));

                Assert.Equal($"{DeclaredIdentity}|{OtherIdentity}", await AllowListAsync(afterMove));
            }
        });
    }

    // This family owns two wire shapes. A profile on the one left behind would quarantine on the wire-shape axis
    // once the family owns its vocabulary, so both have to move.
    [Fact]
    public async Task Upgrading_MovesBothWireShapesTheFamilyOwns()
    {
        fixture.SkipIfUnavailable();

        await this.OnAScratchDatabaseAsync(async scratch =>
        {
            var responses = new SeededConnection();
            var chatCompletions = new SeededConnection();

            await using (var beforeMove = CreateDbContext(scratch))
            {
                await MigrateToAsync(beforeMove, BeforeMove);
                var clientId = await HistoricalSchemaSeed.SeedClientAsync(beforeMove, "OpenAI Wire Shapes");

                await SeedConnectionAsync(beforeMove, responses, clientId, LegacyIdentity, "ApiKey", "Responses", "Responses profile");
                await SeedConnectionAsync(beforeMove, chatCompletions, clientId, LegacyIdentity, "ApiKey", "ChatCompletions", "Chat completions profile");
            }

            await using (var afterMove = CreateDbContext(scratch))
            {
                await MigrateToAsync(afterMove, MoveMigration);

                Assert.Equal($"{DeclaredIdentity}:Responses", await BoundProtocolModeAsync(afterMove, responses));
                Assert.Equal(
                    $"Auto|{DeclaredIdentity}:Responses|Embeddings",
                    await ListedProtocolModesAsync(afterMove, responses));

                Assert.Equal($"{DeclaredIdentity}:ChatCompletions", await BoundProtocolModeAsync(afterMove, chatCompletions));
                Assert.Equal(
                    $"Auto|{DeclaredIdentity}:ChatCompletions|Embeddings",
                    await ListedProtocolModesAsync(afterMove, chatCompletions));
            }
        });
    }

    // The case a plain update cannot do: the same client, model, logical model and day already carries a sample
    // under the declared key, which a connection created after the family declared it produces.
    [Fact]
    public async Task Upgrading_FoldsADailySampleOntoTheOneAlreadyWrittenUnderTheDeclaredIdentity()
    {
        fixture.SkipIfUnavailable();

        await this.OnAScratchDatabaseAsync(async scratch =>
        {
            Guid clientId;

            await using (var beforeMove = CreateDbContext(scratch))
            {
                await MigrateToAsync(beforeMove, BeforeMove);
                clientId = await HistoricalSchemaSeed.SeedClientAsync(beforeMove, "OpenAI Usage Fold");

                await SeedUsageSampleAsync(beforeMove, clientId, "gpt-5.4-mini", "reviewer", LegacyIdentity, 100, 4m);
                await SeedUsageSampleAsync(beforeMove, clientId, "gpt-5.4-mini", "reviewer", DeclaredIdentity, 30, 1m);

                // A sample with no counterpart moves rather than folds, and one nothing priced stays unpriced.
                await SeedUsageSampleAsync(beforeMove, clientId, "gpt-5-nano", "triage", LegacyIdentity, 7, null);
                await SeedUsageSampleAsync(beforeMove, clientId, "claude-opus-5", "reviewer", OtherIdentity, 55, 9m);
            }

            await using (var afterMove = CreateDbContext(scratch))
            {
                await MigrateToAsync(afterMove, MoveMigration);

                // One series, carrying every token and every cent that was on the two rows.
                Assert.Equal(1, await SampleCountAsync(afterMove, clientId, "gpt-5.4-mini"));
                Assert.Equal(130L, await SampleInputTokensAsync(afterMove, clientId, "gpt-5.4-mini", DeclaredIdentity));
                Assert.Equal(5m, await SampleCostAsync(afterMove, clientId, "gpt-5.4-mini", DeclaredIdentity));

                Assert.Equal(7L, await SampleInputTokensAsync(afterMove, clientId, "gpt-5-nano", DeclaredIdentity));
                Assert.Null(await SampleCostAsync(afterMove, clientId, "gpt-5-nano", DeclaredIdentity));

                // Nothing is left under the superseded identity, and another family's series is untouched.
                Assert.Equal(0, await SampleCountUnderAsync(afterMove, clientId, LegacyIdentity));
                Assert.Equal(55L, await SampleInputTokensAsync(afterMove, clientId, "claude-opus-5", OtherIdentity));
            }
        });
    }

    [Fact]
    public async Task Reverting_PutsEveryRowBackUnderTheNamesTheFamilySuperseded()
    {
        fixture.SkipIfUnavailable();

        await this.OnAScratchDatabaseAsync(async scratch =>
        {
            var moved = new SeededConnection();
            Guid clientId;

            await using (var beforeMove = CreateDbContext(scratch))
            {
                await MigrateToAsync(beforeMove, BeforeMove);
                clientId = await HistoricalSchemaSeed.SeedClientAsync(beforeMove, "OpenAI Identity Revert");

                await SeedConnectionAsync(beforeMove, moved, clientId, LegacyIdentity, "ApiKey", "Responses");
                await SeedAllowListAsync(beforeMove, LegacyIdentity, OtherIdentity);
                await SeedUsageSampleAsync(beforeMove, clientId, "gpt-5.4-mini", "reviewer", LegacyIdentity, 100, 4m);
                await SeedUsageSampleAsync(beforeMove, clientId, "gpt-5.4-mini", "reviewer", DeclaredIdentity, 30, 1m);
            }

            await using (var upgraded = CreateDbContext(scratch))
            {
                await MigrateToAsync(upgraded, MoveMigration);
            }

            await using (var reverted = CreateDbContext(scratch))
            {
                await MigrateToAsync(reverted, BeforeMove);

                Assert.Equal(LegacyIdentity, await IdentityAsync(reverted, moved));
                Assert.Equal("ApiKey", await AuthModeAsync(reverted, moved));
                Assert.Equal("Responses", await BoundProtocolModeAsync(reverted, moved));
                Assert.Equal("Auto|Responses|Embeddings", await ListedProtocolModesAsync(reverted, moved));
                Assert.Equal($"{LegacyIdentity}|{OtherIdentity}", await AllowListAsync(reverted));

                // The two samples that were folded come back as one carrying the total: a fold is reversible in
                // what it holds, not in how many rows held it.
                Assert.Equal(1, await SampleCountAsync(reverted, clientId, "gpt-5.4-mini"));
                Assert.Equal(130L, await SampleInputTokensAsync(reverted, clientId, "gpt-5.4-mini", LegacyIdentity));
                Assert.Equal(5m, await SampleCostAsync(reverted, clientId, "gpt-5.4-mini", LegacyIdentity));
            }
        });
    }

    private sealed record SeededConnection
    {
        public Guid ConnectionId { get; } = Guid.NewGuid();

        public Guid ConfiguredModelId { get; } = Guid.NewGuid();
    }

    private static async Task SeedConnectionAsync(
        DbContext dbContext,
        SeededConnection seeded,
        Guid clientId,
        string providerKind,
        string authMode,
        string ownedProtocolMode,
        string? displayName = null)
    {
        // The JSON documents travel as parameters: the raw-SQL helper formats the statement, so a brace in the
        // text would be read as a placeholder.
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ai_connection_profiles
                (id, client_id, display_name, provider_kind, base_url, auth_mode, discovery_mode,
                 default_headers, default_query_params, is_active, created_at, updated_at)
            VALUES ({0}, {1}, {2}, {3}, 'https://api.openai.com/v1', {4}, 'Manual',
                    CAST({5} AS jsonb), CAST({5} AS jsonb), true, now(), now());
            """,
            [
                seeded.ConnectionId,
                clientId,
                displayName ?? $"Seeded {providerKind}",
                providerKind,
                authMode,
                "{}",
            ]);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ai_configured_models
                (id, connection_profile_id, remote_model_id, display_name, operation_kinds,
                 supported_protocol_modes, supports_structured_output, supports_tool_use, source,
                 supports_reasoning, supports_prompt_caching)
            VALUES ({0}, {1}, {4}, 'Seeded model', CAST({2} AS jsonb),
                    CAST({3} AS jsonb), true, true, 'Manual', false, false);
            """,
            [
                seeded.ConfiguredModelId,
                seeded.ConnectionId,
                """["Chat"]""",
                $"""["Auto","{ownedProtocolMode}","Embeddings"]""",
                $"model-of-{providerKind}",
            ]);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ai_purpose_bindings
                (id, connection_profile_id, configured_model_id, purpose, protocol_mode, is_enabled,
                 created_at, updated_at)
            VALUES ({0}, {1}, {2}, 'ReviewDefault', {3}, true, now(), now());
            """,
            [Guid.NewGuid(), seeded.ConnectionId, seeded.ConfiguredModelId, ownedProtocolMode]);
    }

    private static Task SeedAllowListAsync(DbContext dbContext, params string[] permitted)
    {
        return dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE tenants SET allowed_ai_provider_kinds = CAST({0} AS jsonb);",
            [$"[{string.Join(',', permitted.Select(entry => $"\"{entry}\""))}]"]);
    }

    private static Task SeedUsageSampleAsync(
        DbContext dbContext,
        Guid clientId,
        string modelId,
        string logicalModelName,
        string providerKind,
        long inputTokens,
        decimal? estimatedCostUsd)
    {
        return dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO client_token_usage_samples
                (id, client_id, model_id, logical_model_name, provider_kind, date, input_tokens, output_tokens,
                 cached_input_tokens, cache_write_tokens, reasoning_tokens, estimated_cost_usd)
            VALUES ({0}, {1}, {2}, {3}, {4}, DATE '2026-09-13', {5}, 0, 0, 0, 0, {6});
            """,
            [Guid.NewGuid(), clientId, modelId, logicalModelName, providerKind, inputTokens, estimatedCostUsd!]);
    }

    private static async Task<string?> IdentityAsync(DbContext dbContext, SeededConnection seeded)
    {
        return await ScalarAsync(
            dbContext,
            "SELECT provider_kind FROM ai_connection_profiles WHERE id = @id",
            new NpgsqlParameter("id", seeded.ConnectionId)) as string;
    }

    private static async Task<string?> AuthModeAsync(DbContext dbContext, SeededConnection seeded)
    {
        return await ScalarAsync(
            dbContext,
            "SELECT auth_mode FROM ai_connection_profiles WHERE id = @id",
            new NpgsqlParameter("id", seeded.ConnectionId)) as string;
    }

    private static async Task<string?> BoundProtocolModeAsync(DbContext dbContext, SeededConnection seeded)
    {
        return await ScalarAsync(
            dbContext,
            "SELECT protocol_mode FROM ai_purpose_bindings WHERE connection_profile_id = @id",
            new NpgsqlParameter("id", seeded.ConnectionId)) as string;
    }

    // Read in order and joined, so the assertion is about the values and the order they are stored in rather than
    // about how PostgreSQL renders a document.
    private static async Task<string?> ListedProtocolModesAsync(DbContext dbContext, SeededConnection seeded)
    {
        return await ScalarAsync(
            dbContext,
            """
            SELECT string_agg(listed.mode, '|' ORDER BY listed.ordinal)
              FROM ai_configured_models,
                   jsonb_array_elements_text(supported_protocol_modes) WITH ORDINALITY AS listed(mode, ordinal)
             WHERE id = @id
            """,
            new NpgsqlParameter("id", seeded.ConfiguredModelId)) as string;
    }

    private static async Task<string?> AllowListAsync(DbContext dbContext)
    {
        return await ScalarAsync(
            dbContext,
            """
            SELECT string_agg(listed.entry, '|' ORDER BY listed.ordinal)
              FROM tenants,
                   jsonb_array_elements_text(allowed_ai_provider_kinds) WITH ORDINALITY AS listed(entry, ordinal)
            """) as string;
    }

    private static async Task<int> SampleCountAsync(DbContext dbContext, Guid clientId, string modelId)
    {
        return Convert.ToInt32(
            await ScalarAsync(
                dbContext,
                "SELECT count(*) FROM client_token_usage_samples WHERE client_id = @client AND model_id = @model",
                new NpgsqlParameter("client", clientId),
                new NpgsqlParameter("model", modelId)));
    }

    private static async Task<int> SampleCountUnderAsync(DbContext dbContext, Guid clientId, string providerKind)
    {
        return Convert.ToInt32(
            await ScalarAsync(
                dbContext,
                """
                SELECT count(*) FROM client_token_usage_samples
                 WHERE client_id = @client AND provider_kind = @provider
                """,
                new NpgsqlParameter("client", clientId),
                new NpgsqlParameter("provider", providerKind)));
    }

    private static async Task<long?> SampleInputTokensAsync(
        DbContext dbContext,
        Guid clientId,
        string modelId,
        string providerKind)
    {
        return await ScalarAsync(
            dbContext,
            """
            SELECT input_tokens FROM client_token_usage_samples
             WHERE client_id = @client AND model_id = @model AND provider_kind = @provider
            """,
            new NpgsqlParameter("client", clientId),
            new NpgsqlParameter("model", modelId),
            new NpgsqlParameter("provider", providerKind)) as long?;
    }

    private static async Task<decimal?> SampleCostAsync(
        DbContext dbContext,
        Guid clientId,
        string modelId,
        string providerKind)
    {
        return await ScalarAsync(
            dbContext,
            """
            SELECT estimated_cost_usd FROM client_token_usage_samples
             WHERE client_id = @client AND model_id = @model AND provider_kind = @provider
            """,
            new NpgsqlParameter("client", clientId),
            new NpgsqlParameter("model", modelId),
            new NpgsqlParameter("provider", providerKind)) as decimal?;
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
        var databaseName = $"propr_openai_identity_migration_{Guid.NewGuid():N}";
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
                "Skipping the provider identity migration tests because the configured role may not CREATE "
                + "DATABASE. Grant the role CREATEDB, or let the fixture start its own PostgreSQL container.");
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
