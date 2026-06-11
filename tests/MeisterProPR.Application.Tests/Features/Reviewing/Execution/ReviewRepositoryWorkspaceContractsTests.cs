// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class ReviewRepositoryWorkspaceContractsTests
{
    [Fact]
    public void ReviewContextToolsRequest_PreservesWorkspaceMetadata()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var lease = new ReviewRepositoryWorkspaceLease(
            Guid.NewGuid(),
            "workspace-key",
            "/tmp/mirror",
            "/tmp/head",
            "/tmp/base",
            "head-sha",
            "base-sha",
            "merge-base-sha",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "Active");
        var workspace = new StubReviewRepositoryWorkspace(lease);
        var failure = new ReviewWorkspaceFailure("prepare", "test_failure", "Test failure", true, false);

        var request = new ReviewContextToolsRequest(
            review,
            "feature/test",
            7,
            Guid.NewGuid(),
            Workspace: workspace,
            WorkspaceLease: lease,
            WorkspaceFailure: failure);

        Assert.Same(workspace, request.Workspace);
        Assert.Same(lease, request.WorkspaceLease);
        Assert.Same(failure, request.WorkspaceFailure);
    }

    [Fact]
    public void ReviewRepositoryWorkspacePreparationResult_SucceededReflectsWorkspacePresence()
    {
        var lease = new ReviewRepositoryWorkspaceLease(
            Guid.NewGuid(),
            "workspace-key",
            "/tmp/mirror",
            "/tmp/head",
            "/tmp/base",
            "head-sha",
            "base-sha",
            "merge-base-sha",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "Active");
        var workspace = new StubReviewRepositoryWorkspace(lease);

        Assert.True(new ReviewRepositoryWorkspacePreparationResult(workspace, null).Succeeded);
        Assert.False(
            new ReviewRepositoryWorkspacePreparationResult(
                null,
                new ReviewWorkspaceFailure("prepare", "failed", "failure", false, true)).Succeeded);
    }

    private sealed class StubReviewRepositoryWorkspace(ReviewRepositoryWorkspaceLease lease) : IReviewRepositoryWorkspace
    {
        public ReviewRepositoryWorkspaceLease Lease { get; } = lease;

        public Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<ChangedFileSummary>>([]);
        }

        public Task<IReadOnlyList<string>> GetFileTreeAsync(string branchSide, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<string?> ReadFileAsync(string path, string branchSide, CancellationToken ct)
        {
            return Task.FromResult<string?>(null);
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
