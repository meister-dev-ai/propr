// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging.Abstractions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Services;
using MeisterDev.ProPR.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Persistence;

namespace MeisterDev.ProPR.CodeInsights.Tests.Persistence;

/// <summary>
///     Lifecycle coverage for collected code-insight data against a real PostgreSQL instance
///     (Testcontainers / pgvector) over the real schema applied via migrations. The in-memory provider
///     enforces neither database-level cascade nor unique constraints, so the two lifecycle guarantees the
///     feature makes (cascade-delete with the client, and idempotency backed by the natural-key
///     constraint) can only be demonstrated here.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class CodeInsightRetentionPostgresIntegrationTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _tenantId = Guid.NewGuid();
    private MeisterProPRDbContext _dbContext = null!;
    private CodeInsightFindingIngestionService _ingestion = null!;
    private CodeInsightFindingStore _store = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);

        // Isolate from any rows other tests in the shared collection may have left behind.
        await this._dbContext.CodeInsightPullRequests.ExecuteDeleteAsync();

        var now = DateTimeOffset.UtcNow;
        this._dbContext.Tenants.Add(
            new TenantRecord
            {
                Id = this._tenantId,
                Slug = "ci-" + this._tenantId.ToString("N"),
                DisplayName = "Code Insights Test Tenant",
                IsActive = true,
                LocalLoginEnabled = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        this._dbContext.Clients.Add(
            new ClientRecord
            {
                Id = this._clientId,
                TenantId = this._tenantId,
                DisplayName = "Code Insights Test Client",
                IsActive = true,
                CreatedAt = now,
            });
        await this._dbContext.SaveChangesAsync();

        this._store = new CodeInsightFindingStore(this._dbContext, CreateCodec());
        this._ingestion = new CodeInsightFindingIngestionService(this._store, OpenGate(), NullLogger<CodeInsightFindingIngestionService>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            await this._dbContext.DisposeAsync();
        }
    }

    [Fact]
    public async Task CollectedFindings_RoundTripAndSurviveReprocessing_OverRealPostgres()
    {
        var key = this.NewKey();

        await this._ingestion.HandleReviewFindingsProducedAsync(this.CreateEvent());
        var firstIds = (await this._store.GetFindingsForPullRequestAsync(key))
            .Select(finding => finding.Id)
            .ToList();

        // The same event again: the natural-key unique constraint is what makes this safe, and the
        // surrogate identifiers downstream consumers already hold must not change.
        await this._ingestion.HandleReviewFindingsProducedAsync(this.CreateEvent());
        var stored = await this._store.GetFindingsForPullRequestAsync(key);

        Assert.Equal(2, firstIds.Count);
        Assert.Equal(firstIds, stored.Select(finding => finding.Id).ToList());
        Assert.Equal("Null dereference", stored[0].Message);

        // Encryption happened on the real text column, not just in the mapper.
        var rawMessages = await this._dbContext.CodeInsightFindings
            .Select(finding => finding.EncryptedMessage)
            .ToListAsync();
        Assert.All(
            rawMessages,
            message => Assert.DoesNotContain("Null dereference", message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeletingTheClient_CascadesAwayItsCollectedInsights_OverRealPostgres()
    {
        await this._ingestion.HandleReviewFindingsProducedAsync(this.CreateEvent());
        Assert.Equal(2, await this._dbContext.CodeInsightFindings.CountAsync());

        await this._dbContext.Clients
            .Where(client => client.Id == this._clientId)
            .ExecuteDeleteAsync();

        // Both levels go: the client cascade removes the aggregate, whose own cascade removes the findings.
        // Scoped to this test's client, because the container is shared with every other test in the assembly.
        Assert.Empty(await this._dbContext.CodeInsightPullRequests.Where(pr => pr.ClientId == this._clientId).ToListAsync());
        Assert.Empty(
            await this._dbContext.CodeInsightFindings
                .Where(finding => finding.CodeInsightPullRequest!.ClientId == this._clientId)
                .ToListAsync());
    }

    [Fact]
    public async Task PurgeExpiredAsync_RemovesElapsedAggregatesAndTheirFindings_OverRealPostgres()
    {
        await this._ingestion.HandleReviewFindingsProducedAsync(this.CreateEvent(observedAt: DateTimeOffset.UtcNow.AddDays(-400)));

        var removed = await this._store.PurgeExpiredAsync(DateTimeOffset.UtcNow.AddDays(-365));

        Assert.Equal(1, removed);
        Assert.Empty(await this._dbContext.CodeInsightPullRequests.Where(pr => pr.ClientId == this._clientId).ToListAsync());
        Assert.Empty(
            await this._dbContext.CodeInsightFindings
                .Where(finding => finding.CodeInsightPullRequest!.ClientId == this._clientId)
                .ToListAsync());
        // The client itself is untouched: the sweep only ever deletes code-insight rows. Asserted on this test's
        // own client rather than on the table's size, which counts whatever else the shared container holds.
        Assert.NotNull(await this._dbContext.Clients.SingleOrDefaultAsync(client => client.Id == this._clientId));
    }

    private CodeInsightPullRequestKey NewKey()
    {
        return new CodeInsightPullRequestKey(this._clientId, "repo-insights", 4321);
    }

    private ReviewFindingsProducedEvent CreateEvent(DateTimeOffset? observedAt = null)
    {
        return new ReviewFindingsProducedEvent(
            this._clientId,
            "repo-insights",
            4321,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "rev-1",
            "Active",
            observedAt ?? DateTimeOffset.UtcNow,
            [
                new ReviewFindingProduced(
                    0,
                    "src/Service.cs",
                    42,
                    CommentSeverity.Error,
                    "Null dereference",
                    "Baseline",
                    null,
                    null,
                    false,
                    ReviewCommentScopeRelation.OnChangedLine,
                    ReviewCommentReadGrounding.Covered,
                    "thread-1",
                    "comment-1"),
                new ReviewFindingProduced(
                    1,
                    null,
                    null,
                    CommentSeverity.Warning,
                    "The change lacks tests",
                    "PrWide",
                    null,
                    null,
                    false,
                    null,
                    null,
                    null,
                    null),
            ]);
    }

    /// <summary>A gate that permits collection; the gate's own behaviour is covered by its unit tests.</summary>
    private static ICodeInsightsCollectionGate OpenGate()
    {
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        return gate;
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterDev.ProPR.CodeInsightRetentionPostgres.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterDev.ProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }
}
