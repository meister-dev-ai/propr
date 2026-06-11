// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution;

internal sealed class LocalGitReviewContextTools(
    IReviewRepositoryWorkspace workspace,
    IProCursorGateway proCursorGateway,
    IOptions<AiReviewOptions> options,
    ReviewContextToolsRequest request,
    ILogger logger)
    : ProviderReviewContextToolsBase(
        proCursorGateway,
        options,
        request.CodeReview,
        request.SourceBranch,
        request.IterationId,
        request.ClientId,
        request.KnowledgeSourceIds,
        logger,
        request.ProviderScopePath,
        request.TargetBranch,
        request.ChangedPathSnapshots), IAsyncDisposable
{
    private readonly string _normalizedSourceBranch = NormalizeBranchName(request.SourceBranch);

    private readonly string? _normalizedTargetBranch = string.IsNullOrWhiteSpace(request.TargetBranch)
        ? null
        : NormalizeBranchName(request.TargetBranch!);

    public ValueTask DisposeAsync()
    {
        return workspace.DisposeAsync();
    }

    protected override Task<IReadOnlyList<ChangedFileSummary>> LoadChangedFilesAsync(CancellationToken ct)
    {
        return workspace.GetChangedFilesAsync(ct);
    }

    protected override Task<IReadOnlyList<string>> LoadFileTreeAsync(string normalizedBranch, CancellationToken ct)
    {
        return workspace.GetFileTreeAsync(this.ResolveBranchSide(normalizedBranch), ct);
    }

    protected internal override Task<string?> FetchRawFileContentAsync(string normalizedPath, string normalizedBranch, CancellationToken ct)
    {
        return workspace.ReadFileAsync(this.NormalizePath(normalizedPath), this.ResolveBranchSide(normalizedBranch), ct);
    }

    protected override string NormalizePath(string path)
    {
        return path.Trim().TrimStart('/').Replace('\\', '/');
    }

    internal Task<string?> GetUnifiedDiffAsync(string path, CancellationToken ct)
    {
        return workspace.GetUnifiedDiffAsync(this.NormalizePath(path), ct);
    }

    private string ResolveBranchSide(string normalizedBranch)
    {
        if (this._normalizedTargetBranch is not null &&
            string.Equals(normalizedBranch, this._normalizedTargetBranch, StringComparison.OrdinalIgnoreCase))
        {
            return RepositorySearchBranchSides.Target;
        }

        return RepositorySearchBranchSides.Source;
    }

    private static string NormalizeBranchName(string branch)
    {
        return branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? branch["refs/heads/".Length..]
            : branch.Trim();
    }
}
