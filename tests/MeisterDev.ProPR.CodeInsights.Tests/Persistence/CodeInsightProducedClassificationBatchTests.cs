// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Data.Common;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Services;
using MeisterDev.ProPR.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using FactAttribute = Xunit.SkippableFactAttribute;
using TheoryAttribute = Xunit.SkippableTheoryAttribute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Persistence;

[Collection("PostgresIntegration")]
public sealed class CodeInsightProducedClassificationBatchTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly Guid _client = Guid.NewGuid();
    private readonly Guid _foreignClient = Guid.NewGuid();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly ClassificationReads _reads = new();
    private MeisterProPRDbContext _db = null!;
    private CodeInsightFindingStore _store = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        this._db = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).AddInterceptors(this._reads).Options);
        var now = DateTimeOffset.UtcNow;
        this._db.AddRange(
            new TenantRecord { Id = this._tenant, Slug = this._tenant.ToString("N"), DisplayName = "Classification batch", CreatedAt = now },
            new ClientRecord { Id = this._client, TenantId = this._tenant, DisplayName = "Collecting client", CreatedAt = now },
            new ClientRecord { Id = this._foreignClient, TenantId = this._tenant, DisplayName = "Other client", CreatedAt = now });
        await this._db.SaveChangesAsync();
        this._store = new CodeInsightFindingStore(this._db, new SecretProtectionCodec(new EphemeralDataProtectionProvider()));
    }

    public async Task DisposeAsync()
    {
        if (this._db is null)
        {
            return;
        }

        await this._db.CodeInsightPullRequests.Where(row => row.ClientId == this._client).ExecuteDeleteAsync();
        await this._db.Clients.Where(row => row.Id == this._client || row.Id == this._foreignClient).ExecuteDeleteAsync();
        await this._db.Tenants.Where(row => row.Id == this._tenant).ExecuteDeleteAsync();
        await this._db.DisposeAsync();
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(8, false)]
    [InlineData(1, true)]
    [InlineData(8, true)]
    public async Task NewFindingsUseOneOwnershipReadAndNoAssignmentReads(int count, bool custom)
    {
        var tag = new FindingCustomTypeAssignment(Guid.NewGuid(), "tenant-boundary", "Tenant boundary");
        if (custom)
        {
            this._db.Add(CustomTag(tag.Id, this._client, tag.Slug));
            await this._db.SaveChangesAsync();
        }

        var classification = Classification(custom ? [tag] : []);
        var findings = Enumerable.Range(0, count).Select(ordinal => Snapshot(ordinal, classification)).ToArray();
        this._reads.Reset();

        Assert.Equal(
            count, await this._store.MaterialiseFindingsAsync(new(this._client, "repo", 1), Guid.NewGuid(), "revision", DateTimeOffset.UtcNow, findings));

        Assert.Equal(0, this._reads.Assignments);
        Assert.Equal(custom ? 1 : 0, this._reads.CustomTags);
        var ids = await this._db.CodeInsightFindings.Where(row => row.CodeInsightPullRequest!.ClientId == this._client)
            .Select(row => row.Id).ToArrayAsync();
        Assert.Equal(count, await this._db.CodeInsightFindingTags.CountAsync(row => ids.Contains(row.CodeInsightFindingId) && row.IsCore));
        Assert.Equal(custom ? count : 0, await this._db.CodeInsightFindingTags.CountAsync(row => ids.Contains(row.CodeInsightFindingId) && !row.IsCore));
    }

    [Fact]
    public async Task MixedBatchPreservesReplayAndClaimGuardsWhileEnrichingLegacyAndNewFindings()
    {
        var own = new FindingCustomTypeAssignment(Guid.NewGuid(), "tenant-boundary", "Captured label");
        var foreign = new FindingCustomTypeAssignment(Guid.NewGuid(), "foreign-tag", "Other client label");
        var retired = CustomTag(own.Id, this._client, own.Slug);
        retired.RetiredAt = DateTimeOffset.UtcNow;
        this._db.AddRange(
            retired,
            CustomTag(foreign.Id, this._foreignClient, foreign.Slug));
        await this._db.SaveChangesAsync();
        var key = new CodeInsightPullRequestKey(this._client, "repo", 1);
        var job = Guid.NewGuid();
        var original = Classification([]);
        await this._store.MaterialiseFindingsAsync(key, job, "revision", DateTimeOffset.UtcNow, [Snapshot(0, null), Snapshot(1, original), Snapshot(2, null)]);
        var existing = await this._db.CodeInsightFindings.Where(row => row.CodeInsightPullRequest!.ClientId == this._client)
            .ToDictionaryAsync(row => row.Ordinal);
        foreach (var ordinal in new[] { 0, 2 })
        {
            await this._store.ApplyClassificationAsync(
                existing[ordinal].Id,
                new(["naming-clarity"], [], CodeInsightFindingLevel.File, CodeInsightFindingQualifier.Missing, 0.1, "finding-type-v1"));
        }

        var captured = Classification([own, foreign, new(Guid.NewGuid(), "missing-tag", "Missing")]);
        this._reads.Reset();

        Assert.Equal(
            1, await this._store.MaterialiseFindingsAsync(
                key, Guid.NewGuid(), "revision", DateTimeOffset.UtcNow,
                [
                    Snapshot(0, captured), Snapshot(1, captured with { CoreSlugs = ["security"] }),
                    Snapshot(2, captured) with { Message = "A different claim." }, Snapshot(3, captured)
                ]));

        Assert.Equal(1, this._reads.Assignments);
        Assert.Equal(1, this._reads.CustomTags);
        var rows = await this._db.CodeInsightFindings.Where(row => row.CodeInsightPullRequest!.ClientId == this._client)
            .OrderBy(row => row.Ordinal).ToListAsync();
        var ids = rows.Select(row => row.Id).ToArray();
        var tags = await this._db.CodeInsightFindingTags.Where(tag => ids.Contains(tag.CodeInsightFindingId)).ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.All(rows.Take(3), row => Assert.Equal(existing[row.Ordinal].Id, row.Id));
        Assert.Equal("naming-clarity", Assert.Single(tags.Where(tag => tag.CodeInsightFindingId == rows[2].Id)).CoreSlug);
        Assert.Equal("logic-error", Assert.Single(tags.Where(tag => tag.CodeInsightFindingId == rows[1].Id)).CoreSlug);
        Assert.Equal(original.Confidence, rows[1].ClassificationConfidence);
        foreach (var row in new[] { rows[0], rows[3] })
        {
            var assigned = tags.Where(tag => tag.CodeInsightFindingId == row.Id).ToArray();
            Assert.Equal("logic-error", Assert.Single(assigned.Where(tag => tag.IsCore)).CoreSlug);
            Assert.Equal(own.Id, Assert.Single(assigned.Where(tag => !tag.IsCore)).CustomTagId);
            Assert.All(assigned, tag => Assert.Equal(FindingTypeClassification.ReviewSource, tag.ClassifierVersion));
            Assert.Equal(captured.Confidence, row.ClassificationConfidence);
        }

        Assert.Equal(job, rows[0].JobId);
        Assert.Equal(Snapshot(2, null).Message, (await this._store.GetFindingsForPullRequestAsync(key)).Single(row => row.Ordinal == 2).Message);
    }

    private static CodeInsightCustomTag CustomTag(Guid id, Guid client, string slug) => new()
    {
        Id = id, ClientId = client, Slug = slug, DisplayName = slug, Definition = "Captured custom type.",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static FindingTypeClassification Classification(IReadOnlyList<FindingCustomTypeAssignment> custom) => new(
        ["logic-error"], custom, CodeInsightFindingLevel.Member, CodeInsightFindingQualifier.Incorrect, 0.93,
        FindingTypeTaxonomy.Version, FindingTypeClassification.ReviewSource);

    private static CodeInsightFindingSnapshot Snapshot(int ordinal, FindingTypeClassification? classification) => new(
        ordinal, $"src/File{ordinal}.cs", 10, CommentSeverity.Error, $"Finding {ordinal} loses the record.",
        "Baseline", null, null, false, null, null, null, null, FindingClassification: classification);

    private sealed class ClassificationReads : DbCommandInterceptor
    {
        public int Assignments { get; private set; }
        public int CustomTags { get; private set; }

        public void Reset() => (this.Assignments, this.CustomTags) = (0, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                if (command.CommandText.Contains("FROM code_insight_finding_tags", StringComparison.OrdinalIgnoreCase))
                {
                    this.Assignments++;
                }

                if (command.CommandText.Contains("FROM code_insight_custom_tags", StringComparison.OrdinalIgnoreCase))
                {
                    this.CustomTags++;
                }
            }

            return ValueTask.FromResult(result);
        }
    }
}
