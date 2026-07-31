// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Persistence;

namespace MeisterDev.ProPR.CodeInsights.Tests.Persistence;

/// <summary>
///     The read a review view uses to line tags up against the findings it already renders. The ordinal is the
///     join, so the ordering and the status semantics are what these tests pin.
/// </summary>
public sealed class CodeInsightClassificationReadTests : IDisposable
{
    private const int MaxAttempts = 3;
    private static readonly Guid JobId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly MeisterProPRDbContext _dbContext;
    private readonly CodeInsightFindingStore _store;

    public CodeInsightClassificationReadTests()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"CodeInsightClassificationReadTests-{Guid.NewGuid():N}")
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        this._store = new CodeInsightFindingStore(this._dbContext, CreateCodec());
    }

    public void Dispose()
    {
        this._dbContext.Dispose();
    }

    [Fact]
    public async Task ClassificationsComeBackInOrdinalOrder()
    {
        // The caller joins on ordinal, and it renders findings in the persisted order. Returning them out of
        // order would still join correctly, but it makes every consumer sort defensively for no reason.
        var key = await this.SeedAsync(findingCount: 3);
        await this.ClassifyAsync(key, ordinal: 2, "logic-error");
        await this.ClassifyAsync(key, ordinal: 0, "security");

        var views = await this._store.GetClassificationsForJobAsync(JobId, MaxAttempts);

        Assert.Equal([0, 1, 2], views.Select(view => view.Ordinal).ToList());
    }

    [Fact]
    public async Task AClassifiedFindingCarriesItsTagsLevelQualifierAndConfidence()
    {
        var key = await this.SeedAsync(findingCount: 1);
        await this.ClassifyAsync(key, ordinal: 0, "data-validation", "security");

        var view = Assert.Single(await this._store.GetClassificationsForJobAsync(JobId, MaxAttempts));

        Assert.Equal(CodeInsightClassificationStatus.Classified, view.Status);
        Assert.Equal(["data-validation", "security"], view.CoreTags);
        Assert.Equal(CodeInsightFindingLevel.Member, view.Level);
        Assert.Equal(CodeInsightFindingQualifier.Missing, view.Qualifier);
        Assert.Equal(0.8, view.Confidence);
    }

    [Fact]
    public async Task AnUnclassifiedFindingWithAttemptsLeftReadsAsPending()
    {
        // A freshly finished review legitimately has no tags for a cycle or two. "Not yet" must not look the
        // same as "nothing to say", or the first thing anyone sees reads as broken.
        await this.SeedAsync(findingCount: 1);

        var view = Assert.Single(await this._store.GetClassificationsForJobAsync(JobId, MaxAttempts));

        Assert.Equal(CodeInsightClassificationStatus.Pending, view.Status);
        Assert.Empty(view.CoreTags);
        Assert.Null(view.Level);
    }

    [Fact]
    public async Task AFindingThatExhaustedItsAttemptsReadsAsUnclassifiable()
    {
        var key = await this.SeedAsync(findingCount: 1);
        var finding = (await this._store.GetFindingsForPullRequestAsync(key)).Single();
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            await this._store.RecordClassificationAttemptAsync(finding.Id);
        }

        var view = Assert.Single(await this._store.GetClassificationsForJobAsync(JobId, MaxAttempts));

        Assert.Equal(CodeInsightClassificationStatus.Unclassifiable, view.Status);
    }

    [Fact]
    public async Task ARetiredCustomTagStillResolvesToItsSlug()
    {
        // Retirement is a timestamp precisely so a historical assignment keeps a name.
        var key = await this.SeedAsync(findingCount: 1);
        var customTag = new CodeInsightCustomTag
        {
            Id = Guid.CreateVersion7(),
            ClientId = key.ClientId,
            Slug = "domain-rule",
            DisplayName = "Domain rule",
            Definition = "Violates a business rule.",
            RetiredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        this._dbContext.CodeInsightCustomTags.Add(customTag);
        await this._dbContext.SaveChangesAsync();

        var finding = (await this._store.GetFindingsForPullRequestAsync(key)).Single();
        await this._store.ApplyClassificationAsync(
            finding.Id,
            new CodeInsightClassification(
                ["logic-error"],
                [customTag.Id],
                CodeInsightFindingLevel.Member,
                CodeInsightFindingQualifier.Missing,
                0.8,
                "test"));

        var view = Assert.Single(await this._store.GetClassificationsForJobAsync(JobId, MaxAttempts));

        Assert.Equal(["logic-error"], view.CoreTags);
        Assert.Equal(["domain-rule"], view.CustomTags);
    }

    [Fact]
    public async Task AJobWithNothingCollectedReadsAsEmpty()
    {
        // The ordinary case for a client that never opted in. Not an error: the view renders no badges.
        Assert.Empty(await this._store.GetClassificationsForJobAsync(Guid.NewGuid(), MaxAttempts));
    }

    [Fact]
    public async Task AnotherJobsFindingsAreNotReturned()
    {
        var key = await this.SeedAsync(findingCount: 1);
        await this._store.MaterialiseFindingsAsync(
            key,
            Guid.NewGuid(),
            "rev-other",
            DateTimeOffset.UtcNow,
            [Snapshot(0)]);

        Assert.Single(await this._store.GetClassificationsForJobAsync(JobId, MaxAttempts));
    }

    private async Task<CodeInsightPullRequestKey> SeedAsync(int findingCount)
    {
        var key = new CodeInsightPullRequestKey(Guid.NewGuid(), "repo-1", 100);
        await this._store.MaterialiseFindingsAsync(
            key,
            JobId,
            "rev-1",
            DateTimeOffset.UtcNow,
            Enumerable.Range(0, findingCount).Select(Snapshot).ToList());
        return key;
    }

    private async Task ClassifyAsync(CodeInsightPullRequestKey key, int ordinal, params string[] coreSlugs)
    {
        var finding = (await this._store.GetFindingsForPullRequestAsync(key))
            .Single(candidate => candidate.Ordinal == ordinal);
        await this._store.ApplyClassificationAsync(
            finding.Id,
            new CodeInsightClassification(
                coreSlugs,
                [],
                CodeInsightFindingLevel.Member,
                CodeInsightFindingQualifier.Missing,
                0.8,
                "test"));
    }

    private static CodeInsightFindingSnapshot Snapshot(int ordinal)
    {
        return new CodeInsightFindingSnapshot(
            ordinal,
            "src/Service.cs",
            40 + ordinal,
            CommentSeverity.Error,
            $"Finding {ordinal}",
            "Baseline",
            null,
            null,
            false,
            ReviewCommentScopeRelation.OnChangedLine,
            null,
            $"thread-{ordinal}",
            $"comment-{ordinal}");
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterDev.ProPR.CodeInsightClassificationRead.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterDev.ProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }
}
