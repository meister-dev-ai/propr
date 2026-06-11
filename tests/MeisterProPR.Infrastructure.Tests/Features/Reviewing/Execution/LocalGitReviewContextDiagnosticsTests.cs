// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Offline;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution;

public sealed class LocalGitReviewContextDiagnosticsTests
{
    [Fact]
    public async Task DiagnosticsReader_ProjectsWorkspacePreparedSummary()
    {
        var repository = new InMemoryReviewJobRepository();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 42, 1);
        await repository.AddAsync(job, CancellationToken.None);

        var recorder = new InMemoryProtocolRecorder(repository);
        var protocolId = await recorder.BeginAsync(job.Id, 1, "workspace", ct: CancellationToken.None);
        await recorder.RecordReviewStrategyEventAsync(
            protocolId,
            ReviewProtocolEventNames.LocalWorkspacePrepared,
            "{}",
            "{\"workspaceKey\":\"workspace-123\"}",
            null,
            CancellationToken.None);

        var reader = new InMemoryReviewDiagnosticsReader(repository);
        var result = await reader.GetJobProtocolAsync(job.Id, true, CancellationToken.None);

        var protocol = Assert.Single(result!.Protocols);
        Assert.NotNull(protocol.Workspace);
        Assert.True(protocol.Workspace!.Prepared);
        Assert.Equal("workspace-123", protocol.Workspace.WorkspaceKey);
    }

    [Fact]
    public async Task DiagnosticsReader_ProjectsWorkspaceFailureAndFallbackSummary()
    {
        var repository = new InMemoryReviewJobRepository();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 42, 1);
        await repository.AddAsync(job, CancellationToken.None);

        var recorder = new InMemoryProtocolRecorder(repository);
        var protocolId = await recorder.BeginAsync(job.Id, 1, "workspace", ct: CancellationToken.None);
        await recorder.RecordReviewStrategyEventAsync(
            protocolId,
            ReviewProtocolEventNames.LocalWorkspaceFailed,
            "{}",
            "{\"stage\":\"fetch\",\"code\":\"workspace_prepare_failed\",\"message\":\"git fetch failed\"}",
            null,
            CancellationToken.None);
        await recorder.RecordReviewStrategyEventAsync(
            protocolId,
            ReviewProtocolEventNames.LocalWorkspaceFallbackApplied,
            "{}",
            "{}",
            null,
            CancellationToken.None);

        var reader = new InMemoryReviewDiagnosticsReader(repository);
        var result = await reader.GetJobProtocolAsync(job.Id, true, CancellationToken.None);

        var protocol = Assert.Single(result!.Protocols);
        Assert.NotNull(protocol.Workspace);
        Assert.False(protocol.Workspace!.Prepared);
        Assert.True(protocol.Workspace.FallbackApplied);
        Assert.Equal("fetch", protocol.Workspace.FailureStage);
        Assert.Equal("workspace_prepare_failed", protocol.Workspace.FailureCode);
    }
}
