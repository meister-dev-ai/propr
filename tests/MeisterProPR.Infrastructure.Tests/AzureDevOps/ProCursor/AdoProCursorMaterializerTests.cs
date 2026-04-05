// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Core;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AzureDevOps;
using MeisterProPR.Infrastructure.AzureDevOps.ProCursor;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.Wiki.WebApi;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps.ProCursor;

/// <summary>
///     Tests the Azure DevOps-backed ProCursor materializers against a controlled in-memory source state.
/// </summary>
public sealed class AdoProCursorMaterializerTests
{
    private static readonly Guid ClientId = Guid.Parse("20000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task RepositoryMaterializer_MaterializeAsync_WritesScopedTextFiles()
    {
        var source = CreateSource(ProCursorSourceKind.Repository, "refs/heads/main", "/src");
        var trackedBranch = source.TrackedBranches.Single();
        var materializer = new TestableRepositoryMaterializer();

        materializer.SetBranchHead("main", "commit-head");
        materializer.SetTree(
            "commit-head",
            "/src/App/Program.cs",
            "/src/Docs/Runbook.md",
            "/src/Images/logo.png",
            "/tests/AppTests.cs");
        materializer.SetContent("commit-head", "/src/App/Program.cs", "namespace App;\ninternal sealed class Program;\n");
        materializer.SetContent("commit-head", "/src/Docs/Runbook.md", "# Runbook\nRestart the worker after changing tokens.\n");

        var result = await materializer.MaterializeAsync(source, trackedBranch, null, CancellationToken.None);

        try
        {
            Assert.Equal("commit-head", result.CommitSha);
            Assert.Equal(new[] { "/src/App/Program.cs", "/src/Docs/Runbook.md" }, result.MaterializedPaths);
            Assert.True(File.Exists(Path.Combine(result.RootDirectory, "src", "App", "Program.cs")));
            Assert.True(File.Exists(Path.Combine(result.RootDirectory, "src", "Docs", "Runbook.md")));
            Assert.False(File.Exists(Path.Combine(result.RootDirectory, "tests", "AppTests.cs")));
            Assert.False(File.Exists(Path.Combine(result.RootDirectory, "src", "Images", "logo.png")));
        }
        finally
        {
            DeleteDirectory(result.RootDirectory);
        }
    }

    [Fact]
    public async Task RepositoryMaterializer_MaterializeAsync_UsesRequestedCommitWhenProvided()
    {
        var source = CreateSource(ProCursorSourceKind.Repository, "main", null);
        var trackedBranch = source.TrackedBranches.Single();
        var materializer = new TestableRepositoryMaterializer();

        materializer.SetBranchHead("main", "head-commit");
        materializer.SetTree("requested-commit", "/README.md");
        materializer.SetContent("requested-commit", "/README.md", "# Requested\nPinned commit content.\n");

        var result = await materializer.MaterializeAsync(source, trackedBranch, "requested-commit", CancellationToken.None);

        try
        {
            Assert.Equal("requested-commit", result.CommitSha);
            Assert.Equal(new[] { "/README.md" }, result.MaterializedPaths);
            Assert.True(File.Exists(Path.Combine(result.RootDirectory, "README.md")));
        }
        finally
        {
            DeleteDirectory(result.RootDirectory);
        }
    }

    [Fact]
    public async Task WikiMaterializer_MaterializeAsync_SkipsBinaryAttachments()
    {
        var source = CreateSource(ProCursorSourceKind.AdoWiki, "refs/heads/wikiMain", "/wiki");
        var trackedBranch = source.TrackedBranches.Single();
        var materializer = new TestableWikiMaterializer();

        materializer.SetBranchHead("wikiMain", "wiki-commit");
        materializer.SetTree(
            "wiki-commit",
            "/wiki/Home.md",
            "/wiki/Operations/Token-Caching.md",
            "/wiki/.attachments/diagram.png",
            "/docs/Other.md");
        materializer.SetContent("wiki-commit", "/wiki/Home.md", "# Home\nWelcome to the wiki.\n");
        materializer.SetContent("wiki-commit", "/wiki/Operations/Token-Caching.md", "# Token caching\nReuse the active token until it expires.\n");

        var result = await materializer.MaterializeAsync(source, trackedBranch, null, CancellationToken.None);

        try
        {
            Assert.Equal("wiki-commit", result.CommitSha);
            Assert.Equal(new[] { "/wiki/Home.md", "/wiki/Operations/Token-Caching.md" }, result.MaterializedPaths);
            Assert.True(File.Exists(Path.Combine(result.RootDirectory, "wiki", "Home.md")));
            Assert.True(File.Exists(Path.Combine(result.RootDirectory, "wiki", "Operations", "Token-Caching.md")));
            Assert.False(File.Exists(Path.Combine(result.RootDirectory, "wiki", ".attachments", "diagram.png")));
            Assert.False(File.Exists(Path.Combine(result.RootDirectory, "docs", "Other.md")));
        }
        finally
        {
            DeleteDirectory(result.RootDirectory);
        }
    }

    [Fact]
    public async Task WikiMaterializer_ResolveRepositoryIdAsync_UsesCanonicalWikiIdentifier()
    {
        var source = CreateSource(ProCursorSourceKind.AdoWiki, "refs/heads/wikiMain", "/wiki");
        var wikiId = Guid.Parse("30000000-0000-0000-0000-000000000071");
        var repositoryId = Guid.Parse("30000000-0000-0000-0000-000000000072");

        source.UpdateDefinition(
            source.DisplayName,
            source.OrganizationUrl,
            source.ProjectId,
            "legacy-repository-id",
            source.DefaultBranch,
            source.RootPath,
            source.IsEnabled,
            source.SymbolMode,
            source.OrganizationScopeId,
            "azureDevOps",
            wikiId.ToString(),
            "Engineering Wiki");

        var materializer = new TestableWikiMaterializer();
        materializer.SetWikiRepository(wikiId, repositoryId);

        var resolvedRepositoryId = await materializer.ResolveRepositoryIdPublicAsync(source, CancellationToken.None);

        Assert.Equal(repositoryId.ToString(), resolvedRepositoryId);
    }

    [Fact]
    public async Task WikiMaterializer_ResolveRepositoryIdAsync_UsesLegacyDisplayNameWhenCanonicalMetadataIsMissing()
    {
        var source = CreateSource(ProCursorSourceKind.AdoWiki, "refs/heads/wikiMain", "/wiki");
        var repositoryId = Guid.Parse("30000000-0000-0000-0000-000000000073");

        source.UpdateDefinition(
            "Meister DEV Wiki",
            source.OrganizationUrl,
            source.ProjectId,
            "2",
            source.DefaultBranch,
            source.RootPath,
            source.IsEnabled,
            source.SymbolMode,
            source.OrganizationScopeId,
            null,
            null,
            null);

        var materializer = new TestableWikiMaterializer();
        materializer.SetWikiRepository(
            Guid.Parse("30000000-0000-0000-0000-000000000074"),
            repositoryId,
            "Meister DEV Wiki");

        var resolvedRepositoryId = await materializer.ResolveRepositoryIdPublicAsync(source, CancellationToken.None);

        Assert.Equal(repositoryId.ToString(), resolvedRepositoryId);
    }

    [Fact]
    public async Task RepositoryMaterializer_MaterializeAsync_RemovesStaleTrackedBranchWorkspaceDirectories()
    {
        var source = CreateSource(ProCursorSourceKind.Repository, "main", null);
        var trackedBranch = source.TrackedBranches.Single();
        var materializer = new TestableRepositoryMaterializer(
            Microsoft.Extensions.Options.Options.Create(new ProCursorOptions { TempWorkspaceRetentionMinutes = 1 }));

        materializer.SetBranchHead("main", "commit-head");
        materializer.SetTree("commit-head", "/README.md");
        materializer.SetContent("commit-head", "/README.md", "# Readme\n");

        var trackedBranchWorkspaceRoot = Path.Combine(
            Path.GetTempPath(),
            "meisterpropr-procursor",
            source.Id.ToString("N"),
            trackedBranch.Id.ToString("N"));
        var staleWorkspace = Path.Combine(trackedBranchWorkspaceRoot, "stale-workspace");
        var freshWorkspace = Path.Combine(trackedBranchWorkspaceRoot, "fresh-workspace");
        Directory.CreateDirectory(staleWorkspace);
        Directory.CreateDirectory(freshWorkspace);
        Directory.SetLastWriteTimeUtc(staleWorkspace, DateTime.UtcNow.AddMinutes(-10));

        var result = await materializer.MaterializeAsync(source, trackedBranch, null, CancellationToken.None);

        try
        {
            Assert.False(Directory.Exists(staleWorkspace));
            Assert.True(Directory.Exists(freshWorkspace));
        }
        finally
        {
            DeleteDirectory(result.RootDirectory);
            DeleteDirectory(trackedBranchWorkspaceRoot);
        }
    }

    private static ProCursorKnowledgeSource CreateSource(ProCursorSourceKind sourceKind, string branchName, string? rootPath)
    {
        var source = new ProCursorKnowledgeSource(
            Guid.NewGuid(),
            ClientId,
            sourceKind == ProCursorSourceKind.Repository ? "Repository" : "Wiki",
            sourceKind,
            "https://dev.azure.com/test-org",
            "test-project",
            sourceKind == ProCursorSourceKind.Repository ? "repo-id" : "wiki-id",
            branchName,
            rootPath,
            true,
            "auto");

        source.AddTrackedBranch(Guid.NewGuid(), branchName, ProCursorRefreshTriggerMode.Manual, true);
        return source;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private sealed class TestableRepositoryMaterializer : AdoRepositoryMaterializer
    {
        private readonly Dictionary<string, string> _branchHeads = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<string>> _trees = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string CommitSha, string Path), string?> _contents = new();

        public TestableRepositoryMaterializer(IOptions<ProCursorOptions>? options = null)
            : base(
                new VssConnectionFactory(Substitute.For<TokenCredential>()),
                Substitute.For<IClientAdoCredentialRepository>(),
                options ?? Microsoft.Extensions.Options.Options.Create(new ProCursorOptions()),
                NullLogger<AdoRepositoryMaterializer>.Instance)
        {
        }

        public void SetBranchHead(string branchName, string commitSha) => this._branchHeads[NormalizePath(branchName)] = commitSha;

        public void SetTree(string commitSha, params string[] paths) => this._trees[commitSha] = paths.Select(NormalizePath).ToList().AsReadOnly();

        public void SetContent(string commitSha, string path, string? content) => this._contents[(commitSha, NormalizePath(path))] = content;

        protected internal override Task<string> ResolveCommitShaAsync(
            ProCursorKnowledgeSource source,
            ProCursorTrackedBranch trackedBranch,
            string? requestedCommitSha,
            CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(requestedCommitSha))
            {
                return Task.FromResult(requestedCommitSha.Trim());
            }

            var branchKey = NormalizePath(trackedBranch.BranchName.Replace("refs/heads/", string.Empty, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(this._branchHeads[branchKey]);
        }

        protected internal override Task<IReadOnlyList<string>> ListPathsAsync(
            ProCursorKnowledgeSource source,
            string commitSha,
            CancellationToken ct)
        {
            return Task.FromResult(this._trees.TryGetValue(commitSha, out var paths)
                ? paths
                : (IReadOnlyList<string>)[]);
        }

        protected internal override Task<string?> GetFileContentAsync(
            ProCursorKnowledgeSource source,
            string commitSha,
            string path,
            CancellationToken ct)
        {
            this._contents.TryGetValue((commitSha, NormalizePath(path)), out var content);
            return Task.FromResult(content);
        }
    }

    private sealed class TestableWikiMaterializer : AdoWikiMaterializer
    {
        private readonly Dictionary<string, string> _branchHeads = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<string>> _trees = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string CommitSha, string Path), string?> _contents = new();
        private readonly Dictionary<Guid, WikiV2> _wikisById = [];

        public TestableWikiMaterializer(IOptions<ProCursorOptions>? options = null)
            : base(
                new VssConnectionFactory(Substitute.For<TokenCredential>()),
                Substitute.For<IClientAdoCredentialRepository>(),
                options ?? Microsoft.Extensions.Options.Options.Create(new ProCursorOptions()),
                NullLogger<AdoWikiMaterializer>.Instance)
        {
        }

        public void SetBranchHead(string branchName, string commitSha) => this._branchHeads[NormalizePath(branchName)] = commitSha;

        public void SetTree(string commitSha, params string[] paths) => this._trees[commitSha] = paths.Select(NormalizePath).ToList().AsReadOnly();

        public void SetContent(string commitSha, string path, string? content) => this._contents[(commitSha, NormalizePath(path))] = content;

        public void SetWikiRepository(Guid wikiId, Guid repositoryId, string? wikiName = null)
        {
            this._wikisById[wikiId] = new WikiV2
            {
                Id = wikiId,
                Name = wikiName,
                RepositoryId = repositoryId,
            };
        }

        public Task<string> ResolveRepositoryIdPublicAsync(ProCursorKnowledgeSource source, CancellationToken ct)
        {
            return this.ResolveRepositoryIdAsync(source, ct);
        }

        protected internal override Task<IReadOnlyList<WikiV2>> ListWikisAsync(
            ProCursorKnowledgeSource source,
            ClientAdoCredentials? credentials,
            CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<WikiV2>>(this._wikisById.Values.ToList().AsReadOnly());
        }

        protected internal override Task<string> ResolveCommitShaAsync(
            ProCursorKnowledgeSource source,
            ProCursorTrackedBranch trackedBranch,
            string? requestedCommitSha,
            CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(requestedCommitSha))
            {
                return Task.FromResult(requestedCommitSha.Trim());
            }

            var branchKey = NormalizePath(trackedBranch.BranchName.Replace("refs/heads/", string.Empty, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(this._branchHeads[branchKey]);
        }

        protected internal override Task<IReadOnlyList<string>> ListPathsAsync(
            ProCursorKnowledgeSource source,
            string commitSha,
            CancellationToken ct)
        {
            return Task.FromResult(this._trees.TryGetValue(commitSha, out var paths)
                ? paths
                : (IReadOnlyList<string>)[]);
        }

        protected internal override Task<string?> GetFileContentAsync(
            ProCursorKnowledgeSource source,
            string commitSha,
            string path,
            CancellationToken ct)
        {
            this._contents.TryGetValue((commitSha, NormalizePath(path)), out var content);
            return Task.FromResult(content);
        }
    }

    private static string NormalizePath(string value)
    {
        var normalized = value.Replace('\\', '/').Trim();
        return normalized.StartsWith('/') ? normalized : $"/{normalized}";
    }
}
