// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Infrastructure.Features.ProCursor.Broker;

/// <summary>Validates client existence before invoking the configured SCM materialization backend.</summary>
public sealed class LocalProPrScmBroker(IClientAdminService clientAdminService, IProCursorScmBroker scmBackend) : IProCursorScmBroker
{
    public async Task<string?> GetLatestCommitShaAsync(
        ProCursorKnowledgeSourceDto source, ProCursorTrackedBranchDto trackedBranch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(trackedBranch);
        await this.EnsureClientExistsAsync(source.ClientId, ct);
        return await scmBackend.GetLatestCommitShaAsync(source, trackedBranch, ct);
    }

    public async Task<ProCursorScmMaterializationResponse> MaterializeAsync(
        ProCursorKnowledgeSourceDto source, ProCursorTrackedBranchDto trackedBranch,
        string? requestedCommitSha, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(trackedBranch);
        await this.EnsureClientExistsAsync(source.ClientId, ct);
        return await scmBackend.MaterializeAsync(source, trackedBranch, requestedCommitSha, ct);
    }

    private async Task EnsureClientExistsAsync(Guid clientId, CancellationToken ct)
    {
        if (!await clientAdminService.ExistsAsync(clientId, ct))
        {
            throw new KeyNotFoundException($"Client {clientId} was not found.");
        }
    }
}
