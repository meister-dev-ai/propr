// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Options;
using MeisterProPR.CodeAnalysis;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Shared harness for the cross-file reference/definition tool tests. Builds a
///     <see cref="LocalGitReviewContextTools" /> over an in-memory workspace and the real composite
///     analyzer (Tree-sitter when native libs are present + the always-available Roslyn backend).
/// </summary>
internal static class StructuralReferenceToolTestHarness
{
    public static bool TreeSitterAvailable => FileByFileContextPrefetchStageTests.CreateRealAnalyzerIfAvailable() is not null;

    public static LocalGitReviewContextTools CreateTools(
        IReadOnlyDictionary<string, string> files,
        AiReviewOptions? options = null,
        bool includeAnalyzer = true)
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var workspace = new DictionaryWorkspace(files);

        var analyzer = includeAnalyzer ? BuildComposite() : null;

        return new LocalGitReviewContextTools(
            workspace,
            new DisabledProCursorGateway(),
            Microsoft.Extensions.Options.Options.Create(options ?? DefaultOptions()),
            new ReviewContextToolsRequest(review, "feature/demo", 7, Guid.NewGuid(), TargetBranch: "main"),
            NullLogger<LocalGitReviewContextTools>.Instance,
            analyzer);
    }

    public static AiReviewOptions DefaultOptions()
    {
        return new AiReviewOptions
        {
            MaxFileSizeBytes = 1024 * 1024,
            EnableStructuralReferenceTools = true,
            MaxReferenceCandidateFiles = 200,
            MaxReferenceResults = 50,
            MaxReferenceResultChars = 8000,
            ReferenceResolutionTimeoutMs = 4000,
        };
    }

    private static IStructuralCodeAnalyzer BuildComposite()
    {
        var treeSitter = FileByFileContextPrefetchStageTests.CreateRealAnalyzerIfAvailable();
        var roslyn = FileByFileContextPrefetchStageTests.CreateRoslynAnalyzer();
        return treeSitter is null
            ? new CompositeStructuralCodeAnalyzer([roslyn])
            : new CompositeStructuralCodeAnalyzer([treeSitter, roslyn]);
    }

    private sealed class DictionaryWorkspace(IReadOnlyDictionary<string, string> files) : IReviewRepositoryWorkspace
    {
        public ReviewRepositoryWorkspaceLease Lease { get; } = new(
            Guid.NewGuid(), "workspace-key", "/tmp/mirror", "/tmp/source", "/tmp/target",
            "head-sha", "base-sha", "merge-base", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Active");

        public Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<ChangedFileSummary>>([]);
        }

        public Task<IReadOnlyList<string>> GetFileTreeAsync(string branchSide, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<string>>(files.Keys.OrderBy(static k => k, StringComparer.Ordinal).ToList());
        }

        public Task<string?> ReadFileAsync(string path, string branchSide, CancellationToken ct)
        {
            return Task.FromResult(files.TryGetValue(path, out var content) ? content : null);
        }

        public Task<string?> GetUnifiedDiffAsync(string path, CancellationToken ct)
        {
            return Task.FromResult<string?>(null);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
