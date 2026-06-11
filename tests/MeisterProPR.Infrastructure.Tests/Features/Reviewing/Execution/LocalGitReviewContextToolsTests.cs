// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution;

public sealed class LocalGitReviewContextToolsTests
{
    [Fact]
    public async Task LocalTools_ReadChangedFilesTreeAndContent_FromWorkspace()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var workspace = new StubWorkspace();
        var tools = new LocalGitReviewContextTools(
            workspace,
            new DisabledProCursorGateway(),
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileSizeBytes = 1024 * 1024 }),
            new ReviewContextToolsRequest(review, "feature/demo", 7, Guid.NewGuid(), TargetBranch: "main"),
            NullLogger<LocalGitReviewContextTools>.Instance);

        var changedFiles = await tools.GetChangedFilesAsync(CancellationToken.None);
        var tree = await tools.GetFileTreeAsync("feature/demo", CancellationToken.None);
        var content = await tools.GetFileContentAsync("src/Demo.cs", "feature/demo", 2, 3, CancellationToken.None);

        Assert.Single(changedFiles);
        Assert.Equal(["src/Demo.cs"], tree);
        Assert.Equal("line2\nline3", content);
    }

    private sealed class StubWorkspace : IReviewRepositoryWorkspace
    {
        public ReviewRepositoryWorkspaceLease Lease { get; } = new(
            Guid.NewGuid(),
            "workspace-key",
            "/tmp/mirror",
            "/tmp/source",
            "/tmp/target",
            "head-sha",
            "base-sha",
            "merge-base",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "Active");

        public Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<ChangedFileSummary>>([new ChangedFileSummary("src/Demo.cs", ChangeType.Edit)]);
        }

        public Task<IReadOnlyList<string>> GetFileTreeAsync(string branchSide, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<string>>(["src/Demo.cs"]);
        }

        public Task<string?> ReadFileAsync(string path, string branchSide, CancellationToken ct)
        {
            return Task.FromResult<string?>("line1\nline2\nline3\nline4");
        }

        public Task<string?> GetUnifiedDiffAsync(string path, CancellationToken ct)
        {
            return Task.FromResult<string?>("@@ -1,1 +1,2 @@\n-line1\n+line1\n+line2");
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
