using Azure.Core;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AzureDevOps;
using MeisterProPR.Infrastructure.Options;
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
    public async Task GetFileContentAsync_DifferentBranches_FetchedSeparately()
    {
        // Arrange — same path, different branches treated as different cache entries
        var sut = new TestableAdoReviewContextTools(DefaultOptions());
        sut.SetFile("/src/Foo.cs", "main content");

        // Act
        await sut.GetFileContentAsync("/src/Foo.cs", "main", 1, 1, CancellationToken.None);
        await sut.GetFileContentAsync("/src/Foo.cs", "feature/x", 1, 1, CancellationToken.None);

        // Assert — two fetches for two different branches
        Assert.Equal(2, sut.FetchCallCount);
    }

    [Theory]
    [InlineData(VersionControlChangeType.Add, ChangeType.Add)]
    [InlineData(VersionControlChangeType.Edit, ChangeType.Edit)]
    [InlineData(VersionControlChangeType.Delete, ChangeType.Delete)]
    [InlineData(VersionControlChangeType.Rename, ChangeType.Edit)]   // unknown → Edit
    public void MapChangeType_MapsAdoChangeTypeTodomainChangeType(
        VersionControlChangeType adoType, ChangeType expected)
    {
        Assert.Equal(expected, AdoReviewContextTools.MapChangeType(adoType));
    }

    /// <summary>
    ///     Testable subclass of <see cref="AdoReviewContextTools" /> that replaces
    ///     <see cref="AdoReviewContextTools.FetchRawFileContentAsync" /> with a controlled
    ///     in-memory implementation.
    /// </summary>
    private sealed class TestableAdoReviewContextTools : AdoReviewContextTools
    {
        private readonly Dictionary<string, string?> _files = new(StringComparer.Ordinal);

        public TestableAdoReviewContextTools(IOptions<AiReviewOptions> options)
            : base(
                new VssConnectionFactory(Substitute.For<TokenCredential>()),
                Substitute.For<IClientAdoCredentialRepository>(),
                options,
                "https://dev.azure.com/org",
                "proj",
                "repo",
                1,
                1,
                null)
        {
        }

        /// <summary>Gets the total number of calls made to the underlying fetch method.</summary>
        public int FetchCallCount { get; private set; }

        /// <summary>Registers a file path with its content for retrieval.</summary>
        public void SetFile(string path, string? content)
        {
            this._files[path] = content;
        }

        /// <inheritdoc />
        protected internal override Task<string?> FetchRawFileContentAsync(string path, string branch, CancellationToken ct)
        {
            this.FetchCallCount++;
            this._files.TryGetValue(path, out var content);
            return Task.FromResult(content);
        }
    }
}
