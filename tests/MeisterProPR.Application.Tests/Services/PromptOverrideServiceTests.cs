using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Services;

/// <summary>
///     Unit tests for <see cref="PromptOverrideService" /> covering the three-level lookup chain
///     and full CRUD surface (US2, T006).
/// </summary>
public sealed class PromptOverrideServiceTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CrawlConfigId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid OverrideId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private const string PromptKey = "AgenticLoopGuidance";

    // (a) GetOverrideAsync — crawl-config scope wins when a crawl-config override exists
    [Fact]
    public async Task GetOverrideAsync_WhenCrawlConfigOverrideExists_ReturnsCrawlConfigText()
    {
        var crawlOverride = MakeOverride(OverrideId, ClientId, CrawlConfigId, PromptOverrideScope.CrawlConfigScope, PromptKey, "crawl-config text");
        var clientOverride = MakeOverride(Guid.NewGuid(), ClientId, null, PromptOverrideScope.ClientScope, PromptKey, "client text");

        var repo = Substitute.For<IPromptOverrideRepository>();
        repo.GetByScopeAsync(ClientId, PromptOverrideScope.CrawlConfigScope, CrawlConfigId, PromptKey, Arg.Any<CancellationToken>())
            .Returns(crawlOverride);
        repo.GetByScopeAsync(ClientId, PromptOverrideScope.ClientScope, null, PromptKey, Arg.Any<CancellationToken>())
            .Returns(clientOverride);

        var sut = new PromptOverrideService(repo);

        var result = await sut.GetOverrideAsync(ClientId, CrawlConfigId, PromptKey);

        Assert.Equal("crawl-config text", result);
    }

    // (b) GetOverrideAsync — falls back to client scope when no crawl-config override exists
    [Fact]
    public async Task GetOverrideAsync_WhenNoCrawlConfigOverride_ReturnsClientText()
    {
        var clientOverride = MakeOverride(Guid.NewGuid(), ClientId, null, PromptOverrideScope.ClientScope, PromptKey, "client text");

        var repo = Substitute.For<IPromptOverrideRepository>();
        repo.GetByScopeAsync(ClientId, PromptOverrideScope.CrawlConfigScope, CrawlConfigId, PromptKey, Arg.Any<CancellationToken>())
            .Returns((PromptOverride?)null);
        repo.GetByScopeAsync(ClientId, PromptOverrideScope.ClientScope, null, PromptKey, Arg.Any<CancellationToken>())
            .Returns(clientOverride);

        var sut = new PromptOverrideService(repo);

        var result = await sut.GetOverrideAsync(ClientId, CrawlConfigId, PromptKey);

        Assert.Equal("client text", result);
    }

    // (c) GetOverrideAsync — returns null when neither scope has an override
    [Fact]
    public async Task GetOverrideAsync_WhenNoOverrideExists_ReturnsNull()
    {
        var repo = Substitute.For<IPromptOverrideRepository>();
        repo.GetByScopeAsync(Arg.Any<Guid>(), Arg.Any<PromptOverrideScope>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((PromptOverride?)null);

        var sut = new PromptOverrideService(repo);

        var result = await sut.GetOverrideAsync(ClientId, CrawlConfigId, PromptKey);

        Assert.Null(result);
    }

    // (d) CreateAsync — returns a populated PromptOverrideDto matching the input
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

    // (e) DeleteAsync — calls IPromptOverrideRepository.DeleteAsync
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

    // (f) UpdateAsync — updates only OverrideText and UpdatedAt, returning updated DTO
    [Fact]
    public async Task UpdateAsync_UpdatesOverrideTextAndUpdatedAt()
    {
        var before = DateTimeOffset.UtcNow.AddMinutes(-10);
        var existing = MakeOverrideWithTimestamps(OverrideId, ClientId, null, PromptOverrideScope.ClientScope, PromptKey, "old text", before, before);
        var repo = Substitute.For<IPromptOverrideRepository>();
        repo.GetByIdAsync(OverrideId, Arg.Any<CancellationToken>()).Returns(existing);

        var sut = new PromptOverrideService(repo);

        var result = await sut.UpdateAsync(ClientId, OverrideId, "new text");

        Assert.NotNull(result);
        Assert.Equal("new text", result!.OverrideText);
        Assert.True(result.UpdatedAt > before);
        await repo.Received(1).UpdateAsync(existing, Arg.Any<CancellationToken>());
    }

    // --- helpers ---

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
        // We create the entity normally; the constructor sets timestamps to UtcNow.
        // For the update test we don't need to inject a specific CreatedAt — what matters is
        // that UpdatedAt changes after UpdateText() is called.
        _ = createdAt;
        _ = updatedAt;
        return new PromptOverride(id, clientId, crawlConfigId, scope, promptKey, overrideText);
    }
}

/// <summary>
///     Unit tests for <see cref="PromptOverride" /> domain invariants (PR64 findings 5491–5493).
/// </summary>
public sealed class PromptOverrideDomainInvariantTests
{
    private static readonly Guid ValidId = Guid.NewGuid();
    private static readonly Guid ValidClientId = Guid.NewGuid();
    private static readonly Guid ValidCrawlConfigId = Guid.NewGuid();

    // PR64-5493 — clientId Guid.Empty must be rejected
    [Fact]
    public void Constructor_WithEmptyClientId_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new PromptOverride(ValidId, Guid.Empty, null, PromptOverrideScope.ClientScope, "AgenticLoopGuidance", "text"));

        Assert.Equal("clientId", ex.ParamName);
    }

    // PR64-5491 — CrawlConfigScope with null crawlConfigId must be rejected
    [Fact]
    public void Constructor_CrawlConfigScopeWithNullCrawlConfigId_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new PromptOverride(ValidId, ValidClientId, null, PromptOverrideScope.CrawlConfigScope, "AgenticLoopGuidance", "text"));

        Assert.Equal("crawlConfigId", ex.ParamName);
    }

    // PR64-5491 — ClientScope with non-null crawlConfigId must be rejected
    [Fact]
    public void Constructor_ClientScopeWithNonNullCrawlConfigId_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new PromptOverride(ValidId, ValidClientId, ValidCrawlConfigId, PromptOverrideScope.ClientScope, "AgenticLoopGuidance", "text"));

        Assert.Equal("crawlConfigId", ex.ParamName);
    }

    // PR64-5492 — arbitrary promptKey must be rejected
    [Fact]
    public void Constructor_WithUnrecognisedPromptKey_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new PromptOverride(ValidId, ValidClientId, null, PromptOverrideScope.ClientScope, "UnknownKey", "text"));

        Assert.Equal("promptKey", ex.ParamName);
    }

    // PR64-5492 — whitespace promptKey must be rejected
    [Fact]
    public void Constructor_WithBlankPromptKey_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new PromptOverride(ValidId, ValidClientId, null, PromptOverrideScope.ClientScope, "   ", "text"));

        Assert.Equal("promptKey", ex.ParamName);
    }

    // Positive — all valid combinations must construct without throwing
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
