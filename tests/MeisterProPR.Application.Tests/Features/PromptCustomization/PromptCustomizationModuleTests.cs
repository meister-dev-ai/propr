// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.PromptCustomization.Services;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.PromptCustomization;

/// <summary>
///     Unit tests for <see cref="PromptOverrideService" /> covering the three-level lookup chain
///     and full CRUD surface for the Prompt Customization module.
/// </summary>
public sealed class PromptCustomizationModuleTests
{
    private const string PromptKey = "AgenticLoopGuidance";
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CrawlConfigId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid OverrideId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    [Fact]
    public async Task GetOverrideAsync_WhenCrawlConfigOverrideExists_ReturnsCrawlConfigText()
    {
        var crawlOverride = MakeOverride(
            OverrideId,
            ClientId,
            CrawlConfigId,
            PromptOverrideScope.CrawlConfigScope,
            PromptKey,
            "crawl-config text");
        var clientOverride = MakeOverride(
            Guid.NewGuid(),
            ClientId,
            null,
            PromptOverrideScope.ClientScope,
            PromptKey,
            "client text");

        var repo = Substitute.For<IPromptOverrideRepository>();
        repo.GetByScopeAsync(
                ClientId,
                PromptOverrideScope.CrawlConfigScope,
                CrawlConfigId,
                PromptKey,
                Arg.Any<CancellationToken>())
            .Returns(crawlOverride);
        repo.GetByScopeAsync(ClientId, PromptOverrideScope.ClientScope, null, PromptKey, Arg.Any<CancellationToken>())
            .Returns(clientOverride);

        var sut = new PromptOverrideService(repo);

        var result = await sut.GetOverrideAsync(ClientId, CrawlConfigId, PromptKey);

        Assert.Equal("crawl-config text", result);
    }

    [Fact]
    public async Task GetOverrideAsync_WhenNoCrawlConfigOverride_ReturnsClientText()
    {
        var clientOverride = MakeOverride(
            Guid.NewGuid(),
            ClientId,
            null,
            PromptOverrideScope.ClientScope,
            PromptKey,
            "client text");

        var repo = Substitute.For<IPromptOverrideRepository>();
        repo.GetByScopeAsync(
                ClientId,
                PromptOverrideScope.CrawlConfigScope,
                CrawlConfigId,
                PromptKey,
                Arg.Any<CancellationToken>())
            .Returns((PromptOverride?)null);
        repo.GetByScopeAsync(ClientId, PromptOverrideScope.ClientScope, null, PromptKey, Arg.Any<CancellationToken>())
            .Returns(clientOverride);

        var sut = new PromptOverrideService(repo);

        var result = await sut.GetOverrideAsync(ClientId, CrawlConfigId, PromptKey);

        Assert.Equal("client text", result);
    }

