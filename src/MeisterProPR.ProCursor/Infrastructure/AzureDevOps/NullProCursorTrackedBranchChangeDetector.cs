// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Infrastructure.AzureDevOps.ProCursor;

/// <summary>
///     No-op change detector used when Azure DevOps integrations are stubbed.
/// </summary>
public sealed class NullProCursorTrackedBranchChangeDetector : IProCursorTrackedBranchChangeDetector
{
    public Task<string?> GetLatestCommitShaAsync(
        ProCursorKnowledgeSource source,
        ProCursorTrackedBranch trackedBranch,
        CancellationToken ct = default)
    {
        return Task.FromResult<string?>(null);
    }
}
