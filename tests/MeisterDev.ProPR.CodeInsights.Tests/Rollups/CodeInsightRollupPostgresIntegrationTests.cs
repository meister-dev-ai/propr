// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Rollups;

[Collection("PostgresIntegration")]
public sealed class CodeInsightRollupPostgresIntegrationTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private MeisterProPRDbContext _dbContext = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        this._dbContext = this.CreateDbContext();
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            try
            {
                await using var cleanup = this.CreateDbContext();
                await cleanup.Clients.Where(client => client.Id == this._clientId).ExecuteDeleteAsync();
                await cleanup.Tenants.Where(tenant => tenant.Id == this._tenantId).ExecuteDeleteAsync();
            }
            finally
            {
                await this._dbContext.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ProjectsASymbolNameLongerThanSixtyFourCharacters()
    {
        var now = DateTimeOffset.UtcNow;
        var pullRequestId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var symbolName = new string('S', 68);

        this._dbContext.Tenants.Add(
            new TenantRecord
            {
                Id = this._tenantId,
                Slug = $"rollup-{this._tenantId:N}",
                DisplayName = "Rollup Test Tenant",
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
                DisplayName = "Rollup Test Client",
                IsActive = true,
                CreatedAt = now,
            });
        this._dbContext.CodeInsightPullRequests.Add(
            new CodeInsightPullRequest
            {
                Id = pullRequestId,
                ClientId = this._clientId,
                RepositoryId = "rollup-repo",
                PullRequestId = 272,
                PullRequestState = "Active",
                LatestRevisionKey = "revision-1",
                LastActivityAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        this._dbContext.CodeInsightFindings.Add(
            new CodeInsightFinding
            {
                Id = Guid.NewGuid(),
                CodeInsightPullRequestId = pullRequestId,
                JobId = jobId,
                RevisionKey = "revision-1",
                Ordinal = 0,
                FilePath = "src/Service.cs",
                LineNumber = 10,
                Severity = CommentSeverity.Warning,
                EncryptedMessage = "test message",
                FindingChainId = Guid.NewGuid(),
                OriginSymbolName = symbolName,
                OriginSymbolKind = "Method",
                ObservedAt = now,
                CreatedAt = now,
            });
        await this._dbContext.SaveChangesAsync();

        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(this._clientId, Arg.Any<CancellationToken>()).Returns(true);
        var projector = new CodeInsightRollupProjector(
            this._dbContext,
            gate,
            NullLogger<CodeInsightRollupProjector>.Instance);

        await projector.ProjectJobAsync(jobId);

        var symbolCount = await this._dbContext.CodeInsightDailyCounts
            .SingleAsync(count => count.JobId == jobId && count.Dimension == CodeInsightCountDimension.Symbol);
        Assert.Equal(symbolName, symbolCount.DimensionKey);
        Assert.Equal(1, symbolCount.Count);
    }

    private MeisterProPRDbContext CreateDbContext()
    {
        return new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
                .Options);
    }
}
