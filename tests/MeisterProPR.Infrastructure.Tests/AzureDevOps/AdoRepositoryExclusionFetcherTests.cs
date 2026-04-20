// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Core;
using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

/// <summary>
///     Unit tests for <see cref="AdoRepositoryExclusionFetcher" /> using a testable subclass
///     that bypasses the ADO network layer.
/// </summary>
public class AdoRepositoryExclusionFetcherTests
{
    [Fact]
    public async Task FetchAsync_WithPatterns_ReturnsParsedExclusionRules()
    {
        var sut = new TestableAdoRepositoryExclusionFetcher();
        sut.SetContent("**/Migrations/*.cs\nsrc/Generated/**\n");

        var result = await sut.FetchAsync(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "main",
            null,
            CancellationToken.None);

        Assert.Equal(2, result.Patterns.Count);
        Assert.Contains("**/Migrations/*.cs", result.Patterns);
        Assert.Contains("src/Generated/**", result.Patterns);
        Assert.False(result.IsDefault);
    }

    [Fact]
    public async Task FetchAsync_AbsentFile_ReturnsDefault()
    {
        var sut = new TestableAdoRepositoryExclusionFetcher();
        sut.SetFileAbsent();

        var result = await sut.FetchAsync(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "main",
            null,
            CancellationToken.None);

        Assert.True(result.IsDefault);
    }

    [Fact]
    public async Task FetchAsync_EmptyFile_ReturnsEmpty()
    {
        var sut = new TestableAdoRepositoryExclusionFetcher();
        sut.SetContent(string.Empty);

        var result = await sut.FetchAsync(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "main",
            null,
            CancellationToken.None);

        // File is present but has no usable patterns — must map to Empty, NOT Default.
        Assert.False(result.IsDefault);
        Assert.False(result.HasPatterns);
    }

    [Fact]
    public async Task FetchAsync_FileWithOnlyBlankLinesAndComments_ReturnsEmpty()
    {
        var sut = new TestableAdoRepositoryExclusionFetcher();
        sut.SetContent("# This is a comment\n\n# Another comment\n   \n");

        var result = await sut.FetchAsync(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "main",
            null,
            CancellationToken.None);

        // File is present but contains only comments/whitespace — explicit empty rules, not default.
        Assert.False(result.IsDefault);
        Assert.False(result.HasPatterns);
    }

    [Fact]
    public async Task FetchAsync_WhitespaceFile_ReturnsEmpty()
    {
        var sut = new TestableAdoRepositoryExclusionFetcher();
        sut.SetContent("   \n\t\n  ");

        var result = await sut.FetchAsync(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "main",
            null,
            CancellationToken.None);

        // Whitespace-only file: present + no usable patterns — returns Empty (no exclusions), not Default.
        Assert.False(result.IsDefault);
        Assert.False(result.HasPatterns);
    }

    [Fact]
    public async Task FetchAsync_BlankLinesAndComments_AreIgnoredInPatternCount()
    {
        var sut = new TestableAdoRepositoryExclusionFetcher();
        sut.SetContent("# Exclude generated migrations\n**/Migrations/*.Designer.cs\n\n# Ignore snapshots\n**/Migrations/*ModelSnapshot.cs\n");

        var result = await sut.FetchAsync(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "main",
            null,
            CancellationToken.None);

        Assert.Equal(2, result.Patterns.Count);
        Assert.DoesNotContain(result.Patterns, p => p.StartsWith('#'));
    }

    [Fact]
    public async Task FetchAsync_ParsedPattern_MatchesExpectedFilePath()
    {
        var sut = new TestableAdoRepositoryExclusionFetcher();
        sut.SetContent("**/Migrations/*.Designer.cs\n");

        var result = await sut.FetchAsync(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "main",
            null,
            CancellationToken.None);

        Assert.True(result.Matches("src/Infrastructure/Migrations/20260101_Init.Designer.cs"));
        Assert.False(result.Matches("src/Infrastructure/Migrations/20260101_Init.cs"));
    }

    // ADO returns file paths with a leading '/' (e.g. "/openapi.json").
    // A simple filename pattern like "openapi.json" must match even with that prefix.
    [Fact]
    public async Task FetchAsync_SimpleFilenamePattern_MatchesAdoPathWithLeadingSlash()
    {
        var sut = new TestableAdoRepositoryExclusionFetcher();
        sut.SetContent("openapi.json\n");

        var result = await sut.FetchAsync(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "main",
            null,
            CancellationToken.None);

        Assert.True(result.Matches("/openapi.json"));
        Assert.False(result.Matches("/src/openapi.json"));
    }

    [Fact]
    public async Task FetchAsync_GlobPattern_MatchesAdoPathWithLeadingSlash()
    {
        var sut = new TestableAdoRepositoryExclusionFetcher();
        sut.SetContent("**/Migrations/*.Designer.cs\n");

        var result = await sut.FetchAsync(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "main",
            null,
            CancellationToken.None);

        Assert.True(result.Matches("/src/Infrastructure/Migrations/20260101_Init.Designer.cs"));
        Assert.False(result.Matches("/src/Infrastructure/Migrations/20260101_Init.cs"));
    }

    /// <summary>
    ///     Testable subclass that replaces the ADO file fetch with in-memory content.
    /// </summary>
    private sealed class TestableAdoRepositoryExclusionFetcher : AdoRepositoryExclusionFetcher
    {
        private string? _content;
        private bool _fileExists = true;

        public TestableAdoRepositoryExclusionFetcher()
            : base(
                new VssConnectionFactory(Substitute.For<TokenCredential>()),
                Substitute.For<IClientScmConnectionRepository>(),
                Substitute.For<ILogger<AdoRepositoryExclusionFetcher>>())
        {
        }

        /// <summary>Sets the raw content the virtual exclude file returns.</summary>
        public void SetContent(string content)
        {
            this._content = content;
            this._fileExists = true;
        }

        /// <summary>Simulates an absent <c>.meister-propr/exclude</c> file.</summary>
        public void SetFileAbsent()
        {
            this._fileExists = false;
        }

        /// <inheritdoc />
        protected override Task<string?> FetchExcludeFileAsync(
            string organizationUrl,
            string projectId,
            string repositoryId,
            string targetBranch,
            Guid? clientId,
            CancellationToken cancellationToken)
        {
            if (!this._fileExists)
            {
                return Task.FromResult<string?>(null);
            }

            // Return content as-is (including empty/whitespace) so tests can cover the
            // "file present but no usable patterns" branch independently of "file absent".
            return Task.FromResult<string?>(this._content);
        }
    }
}
