// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.TenantData;
using MeisterDev.ProPR.Domain;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.TenantData;
using MeisterDev.ProPR.ProCursor.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.TenantData;

/// <summary>
///     Deletes, exports and retains tenant data on a database of its own at the latest migration, because the tests
///     place rows in every table that carries a tenant or a client. The ProCursor tables are in the same database, as in
///     an installation without a separate ProCursor database.
/// </summary>
[Collection("PostgresIntegration4")]
public sealed class TenantDataLifecycleTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly string _database = "tenant_data_" + Guid.NewGuid().ToString("N");

    // The clock starts at a whole second, so the times stored in the database compare exactly, and after the rows
    // that the seeder stamps with the current time, so their update time starts no quiet period.
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddMinutes(10));

    private NpgsqlConnection _connection = null!;
    private MeisterProPRDbContext _context = null!;
    private ProCursorOperationalDbContext _proCursorContext = null!;
    private SchemaRowSeeder _seeder = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        await using (var migrations = Context(fixture.ConnectionString))
        {
            await fixture.CreateDatabaseAtMigrationAsync(_database, migrations.Database.GetMigrations().Last());
        }

        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = _database }
            .ConnectionString;
        _context = Context(connection);
        _proCursorContext = new ProCursorOperationalDbContext(
            new DbContextOptionsBuilder<ProCursorOperationalDbContext>().UseNpgsql(connection, o => o.UseVector()).Options);
        _connection = new NpgsqlConnection(connection);
        await _connection.OpenAsync();
        _seeder = new SchemaRowSeeder(_connection);
    }

    public async Task DisposeAsync()
    {
        if (_connection is null)
        {
            return;
        }

        await _connection.DisposeAsync();
        await _context.DisposeAsync();
        await _proCursorContext.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task DeletingATenantLeavesNoRowThatReferencesItOrItsClients()
    {
        var (tenant, client) = await TenantAsync("deleted");
        var (otherTenant, otherClient) = await TenantAsync("kept");
        var owned = await _seeder.OwnedColumnsAsync();
        foreach (var table in owned.Select(item => item.Table).Distinct().Where(table => table is not ("tenants" or "clients")))
        {
            await _seeder.InsertAsync(table, Ids(owned, table, tenant, client));
        }

        await _seeder.InsertAsync("review_jobs", Ids(owned, "review_jobs", otherTenant, otherClient));
        var member = await UserAsync("member", "User", tenant);
        var administrator = await UserAsync("administrator", "Admin", tenant);
        var shared = await UserAsync("shared", "User", tenant, otherTenant);

        await DeleteAsync(Lifecycle(), tenant);

        foreach (var (table, column) in owned)
        {
            Assert.True(
                await CountAsync($"SELECT count(*) FROM \"{table}\" WHERE \"{column}\" IN (@a, @b)", tenant, client) == 0,
                $"{table}.{column} still references the deleted tenant or its client.");
        }

        Assert.Equal(1, await CountAsync("SELECT count(*) FROM review_jobs WHERE client_id = @a", otherClient, otherClient));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM app_users WHERE id = @a", member, member));
        Assert.Equal(2, await CountAsync("SELECT count(*) FROM app_users WHERE id IN (@a, @b)", administrator, shared));
    }

    [Fact]
    public async Task TheSystemTenantCannotBeDeletedAndAnUnknownTenantIsNotFound()
    {
        var lifecycle = Lifecycle();

        await Assert.ThrowsAsync<TenantDataLifecycleException>(() => lifecycle.DeleteTenantAsync(SystemTenant.Id));
        Assert.Null(await lifecycle.DeleteTenantAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task TheFirstRequestStopsTheWorkOfTheTenantAndALaterOneDeletesIt()
    {
        var (tenant, client) = await TenantAsync("stopped");
        await ExecuteAsync("UPDATE tenants SET is_active = true WHERE id = @a", tenant);
        await ExecuteAsync("UPDATE clients SET is_active = true WHERE id = @a", client);
        var webhook = await _seeder.InsertAsync("webhook_configurations", new Dictionary<string, object> { ["client_id"] = client, ["is_active"] = true });
        var crawl = await _seeder.InsertAsync("crawl_configurations", new Dictionary<string, object> { ["client_id"] = client, ["is_active"] = true });
        var mention = await _seeder.InsertAsync("mention_configurations", new Dictionary<string, object> { ["client_id"] = client, ["is_active"] = true });
        var review = await _seeder.InsertAsync("review_jobs", new Dictionary<string, object> { ["client_id"] = client, ["status"] = "Pending" });
        var threadPass = await _seeder.InsertAsync("thread_pass_jobs", new Dictionary<string, object> { ["client_id"] = client, ["status"] = "Pending" });
        var reply = await _seeder.InsertAsync("mention_reply_jobs", new Dictionary<string, object> { ["client_id"] = client, ["status"] = "Pending" });
        var sync = await _seeder.InsertAsync(
            "discussion_synchronization_states", new Dictionary<string, object> { ["client_id"] = client, ["is_closed"] = false });
        var lifecycle = Lifecycle();

        var first = await lifecycle.DeleteTenantAsync(tenant);

        Assert.Null(first!.Deleted);
        Assert.Equal(TenantDataLifecycle.QuietPeriod, first.Pending!.RetryAfter);
        Assert.Empty(first.Pending.Running);
        Assert.Equal("False", await TextAsync("SELECT is_active::text FROM tenants WHERE id = @a", tenant), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("False", await TextAsync("SELECT is_active::text FROM clients WHERE id = @a", client), StringComparer.OrdinalIgnoreCase);
        foreach (var (table, id) in new[] { ("webhook_configurations", webhook), ("crawl_configurations", crawl), ("mention_configurations", mention) })
        {
            Assert.Equal("False", await TextAsync($"SELECT is_active::text FROM {table} WHERE id = @a", (Guid)id!), StringComparer.OrdinalIgnoreCase);
        }

        Assert.Equal("Stopped", await TextAsync("SELECT status FROM review_jobs WHERE id = @a", (Guid)review!));
        Assert.Equal("Cancelled", await TextAsync("SELECT status FROM thread_pass_jobs WHERE id = @a", (Guid)threadPass!));
        Assert.Equal("Failed", await TextAsync("SELECT status FROM mention_reply_jobs WHERE id = @a", (Guid)reply!));
        Assert.Equal(
            "True", await TextAsync("SELECT is_closed::text FROM discussion_synchronization_states WHERE id = @a", (Guid)sync!),
            StringComparer.OrdinalIgnoreCase);

        _clock.Advance(TenantDataLifecycle.QuietPeriod - TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), (await lifecycle.DeleteTenantAsync(tenant))!.Pending!.RetryAfter);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var deleted = (await lifecycle.DeleteTenantAsync(tenant))!.Deleted;

        Assert.NotNull(deleted);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM tenants WHERE id = @a", tenant, tenant));
    }

    [Fact]
    public async Task TheDeletionWaitsUntilTheLeaseOfAStoppedReviewHasExpired()
    {
        var (tenant, client) = await TenantAsync("leased");
        var review = (Guid)(await _seeder.InsertAsync(
            "review_jobs", new Dictionary<string, object>
                { ["client_id"] = client, ["status"] = "Processing", ["lease_expires_at"] = _clock.GetUtcNow().AddMinutes(3) }))!;
        var cancellations = Substitute.For<IReviewJobCancellationRegistry>();
        var lifecycle = Lifecycle(cancellations);

        var first = await lifecycle.DeleteTenantAsync(tenant);

        Assert.Equal(TimeSpan.FromMinutes(3), first!.Pending!.RetryAfter);
        Assert.Equal(1, first.Pending.Running["reviews"]);
        Assert.Equal("Stopped", await TextAsync("SELECT status FROM review_jobs WHERE id = @a", review));
        cancellations.Received(1).Cancel(review);

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(TimeSpan.FromMinutes(1), (await lifecycle.DeleteTenantAsync(tenant))!.Pending!.RetryAfter);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull((await lifecycle.DeleteTenantAsync(tenant))!.Deleted);
    }

    [Fact]
    public async Task TheDeletionWaitsForARunningThreadPassAndARunningIndexJobAndDeletesTheIndexes()
    {
        var (tenant, client) = await TenantAsync("running");
        var now = _clock.GetUtcNow();
        var threadPass = (Guid)(await _seeder.InsertAsync(
            "thread_pass_jobs", new Dictionary<string, object>
                { ["client_id"] = client, ["status"] = "Processing", ["processing_started_at"] = now.AddMinutes(-5) }))!;
        var source = (Guid)(await _seeder.InsertAsync(
            "procursor_knowledge_sources", new Dictionary<string, object> { ["client_id"] = client, ["is_enabled"] = true }))!;
        var waiting = (Guid)(await _seeder.InsertAsync(
            "procursor_index_jobs", new Dictionary<string, object> { ["knowledge_source_id"] = source, ["status"] = (short)0 }))!;
        await _seeder.InsertAsync(
            "procursor_index_jobs", new Dictionary<string, object>
                { ["knowledge_source_id"] = source, ["status"] = (short)1, ["started_at"] = now.AddMinutes(-10) });
        var snapshot = await _seeder.InsertAsync("procursor_index_snapshots", new Dictionary<string, object> { ["knowledge_source_id"] = source });
        await _seeder.InsertAsync("procursor_knowledge_chunks", new Dictionary<string, object> { ["snapshot_id"] = snapshot! });
        var lifecycle = Lifecycle();

        var first = await lifecycle.DeleteTenantAsync(tenant);

        // The index job started ten minutes ago and counts as running for an hour, which is the longest wait.
        Assert.Equal(TenantDataLifecycle.IndexJobRunLimit - TimeSpan.FromMinutes(10), first!.Pending!.RetryAfter);
        Assert.Equal((1, 1), (first.Pending.Running["threadPasses"], first.Pending.Running["indexJobs"]));
        Assert.Equal("Processing", await TextAsync("SELECT status FROM thread_pass_jobs WHERE id = @a", threadPass));
        Assert.Equal("5", await TextAsync("SELECT status::text FROM procursor_index_jobs WHERE id = @a", waiting));
        Assert.Equal(
            "False", await TextAsync("SELECT is_enabled::text FROM procursor_knowledge_sources WHERE id = @a", source), StringComparer.OrdinalIgnoreCase);

        _clock.Advance(first.Pending.RetryAfter);
        var deleted = (await lifecycle.DeleteTenantAsync(tenant))!.Deleted;

        Assert.NotNull(deleted);
        Assert.Equal(
            (2, 1, 1),
            (deleted.DeletedRows["procursor_index_jobs"], deleted.DeletedRows["procursor_index_snapshots"],
                deleted.DeletedRows["procursor_knowledge_chunks"]));
    }

    [Fact]
    public async Task TheExportContainsTheReviewRecordsWithoutSecrets()
    {
        var (tenant, client) = await TenantAsync("exported");
        await _seeder.InsertAsync(
            "client_scm_connections", new Dictionary<string, object>
                { ["client_id"] = client, ["encrypted_secret_material"] = "secret-material-never-exported" });
        var job = await _seeder.InsertAsync(
            "review_jobs", new Dictionary<string, object>
                { ["client_id"] = client, ["status"] = "Completed", ["result_summary"] = "Two findings", ["pr_title"] = "Add the export" });

        // The comments are stored as the application writes them.
        await _seeder.InsertAsync(
            "review_file_results", new Dictionary<string, object>
            {
                ["job_id"] = job!, ["file_path"] = "src/Export.cs",
                ["comments_json"] = new SchemaRowSeeder.Jsonb("""[{"FilePath":"src/Export.cs","LineNumber":3,"Severity":1,"Message":"Close the stream."}]"""),
            });
        var protocol = await _seeder.InsertAsync("review_job_protocols", new Dictionary<string, object> { ["job_id"] = job! });
        await _seeder.InsertAsync(
            "protocol_events", new Dictionary<string, object>
                { ["protocol_id"] = protocol!, ["name"] = "ai-call", ["occurred_at"] = DateTimeOffset.UtcNow });
        using var output = new MemoryStream();

        Assert.True(await Lifecycle().WriteExportAsync(tenant, output, new(IncludeProtocols: true)));
        Assert.False(await Lifecycle().WriteExportAsync(Guid.NewGuid(), new MemoryStream(), new(false)));

        var text = System.Text.Encoding.UTF8.GetString(output.ToArray());
        Assert.DoesNotContain("secret-material-never-exported", text, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.Equal((TenantDataLifecycle.ExportFormat, 1), (root.GetProperty("format").GetString(), root.GetProperty("version").GetInt32()));
        var exportedClient = Assert.Single(root.GetProperty("clients").EnumerateArray());
        Assert.Single(exportedClient.GetProperty("scmConnections").EnumerateArray());
        var exportedJob = Assert.Single(exportedClient.GetProperty("reviewJobs").EnumerateArray());
        Assert.Equal(("Two findings", "Add the export"), (exportedJob.GetProperty("summary").GetString(), exportedJob.GetProperty("title").GetString()));
        Assert.Equal("Close the stream.", exportedJob.GetProperty("fileResults")[0].GetProperty("comments")[0].GetProperty("message").GetString());
        Assert.Equal("ai-call", exportedJob.GetProperty("protocols")[0].GetProperty("events")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ProtocolEventsOlderThanTheRetentionAreDeletedAndTheJobKeepsItsResult()
    {
        var (tenant, client) = await TenantAsync("retained");
        var (_, unlimitedClient) = await TenantAsync("unlimited");
        var now = DateTimeOffset.UtcNow;

        async Task<object> EventAsync(Guid owner, DateTimeOffset at)
        {
            var job = await _seeder.InsertAsync("review_jobs", new Dictionary<string, object> { ["client_id"] = owner, ["result_summary"] = "Kept" });
            var protocol = await _seeder.InsertAsync("review_job_protocols", new Dictionary<string, object> { ["job_id"] = job! });
            return (await _seeder.InsertAsync("protocol_events", new Dictionary<string, object> { ["protocol_id"] = protocol!, ["occurred_at"] = at }))!;
        }

        var old = await EventAsync(client, now.AddDays(-31));
        var recent = await EventAsync(client, now.AddDays(-29));
        var unlimited = await EventAsync(unlimitedClient, now.AddDays(-400));
        var lifecycle = Lifecycle();

        Assert.True(await lifecycle.SetProtocolRetentionAsync(tenant, 30));
        var purged = await lifecycle.PurgeExpiredProtocolEventsAsync(now);

        Assert.Equal(1, purged);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM protocol_events WHERE id = @a", (Guid)old, (Guid)old));
        Assert.Equal(2, await CountAsync("SELECT count(*) FROM protocol_events WHERE id IN (@a, @b)", (Guid)recent, (Guid)unlimited));
        Assert.Equal(2, await CountAsync("SELECT count(*) FROM review_jobs WHERE client_id = @a AND result_summary = 'Kept'", client, client));
        await Assert.ThrowsAsync<TenantDataLifecycleException>(() => lifecycle.SetProtocolRetentionAsync(tenant, 0));
    }

    [Fact]
    public async Task ErasingAnAuthorClearsTheIdentityInTheTenantOnlyAndRecordsItInTheAudit()
    {
        var (tenant, client) = await TenantAsync("erasing");
        var (_, otherClient) = await TenantAsync("untouched");
        var job = await _seeder.InsertAsync(
            "review_jobs", new Dictionary<string, object>
                { ["client_id"] = client, ["pr_author_external_user_id"] = "4711", ["pr_author_login"] = "dev", ["pr_author_display_name"] = "Dev Eloper" });
        var protocol = await _seeder.InsertAsync("review_job_protocols", new Dictionary<string, object> { ["job_id"] = job! });
        await _seeder.InsertAsync(
            "protocol_events", new Dictionary<string, object> { ["protocol_id"] = protocol!, ["input_text_sample"] = "Dev Eloper wrote" });
        await _seeder.InsertAsync(
            "mention_reply_jobs", new Dictionary<string, object>
                { ["client_id"] = client, ["comment_author_login"] = "dev" });
        var pullRequest = await _seeder.InsertAsync("retained_pull_requests", new Dictionary<string, object> { ["client_id"] = client });
        var thread = await _seeder.InsertAsync("retained_threads", new Dictionary<string, object> { ["retained_pull_request_id"] = pullRequest! });
        await _seeder.InsertAsync(
            "retained_thread_comments", new Dictionary<string, object>
                { ["retained_thread_id"] = thread!, ["author_identity"] = "4711" });
        await _seeder.InsertAsync(
            "review_jobs", new Dictionary<string, object>
                { ["client_id"] = otherClient, ["pr_author_external_user_id"] = "4711" });

        var result = await Lifecycle().EraseAuthorAsync(tenant, new("4711", "dev"), null, "DPA-2026-17");

        Assert.Equal(new IdentityErasureResult(1, 1, 1, 1), result);
        Assert.Equal(
            0,
            await CountAsync(
                "SELECT count(*) FROM review_jobs WHERE client_id = @a AND (pr_author_external_user_id IS NOT NULL OR pr_author_display_name IS NOT NULL)",
                client, client));
        Assert.Equal(
            1, await CountAsync("SELECT count(*) FROM review_jobs WHERE client_id = @a AND pr_author_external_user_id = '4711'", otherClient, otherClient));
        await using var audit = new NpgsqlCommand(
            "SELECT summary FROM tenant_audit_entries WHERE tenant_id = @tenant AND event_type = 'author.erased'", _connection);
        audit.Parameters.AddWithValue("tenant", tenant);
        var summary = (string)(await audit.ExecuteScalarAsync())!;
        Assert.Contains("DPA-2026-17", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("4711", summary, StringComparison.Ordinal);
    }

    private TenantDataLifecycle Lifecycle(IReviewJobCancellationRegistry? cancellations = null) =>
        new(_context, cancellations, _clock, new ProCursorTenantData(_proCursorContext));

    /// <summary>Requests the deletion until it goes ahead, waiting as long as each answer asks.</summary>
    private async Task<TenantDeletionResult> DeleteAsync(TenantDataLifecycle lifecycle, Guid tenant)
    {
        for (var request = 0; request < 3; request++)
        {
            var answer = await lifecycle.DeleteTenantAsync(tenant);
            if (answer!.Deleted is { } deleted)
            {
                return deleted;
            }

            _clock.Advance(answer.Pending!.RetryAfter);
        }

        throw new InvalidOperationException("The tenant was not deleted after three requests.");
    }

    private async Task<(Guid Tenant, Guid Client)> TenantAsync(string name)
    {
        var tenant = (Guid)(await _seeder.InsertAsync(
            "tenants", new Dictionary<string, object>
                { ["slug"] = name + "-" + Guid.NewGuid().ToString("N")[..8], ["display_name"] = name }))!;
        var client = (Guid)(await _seeder.InsertAsync(
            "clients", new Dictionary<string, object>
                { ["tenant_id"] = tenant, ["display_name"] = name + " client" }))!;
        return (tenant, client);
    }

    private async Task<Guid> UserAsync(string name, string role, params Guid[] tenants)
    {
        var user = (Guid)(await _seeder.InsertAsync(
            "app_users", new Dictionary<string, object>
                { ["username"] = name + "-" + Guid.NewGuid().ToString("N")[..8], ["global_role"] = role }))!;
        foreach (var tenant in tenants)
        {
            await _seeder.InsertAsync("tenant_memberships", new Dictionary<string, object> { ["tenant_id"] = tenant, ["user_id"] = user });
        }

        return user;
    }

    private static Dictionary<string, object> Ids(
        IReadOnlyList<(string Table, string Column)> owned, string table,
        Guid tenant, Guid client) =>
        owned.Where(item => item.Table == table).ToDictionary(
            item => item.Column,
            item => (object)(item.Column is "tenant_id" or "TenantId" ? tenant : client), StringComparer.Ordinal);

    private async Task<long> CountAsync(string sql, Guid first, Guid second)
    {
        await using var command = new NpgsqlCommand(sql, _connection);
        command.Parameters.AddWithValue("a", first);
        command.Parameters.AddWithValue("b", second);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string?> TextAsync(string sql, Guid id)
    {
        await using var command = new NpgsqlCommand(sql, _connection);
        command.Parameters.AddWithValue("a", id);
        return (string?)await command.ExecuteScalarAsync();
    }

    private async Task ExecuteAsync(string sql, Guid id)
    {
        await using var command = new NpgsqlCommand(sql, _connection);
        command.Parameters.AddWithValue("a", id);
        await command.ExecuteNonQueryAsync();
    }

    private static MeisterProPRDbContext Context(string connectionString) => new(
        new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(connectionString, o => o.UseVector()).Options);
}
