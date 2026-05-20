// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Core;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
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
        var sut = new TestableAdoReviewContextTools(DefaultOptions(5));
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
        var sut = new TestableAdoReviewContextTools(DefaultOptions(5));
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
        var sut = new TestableAdoReviewContextTools(DefaultOptions(), "feat/my-pr");
        sut.SetFile("/src/Foo.cs", "main content");

        // Act — AI tries two different branches; stored branch is enforced for both
        await sut.GetFileContentAsync("/src/Foo.cs", "main", 1, 1, CancellationToken.None);
        await sut.GetFileContentAsync("/src/Foo.cs", "feature/x", 1, 1, CancellationToken.None);

        // Assert — only one fetch; second call is a cache hit on the stored branch
        Assert.Equal(1, sut.FetchCallCount);
    }

    [Fact]
    public async Task GetFileTreeAsync_AlwaysUsesStoredSourceBranch_IgnoresAiSuppliedBranch()
    {
        // Arrange
        const string sourceBranch = "feature/my-pr";
        var sut = new TestableAdoReviewContextTools(DefaultOptions(), sourceBranch);
        sut.SetTree("src/Example.cs", ".meister-propr/exclude");

        // Act
        var tree = await sut.GetFileTreeAsync("main", CancellationToken.None);

        // Assert
        Assert.Equal(sourceBranch, sut.LastFetchedTreeBranch);
        Assert.Contains("src/Example.cs", tree);
        Assert.Contains(".meister-propr/exclude", tree);
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
        var sut = new TestableAdoReviewContextTools(DefaultOptions(), sourceBranch, logger: logger);
        // File not registered → FetchRawFileContentAsync returns null

        // Act
        await sut.GetFileContentAsync("/src/Missing.cs", "main", 1, 10, CancellationToken.None);

        // Assert — a Warning must be logged containing the path and branch
        logger.Received(1)
            .Log(
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

    [Fact]
    public async Task SearchSourceRepoAsync_SourceScope_ReturnsRepositoryMatches()
    {
        var sut = new TestableAdoReviewContextTools(DefaultOptions(), "feature/my-pr");
        sut.SetTree("src/Foo.cs", "src/Other.cs");
        sut.SetFile("src/Foo.cs", "public class Foo\n{\n    private const string Marker = \"needle\";\n}");
        sut.SetFile("src/Other.cs", "public class Other {}", "feature/my-pr");

        var result = await sut.SearchSourceRepoAsync("needle", "**/*.cs", CancellationToken.None);

        Assert.Equal(RepositorySearchStatuses.Success, result.Status);
        Assert.Single(result.Matches);
        Assert.Equal("src/Foo.cs", result.Matches[0].FilePath);
    }

    [Fact]
    public async Task SearchSourceChangedFilesAsync_UsesChangedPathScopeOnly()
    {
        var sut = new TestableAdoReviewContextTools(
            DefaultOptions(),
            "feature/my-pr",
            "main",
            [
                new ChangedPathSnapshot("src/Foo.cs", ChangeType.Edit, true, true),
            ]);
        sut.SetTree("src/Foo.cs", "src/Other.cs");
        sut.SetFile("src/Foo.cs", "private const string Marker = \"needle\";", "feature/my-pr");
        sut.SetFile("src/Other.cs", "private const string Marker = \"needle\";", "feature/my-pr");

        var result = await sut.SearchSourceChangedFilesAsync("needle", "**/*.cs", CancellationToken.None);

        Assert.Equal(RepositorySearchStatuses.Success, result.Status);
        Assert.Single(result.Matches);
        Assert.All(result.Matches, match => Assert.Equal("src/Foo.cs", match.FilePath));
    }

    [Fact]
    public async Task SearchTargetRepoAsync_TargetBranchUsesBaselineBranch()
    {
        var sut = new TestableAdoReviewContextTools(DefaultOptions(), "feature/my-pr");
        sut.SetTree("src/Foo.cs");
        sut.SetFile("src/Foo.cs", "private const string Marker = \"baseline\";", "main");

        var result = await sut.SearchTargetRepoAsync("baseline", "**/*.cs", CancellationToken.None);

        Assert.Equal(RepositorySearchStatuses.Success, result.Status);
        Assert.Equal("main", sut.LastFetchedBranch);
    }

    [Fact]
    public async Task SearchTargetChangedFilesAsync_MissingTargetPath_ReturnsPartialWithLimitation()
    {
        var sut = new TestableAdoReviewContextTools(
            DefaultOptions(),
            "feature/my-pr",
            "main",
            [
                new ChangedPathSnapshot("src/NewFile.cs", ChangeType.Add, true, false),
            ]);

        var result = await sut.SearchTargetChangedFilesAsync("needle", "**/*.cs", CancellationToken.None);

        Assert.Equal(RepositorySearchStatuses.Partial, result.Status);
        Assert.Empty(result.Matches);
        Assert.Single(result.Limitations);
        Assert.Equal(RepositorySearchLimitationReasons.MissingOnBranch, result.Limitations[0].Reason);
    }

    [Fact]
    public async Task SearchSourceRepoAsync_InvalidRegex_ReturnsInvalidRequest()
    {
        var sut = new TestableAdoReviewContextTools(DefaultOptions(), "feature/my-pr");

        var result = await sut.SearchSourceRepoAsync("(", "**/*.cs", CancellationToken.None);

        Assert.Equal(RepositorySearchStatuses.InvalidRequest, result.Status);
        Assert.Single(result.Limitations);
        Assert.Equal(RepositorySearchLimitationReasons.InvalidRegex, result.Limitations[0].Reason);
    }

    [Fact]
    public async Task SearchSourceRepoAsync_NoMatches_ReturnsNoMatch()
    {
        var sut = new TestableAdoReviewContextTools(DefaultOptions(), "feature/my-pr");
        sut.SetTree("src/Foo.cs");
        sut.SetFile("src/Foo.cs", "public class Foo {}", "feature/my-pr");

        var result = await sut.SearchSourceRepoAsync("needle", "**/*.cs", CancellationToken.None);

        Assert.Equal(RepositorySearchStatuses.NoMatch, result.Status);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task SearchSourceRepoAsync_TruncatesLargeResultSetsDeterministically()
    {
        var sut = new TestableAdoReviewContextTools(DefaultOptions(), "feature/my-pr");
        sut.SetTree("src/Foo.cs");
        sut.SetFile(
            "src/Foo.cs",
            string.Join("\n", Enumerable.Range(1, 60).Select(index => $"needle {index:D2}")),
            "feature/my-pr");

        var result = await sut.SearchSourceRepoAsync("needle", "**/*.cs", CancellationToken.None);

        Assert.Equal(RepositorySearchStatuses.Partial, result.Status);
        Assert.True(result.Truncated);
        Assert.Equal(50, result.Matches.Count);
        Assert.Equal(1, result.Matches[0].LineNumber);
        Assert.Equal(50, result.Matches[^1].LineNumber);
        Assert.Contains(result.Limitations, limitation => limitation.Reason == RepositorySearchLimitationReasons.ResultTruncated);
    }

    /// <summary>
    ///     Testable subclass of <see cref="AdoReviewContextTools" /> that replaces
    ///     <see cref="AdoReviewContextTools.FetchRawFileContentAsync" /> with a controlled
    ///     in-memory implementation.
    /// </summary>
    private sealed class TestableAdoReviewContextTools : AdoReviewContextTools
    {
        private readonly Dictionary<string, string?> _files = new(StringComparer.Ordinal);
        private readonly string _sourceBranch;
        private readonly string? _targetBranch;
        private IReadOnlyList<string> _tree = [];

        public TestableAdoReviewContextTools(
            IOptions<AiReviewOptions> options,
            string sourceBranch = "feature/test",
            string? targetBranch = "main",
            IReadOnlyList<ChangedPathSnapshot>? changedPathSnapshots = null,
            ILogger<AdoReviewContextTools>? logger = null)
            : base(
                new VssConnectionFactory(Substitute.For<TokenCredential>()),
                Substitute.For<IClientScmConnectionRepository>(),
                Substitute.For<IProCursorGateway>(),
                options,
                "https://dev.azure.com/org",
                "proj",
                "repo",
                sourceBranch,
                1,
                1,
                null,
                null,
                targetBranch,
                changedPathSnapshots,
                logger)
        {
            this._sourceBranch = sourceBranch;
            this._targetBranch = targetBranch;
        }

        /// <summary>Gets the total number of calls made to the underlying fetch method.</summary>
        public int FetchCallCount { get; private set; }

        /// <summary>Gets the branch name last passed to <see cref="AdoReviewContextTools.FetchRawFileContentAsync" />.</summary>
        public string? LastFetchedBranch { get; private set; }

        /// <summary>Gets the branch name last passed to <see cref="AdoReviewContextTools.FetchFileTreePathsAsync" />.</summary>
        public string? LastFetchedTreeBranch { get; private set; }

        /// <summary>Registers a file path with its content for retrieval.</summary>
        public void SetFile(string path, string? content, string? branch = null)
        {
            var effectiveBranch = NormalizeBranch(branch ?? this._sourceBranch);
            this._files[$"{effectiveBranch}:{path}"] = content;
        }

        /// <summary>Registers a repository tree returned by <see cref="GetFileTreeAsync" />.</summary>
        public void SetTree(params string[] paths)
        {
            this._tree = paths.ToList().AsReadOnly();
        }

        /// <inheritdoc />
        protected internal override Task<IReadOnlyList<string>> FetchFileTreePathsAsync(string branch, CancellationToken ct)
        {
            this.LastFetchedTreeBranch = branch;
            return Task.FromResult(this._tree);
        }

        /// <inheritdoc />
        protected internal override Task<string?> FetchRawFileContentAsync(
            string path,
            string branch,
            CancellationToken ct)
        {
            this.FetchCallCount++;
            this.LastFetchedBranch = branch;
            this._files.TryGetValue($"{NormalizeBranch(branch)}:{path}", out var content);
            return Task.FromResult(content);
        }

        private static string NormalizeBranch(string branch)
        {
            return branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
                ? branch["refs/heads/".Length..]
                : branch;
        }
    }
}