    [Fact]
    public async Task GetOverrideAsync_WhenNoOverrideExists_ReturnsNull()
    {
        var repo = Substitute.For<IPromptOverrideRepository>();
        repo.GetByScopeAsync(
                Arg.Any<Guid>(),
                Arg.Any<PromptOverrideScope>(),
                Arg.Any<Guid?>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns((PromptOverride?)null);

        var sut = new PromptOverrideService(repo);

        var result = await sut.GetOverrideAsync(ClientId, CrawlConfigId, PromptKey);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateAsync_ReturnsPopulatedDto()
    {
        var repo = Substitute.For<IPromptOverrideRepository>();

        var sut = new PromptOverrideService(repo);

        var dto = await sut.CreateAsync(ClientId, PromptOverrideScope.ClientScope, null, PromptKey, "my override text");

        Assert.NotEqual(Guid.Empty, dto.Id);
        Assert.Equal(ClientId, dto.ClientId);
        Assert.Equal(PromptOverrideScope.ClientScope, dto.Scope);
        Assert.Null(dto.CrawlConfigId);
        Assert.Equal(PromptKey, dto.PromptKey);
        Assert.Equal("my override text", dto.OverrideText);
        Assert.True(dto.CreatedAt > DateTimeOffset.UtcNow.AddSeconds(-5));
        await repo.Received(1).AddAsync(Arg.Any<PromptOverride>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_CallsRepositoryDeleteAsync()
    {
        var existing = MakeOverride(OverrideId, ClientId, null, PromptOverrideScope.ClientScope, PromptKey, "text");
        var repo = Substitute.For<IPromptOverrideRepository>();
        repo.GetByIdAsync(OverrideId, Arg.Any<CancellationToken>()).Returns(existing);
        repo.DeleteAsync(OverrideId, Arg.Any<CancellationToken>()).Returns(true);

        var sut = new PromptOverrideService(repo);

        var deleted = await sut.DeleteAsync(ClientId, OverrideId);

        Assert.True(deleted);
        await repo.Received(1).DeleteAsync(OverrideId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_UpdatesOverrideTextAndUpdatedAt()
    {
        var before = DateTimeOffset.UtcNow.AddMinutes(-10);
        var existing = MakeOverrideWithTimestamps(
            OverrideId,
            ClientId,
            null,
            PromptOverrideScope.ClientScope,
            PromptKey,
            "old text",
            before,
            before);
        var repo = Substitute.For<IPromptOverrideRepository>();
        repo.GetByIdAsync(OverrideId, Arg.Any<CancellationToken>()).Returns(existing);

        var sut = new PromptOverrideService(repo);

        var result = await sut.UpdateAsync(ClientId, OverrideId, "new text");

        Assert.NotNull(result);
        Assert.Equal("new text", result!.OverrideText);
        Assert.True(result.UpdatedAt > before);
        await repo.Received(1).UpdateAsync(existing, Arg.Any<CancellationToken>());
    }

    private static PromptOverride MakeOverride(
        Guid id,
        Guid clientId,
        Guid? crawlConfigId,
        PromptOverrideScope scope,
        string promptKey,
        string overrideText)
    {
        return new PromptOverride(id, clientId, crawlConfigId, scope, promptKey, overrideText);
    }

    private static PromptOverride MakeOverrideWithTimestamps(
        Guid id,
        Guid clientId,
        Guid? crawlConfigId,
        PromptOverrideScope scope,
        string promptKey,
        string overrideText,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        _ = createdAt;
        _ = updatedAt;
        return new PromptOverride(id, clientId, crawlConfigId, scope, promptKey, overrideText);
    }
}

/// <summary>
///     Unit tests for <see cref="PromptOverride" /> domain invariants.
/// </summary>
public sealed class PromptOverrideDomainInvariantTests
{
    private static readonly Guid ValidId = Guid.NewGuid();
    private static readonly Guid ValidClientId = Guid.NewGuid();
    private static readonly Guid ValidCrawlConfigId = Guid.NewGuid();

    [Fact]
    public void Constructor_WithEmptyClientId_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new PromptOverride(
                ValidId,
                Guid.Empty,
                null,
                PromptOverrideScope.ClientScope,
                "AgenticLoopGuidance",
                "text"));

        Assert.Equal("clientId", ex.ParamName);
    }

    [Fact]
    public void Constructor_CrawlConfigScopeWithNullCrawlConfigId_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new PromptOverride(
                ValidId,
                ValidClientId,
                null,
                PromptOverrideScope.CrawlConfigScope,
                "AgenticLoopGuidance",
                "text"));

        Assert.Equal("crawlConfigId", ex.ParamName);
    }

    [Fact]
    public void Constructor_ClientScopeWithNonNullCrawlConfigId_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new PromptOverride(
                ValidId,
                ValidClientId,
                ValidCrawlConfigId,
                PromptOverrideScope.ClientScope,
                "AgenticLoopGuidance",
                "text"));

        Assert.Equal("crawlConfigId", ex.ParamName);
    }

    [Fact]
    public void Constructor_WithUnrecognisedPromptKey_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new PromptOverride(ValidId, ValidClientId, null, PromptOverrideScope.ClientScope, "UnknownKey", "text"));

        Assert.Equal("promptKey", ex.ParamName);
    }

    [Fact]
    public void Constructor_WithBlankPromptKey_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new PromptOverride(ValidId, ValidClientId, null, PromptOverrideScope.ClientScope, "   ", "text"));

        Assert.Equal("promptKey", ex.ParamName);
    }

    [Theory]
    [InlineData("SystemPrompt")]
    [InlineData("AgenticLoopGuidance")]
    [InlineData("SynthesisSystemPrompt")]
    [InlineData("QualityFilterSystemPrompt")]
    [InlineData("PerFileContextPrompt")]
    public void Constructor_WithValidPromptKey_DoesNotThrow(string key)
    {
        var entity = new PromptOverride(ValidId, ValidClientId, null, PromptOverrideScope.ClientScope, key, "text");
        Assert.Equal(key, entity.PromptKey);
    }
}
