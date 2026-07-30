// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.CodeInsights.Taxonomy;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.CodeInsights;

public sealed class CodeInsightTaxonomyServiceTests : IDisposable
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherClientId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private readonly MeisterProPRDbContext _dbContext;
    private readonly CodeInsightTaxonomyService _service;

    public CodeInsightTaxonomyServiceTests()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"CodeInsightTaxonomyServiceTests-{Guid.NewGuid():N}")
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        this._service = new CodeInsightTaxonomyService(this._dbContext);
    }

    public void Dispose()
    {
        this._dbContext.Dispose();
    }

    [Fact]
    public async Task GetTaxonomyAsync_ReturnsTheFixedCoreSetForAClientWithNoCustomTags()
    {
        var taxonomy = await this._service.GetTaxonomyAsync(ClientId);

        Assert.Equal(CodeInsightCoreTaxonomy.Version, taxonomy.Version);
        Assert.Equal(CodeInsightCoreTaxonomy.All.Count, taxonomy.CoreTags.Count);
        Assert.Empty(taxonomy.CustomTags);
        Assert.Contains(taxonomy.CoreTags, tag => tag.Slug == CodeInsightCoreTaxonomy.Security);
    }

    [Fact]
    public async Task CreateCustomTagAsync_CreatesAnActiveTagAndNormalisesTheSlug()
    {
        var result = await this._service.CreateCustomTagAsync(
            ClientId,
            new CodeInsightCustomTagWriteRequest("  Domain-Rule  ", " Domain rule ", " Violates a business rule of ours. "));

        Assert.True(result.Succeeded);
        Assert.Equal("domain-rule", result.Tag!.Slug);
        Assert.Equal("Domain rule", result.Tag.DisplayName);
        Assert.Equal("Violates a business rule of ours.", result.Tag.Definition);
        Assert.Null(result.Tag.RetiredAt);
    }

    [Theory]
    [InlineData("security")]
    [InlineData("SECURITY")]
    [InlineData("  Security ")]
    public async Task CreateCustomTagAsync_RejectsASlugThatShadowsACoreTypeRegardlessOfCase(string slug)
    {
        var result = await this._service.CreateCustomTagAsync(
            ClientId,
            new CodeInsightCustomTagWriteRequest(slug, "Shadow", "Tries to shadow a core type."));

        Assert.Equal(CodeInsightCustomTagWriteError.ShadowsCoreTag, result.Error);
        Assert.Empty(await this._dbContext.CodeInsightCustomTags.ToListAsync());
    }

    [Theory]
    [InlineData("Not Kebab")]
    [InlineData("trailing-")]
    [InlineData("double--dash")]
    [InlineData("")]
    public async Task CreateCustomTagAsync_RejectsAMalformedSlug(string slug)
    {
        var result = await this._service.CreateCustomTagAsync(
            ClientId,
            new CodeInsightCustomTagWriteRequest(slug, "Name", "A definition long enough to pass."));

        Assert.Equal(CodeInsightCustomTagWriteError.Invalid, result.Error);
        Assert.Empty(await this._dbContext.CodeInsightCustomTags.ToListAsync());
    }

    [Fact]
    public async Task CreateCustomTagAsync_RequiresADefinitionBecauseTheClassifierUsesIt()
    {
        var result = await this._service.CreateCustomTagAsync(
            ClientId,
            new CodeInsightCustomTagWriteRequest("domain-rule", "Domain rule", "   "));

        Assert.Equal(CodeInsightCustomTagWriteError.Invalid, result.Error);
    }

    [Fact]
    public async Task CreateCustomTagAsync_RejectsASlugTheClientAlreadyUses()
    {
        await this._service.CreateCustomTagAsync(
            ClientId,
            new CodeInsightCustomTagWriteRequest("domain-rule", "Domain rule", "Violates a business rule."));

        var duplicate = await this._service.CreateCustomTagAsync(
            ClientId,
            new CodeInsightCustomTagWriteRequest("domain-rule", "Another name", "A different definition."));

        Assert.Equal(CodeInsightCustomTagWriteError.SlugAlreadyUsed, duplicate.Error);
        Assert.Single(await this._dbContext.CodeInsightCustomTags.ToListAsync());
    }

    [Fact]
    public async Task CreateCustomTagAsync_AllowsTwoClientsTheSameSlug()
    {
        // Custom vocabulary is per-client, which is exactly why it never rolls up across clients.
        var first = await this._service.CreateCustomTagAsync(
            ClientId,
            new CodeInsightCustomTagWriteRequest("domain-rule", "Domain rule", "Violates our business rule."));
        var second = await this._service.CreateCustomTagAsync(
            OtherClientId,
            new CodeInsightCustomTagWriteRequest("domain-rule", "Domain rule", "Violates their business rule."));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotEqual(first.Tag!.Id, second.Tag!.Id);
    }

    [Fact]
    public async Task UpdateCustomTagAsync_RenamesWithoutOrphaningOrRelabellingAssignments()
    {
        var created = await this._service.CreateCustomTagAsync(
            ClientId,
            new CodeInsightCustomTagWriteRequest("domain-rule", "Domain rule", "Violates a business rule."));
        var assignment = await this.SeedAssignmentAsync(created.Tag!.Id);

        var updated = await this._service.UpdateCustomTagAsync(
            ClientId,
            created.Tag.Id,
            new CodeInsightCustomTagWriteRequest("house-convention", "House convention", "Breaks one of our conventions."));

        Assert.True(updated.Succeeded);
        Assert.Equal("house-convention", updated.Tag!.Slug);
        Assert.Equal(created.Tag.Id, updated.Tag.Id);

        // The assignment row is untouched: it references the tag's identity, never its name.
        var storedAssignment = await this._dbContext.CodeInsightFindingTags.SingleAsync();
        Assert.Equal(assignment.Id, storedAssignment.Id);
        Assert.Equal(created.Tag.Id, storedAssignment.CustomTagId);
        Assert.False(storedAssignment.IsCore);
    }

    [Fact]
    public async Task UpdateCustomTagAsync_RejectsARenameOntoACoreTypeOrAnExistingSlug()
    {
        var first = await this._service.CreateCustomTagAsync(
            ClientId,
            new CodeInsightCustomTagWriteRequest("domain-rule", "Domain rule", "Violates a business rule."));
        var second = await this._service.CreateCustomTagAsync(
            ClientId,
            new CodeInsightCustomTagWriteRequest("house-convention", "House convention", "Breaks a convention."));

        var ontoCore = await this._service.UpdateCustomTagAsync(
            ClientId,
            first.Tag!.Id,
            new CodeInsightCustomTagWriteRequest("performance", "Performance", "Tries to shadow a core type."));
        var ontoSibling = await this._service.UpdateCustomTagAsync(
            ClientId,
            first.Tag.Id,
            new CodeInsightCustomTagWriteRequest("house-convention", "Clash", "Collides with a sibling tag."));

        Assert.Equal(CodeInsightCustomTagWriteError.ShadowsCoreTag, ontoCore.Error);
        Assert.Equal(CodeInsightCustomTagWriteError.SlugAlreadyUsed, ontoSibling.Error);
        Assert.Equal("house-convention", second.Tag!.Slug);
        Assert.Equal("domain-rule", (await this._dbContext.CodeInsightCustomTags.FindAsync(first.Tag.Id))!.Slug);
    }

    [Fact]
    public async Task UpdateCustomTagAsync_KeepingItsOwnSlugIsNotACollision()
    {
        var created = await this._service.CreateCustomTagAsync(
            ClientId,
            new CodeInsightCustomTagWriteRequest("domain-rule", "Domain rule", "Violates a business rule."));

        var updated = await this._service.UpdateCustomTagAsync(
            ClientId,
            created.Tag!.Id,
            new CodeInsightCustomTagWriteRequest("domain-rule", "Domain rule (revised)", "A clearer definition."));

        Assert.True(updated.Succeeded);
        Assert.Equal("Domain rule (revised)", updated.Tag!.DisplayName);
    }

    [Fact]
    public async Task RetireCustomTagAsync_StopsOfferingTheTagButKeepsItResolvable()
    {
        var created = await this._service.CreateCustomTagAsync(
            ClientId,
            new CodeInsightCustomTagWriteRequest("domain-rule", "Domain rule", "Violates a business rule."));
        await this.SeedAssignmentAsync(created.Tag!.Id);

        var retired = await this._service.RetireCustomTagAsync(ClientId, created.Tag.Id);

        Assert.True(retired.Succeeded);
        Assert.NotNull(retired.Tag!.RetiredAt);

        // Assignable vocabulary no longer offers it; the full vocabulary still resolves it, so a finding that
        // already carries it keeps a name.
        Assert.Empty((await this._service.GetAssignableTaxonomyAsync(ClientId)).CustomTags);
        Assert.Single((await this._service.GetTaxonomyAsync(ClientId)).CustomTags);
        Assert.Single(await this._dbContext.CodeInsightFindingTags.ToListAsync());
    }

    [Fact]
    public async Task RetireCustomTagAsync_IsIdempotentAndKeepsTheOriginalTimestamp()
    {
        var created = await this._service.CreateCustomTagAsync(
            ClientId,
            new CodeInsightCustomTagWriteRequest("domain-rule", "Domain rule", "Violates a business rule."));

        var first = await this._service.RetireCustomTagAsync(ClientId, created.Tag!.Id);
        var second = await this._service.RetireCustomTagAsync(ClientId, created.Tag.Id);

        Assert.True(second.Succeeded);
        Assert.Equal(first.Tag!.RetiredAt, second.Tag!.RetiredAt);
    }

    [Fact]
    public async Task ARetiredSlug_StaysTakenSoOneLabelNeverMeansTwoThings()
    {
        var created = await this._service.CreateCustomTagAsync(
            ClientId,
            new CodeInsightCustomTagWriteRequest("domain-rule", "Domain rule", "Violates a business rule."));
        await this._service.RetireCustomTagAsync(ClientId, created.Tag!.Id);

        var reused = await this._service.CreateCustomTagAsync(
            ClientId,
            new CodeInsightCustomTagWriteRequest("domain-rule", "Something else entirely", "A different meaning."));

        Assert.Equal(CodeInsightCustomTagWriteError.SlugAlreadyUsed, reused.Error);
    }

    [Fact]
    public async Task AnotherClientsTagCannotBeReadUpdatedOrRetired()
    {
        var created = await this._service.CreateCustomTagAsync(
            OtherClientId,
            new CodeInsightCustomTagWriteRequest("domain-rule", "Domain rule", "Violates a business rule."));

        var update = await this._service.UpdateCustomTagAsync(
            ClientId,
            created.Tag!.Id,
            new CodeInsightCustomTagWriteRequest("hijacked", "Hijacked", "Reaches across clients."));
        var retire = await this._service.RetireCustomTagAsync(ClientId, created.Tag.Id);

        Assert.Equal(CodeInsightCustomTagWriteError.NotFound, update.Error);
        Assert.Equal(CodeInsightCustomTagWriteError.NotFound, retire.Error);
        Assert.Empty((await this._service.GetTaxonomyAsync(ClientId)).CustomTags);
    }

    private async Task<CodeInsightFindingTag> SeedAssignmentAsync(Guid customTagId)
    {
        var pullRequest = new CodeInsightPullRequest
        {
            Id = Guid.CreateVersion7(),
            ClientId = ClientId,
            RepositoryId = "repo-1",
            PullRequestId = 1,
            PullRequestState = "Active",
            LastActivityAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var finding = new CodeInsightFinding
        {
            Id = Guid.CreateVersion7(),
            CodeInsightPullRequestId = pullRequest.Id,
            JobId = Guid.NewGuid(),
            RevisionKey = "rev-1",
            Ordinal = 0,
            EncryptedMessage = "protected",
            ObservedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var assignment = new CodeInsightFindingTag
        {
            Id = Guid.CreateVersion7(),
            CodeInsightFindingId = finding.Id,
            IsCore = false,
            CustomTagId = customTagId,
            TaxonomyVersion = CodeInsightCoreTaxonomy.Version,
            ClassifierVersion = "test",
            AssignedAt = DateTimeOffset.UtcNow,
        };

        this._dbContext.CodeInsightPullRequests.Add(pullRequest);
        this._dbContext.CodeInsightFindings.Add(finding);
        this._dbContext.CodeInsightFindingTags.Add(assignment);
        await this._dbContext.SaveChangesAsync();

        return assignment;
    }
}
