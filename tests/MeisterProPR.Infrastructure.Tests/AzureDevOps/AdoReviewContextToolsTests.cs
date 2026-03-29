using Azure.Core;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AzureDevOps;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

/// <summary>
///     Unit tests for <see cref="AdoReviewContextTools" /> using a testable subclass
///     that bypasses the ADO network layer while exercising the caching and line-slicing logic.
/// </summary>
public class AdoReviewContextToolsTests
{
    private static IOptions<AiReviewOptions> DefaultOptions(int maxFileSizeBytes = 1_048_576)
    {
        return Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileSizeBytes = maxFileSizeBytes });
    }

    [Fact]
    public async Task GetFileContentAsync_CacheHit_DoesNotFetchTwice()
    {
        // Arrange
        var sut = new TestableAdoReviewContextTools(DefaultOptions());
        sut.SetFile("/src/Foo.cs", "line1\nline2\nline3");

        // Act — call twice with the same path and branch
        await sut.GetFileContentAsync("/src/Foo.cs", "main", 1, 3, CancellationToken.None);
        await sut.GetFileContentAsync("/src/Foo.cs", "main", 1, 2, CancellationToken.None);

        // Assert — underlying fetch must only have been called once
        Assert.Equal(1, sut.FetchCallCount);
    }

    [Fact]
    public async Task GetFileContentAsync_LineRangeSlicing_ReturnsCorrectLines()
    {
        // Arrange
        var sut = new TestableAdoReviewContextTools(DefaultOptions());
        sut.SetFile("/src/Foo.cs", "alpha\nbeta\ngamma\ndelta\nepsilon");

        // Act — request lines 2–4 (1-based, inclusive)
        var result = await sut.GetFileContentAsync("/src/Foo.cs", "main", 2, 4, CancellationToken.None);

        // Assert
        Assert.Equal("beta\ngamma\ndelta", result);
    }

    [Fact]
    public async Task GetFileContentAsync_MissingFile_ReturnsEmptyString()
    {
        // Arrange
        var sut = new TestableAdoReviewContextTools(DefaultOptions());
        // File not registered → returns null from FetchRawFileContentAsync

        // Act
        var result = await sut.GetFileContentAsync("/src/Missing.cs", "main", 1, 10, CancellationToken.None);

        // Assert — empty string, no exception
        Assert.Equal("", result);
    }

    [Fact]
    public async Task GetFileContentAsync_FileExceedsMaxSize_ReturnsErrorString()
    {
        // Arrange — set a very small max size so any content exceeds it
        var sut = new TestableAdoReviewContextTools(DefaultOptions(maxFileSizeBytes: 5));
        sut.SetFile("/src/Big.cs", "this content is longer than 5 bytes");

        // Act
        var result = await sut.GetFileContentAsync("/src/Big.cs", "main", 1, 100, CancellationToken.None);

        // Assert — error message, file not cached (fetch count stays at 1)
        Assert.StartsWith("[File too large:", result);
        Assert.Contains("bytes exceeds limit of 5 bytes", result);
    }

    [Fact]
    public async Task GetFileContentAsync_FileExceedsMaxSize_DoesNotCacheContent()
    {
        // Arrange
        var sut = new TestableAdoReviewContextTools(DefaultOptions(maxFileSizeBytes: 5));
        sut.SetFile("/src/Big.cs", "this content is longer than 5 bytes");

        // First call — exceeds limit, returns error string
        await sut.GetFileContentAsync("/src/Big.cs", "main", 1, 100, CancellationToken.None);
        // Second call — must fetch again (not cached)
        await sut.GetFileContentAsync("/src/Big.cs", "main", 1, 100, CancellationToken.None);

        // The file was fetched twice — it was NOT cached when size limit was exceeded
        Assert.Equal(2, sut.FetchCallCount);
    }

    [Fact]
    public async Task GetFileContentAsync_StartLineBeyondEof_ReturnsEmptyString()
    {
        // Arrange
        var sut = new TestableAdoReviewContextTools(DefaultOptions());
        sut.SetFile("/src/Small.cs", "only\none line");

        // Act — request starting beyond end of file
        var result = await sut.GetFileContentAsync("/src/Small.cs", "main", 999, 1000, CancellationToken.None);

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public async Task GetFileContentAsync_DifferentAiSuppliedBranches_OneFetchBecauseStoredBranchEnforced()
    {
        // Arrange — AI-supplied branch is ignored; stored _sourceBranch is always used, so both calls
        // resolve to the same cache key and only one actual fetch occurs.
        var sut = new TestableAdoReviewContextTools(DefaultOptions(), sourceBranch: "feat/my-pr");
        sut.SetFile("/src/Foo.cs", "main content");

        // Act — AI tries two different branches; stored branch is enforced for both
        await sut.GetFileContentAsync("/src/Foo.cs", "main", 1, 1, CancellationToken.None);
        await sut.GetFileContentAsync("/src/Foo.cs", "feature/x", 1, 1, CancellationToken.None);

        // Assert — only one fetch; second call is a cache hit on the stored branch
        Assert.Equal(1, sut.FetchCallCount);
    }

    [Theory]
    [InlineData(VersionControlChangeType.Add, ChangeType.Add)]
    [InlineData(VersionControlChangeType.Edit, ChangeType.Edit)]
    [InlineData(VersionControlChangeType.Delete, ChangeType.Delete)]
    [InlineData(VersionControlChangeType.Rename, ChangeType.Edit)] // unknown → Edit
    public void MapChangeType_MapsAdoChangeTypeTodomainChangeType(VersionControlChangeType adoType, ChangeType expected)
    {
        Assert.Equal(expected, AdoReviewContextTools.MapChangeType(adoType));
    }

    // T006(a) — US1: source-branch enforcement
    [Fact]
    public async Task GetFileContentAsync_AlwaysUsesStoredSourceBranch_IgnoresAiSuppliedBranch()
    {
        // Arrange
        const string sourceBranch = "feature/my-pr";
        var sut = new TestableAdoReviewContextTools(DefaultOptions(), sourceBranch);
        sut.SetFile("/src/Foo.cs", "content");

        // Act — AI supplies "main" but _sourceBranch should win
        await sut.GetFileContentAsync("/src/Foo.cs", "main", 1, 10, CancellationToken.None);

        // Assert — the branch forwarded to FetchRawFileContentAsync must be the stored source branch
        Assert.Equal(sourceBranch, sut.LastFetchedBranch);
    }

    // T006(b) — US1: warning log when file not found
    [Fact]
    public async Task GetFileContentAsync_FileNotFound_EmitsWarningWithPathAndBranch()
    {
        // Arrange
        const string sourceBranch = "feature/my-pr";
        var logger = Substitute.For<ILogger<AdoReviewContextTools>>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);
        var sut = new TestableAdoReviewContextTools(DefaultOptions(), sourceBranch, logger);
        // File not registered → FetchRawFileContentAsync returns null

        // Act
        await sut.GetFileContentAsync("/src/Missing.cs", "main", 1, 10, CancellationToken.None);

        // Assert — a Warning must be logged containing the path and branch
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("/src/Missing.cs") && o.ToString()!.Contains(sourceBranch)),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // T006(c) — US1: empty string returned when file not found (regression guard)
    [Fact]
    public async Task GetFileContentAsync_FileNotFound_ReturnsEmptyString_SourceBranchPath()
    {
        // Arrange
        const string sourceBranch = "feature/my-pr";
        var sut = new TestableAdoReviewContextTools(DefaultOptions(), sourceBranch);
        // File not registered

        // Act
        var result = await sut.GetFileContentAsync("/src/Missing.cs", "main", 1, 10, CancellationToken.None);

        // Assert
        Assert.Equal(string.Empty, result);
    }

    /// <summary>
    ///     Testable subclass of <see cref="AdoReviewContextTools" /> that replaces
    ///     <see cref="AdoReviewContextTools.FetchRawFileContentAsync" /> with a controlled
    ///     in-memory implementation.
    /// </summary>
    private sealed class TestableAdoReviewContextTools : AdoReviewContextTools
    {
        private readonly Dictionary<string, string?> _files = new(StringComparer.Ordinal);

        public TestableAdoReviewContextTools(
            IOptions<AiReviewOptions> options,
            string sourceBranch = "feature/test",
            ILogger<AdoReviewContextTools>? logger = null)
            : base(
                new VssConnectionFactory(Substitute.For<TokenCredential>()),
                Substitute.For<IClientAdoCredentialRepository>(),
                options,
                "https://dev.azure.com/org",
                "proj",
                "repo",
                sourceBranch,
                1,
                1,
                null,
                logger)
        {
        }

        /// <summary>Gets the total number of calls made to the underlying fetch method.</summary>
        public int FetchCallCount { get; private set; }

        /// <summary>Gets the branch name last passed to <see cref="AdoReviewContextTools.FetchRawFileContentAsync" />.</summary>
        public string? LastFetchedBranch { get; private set; }

        /// <summary>Registers a file path with its content for retrieval.</summary>
        public void SetFile(string path, string? content)
        {
            this._files[path] = content;
        }

        /// <inheritdoc />
        protected internal override Task<string?> FetchRawFileContentAsync(string path, string branch, CancellationToken ct)
        {
            this.FetchCallCount++;
            this.LastFetchedBranch = branch;
            this._files.TryGetValue(path, out var content);
            return Task.FromResult(content);
        }
    }
}
