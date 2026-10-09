// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;


namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Discovery;

/// <summary>Resolves native guided sources through the neutral discovery capability.</summary>
public static class AdoGuidedDiscovery
{
    public static async Task<GuidedSourceSelection> ResolveSourceAsync(
        IProviderAdminDiscoveryService discovery, Guid clientId, Guid scopeId, string projectId,
        ProCursorSourceKind kind, CanonicalSourceReferenceDto? reference, CancellationToken ct, Guid? connectionId = null)
    {
        var scope = await discovery.GetScopeAsync(clientId, scopeId, ct, connectionId);
        if (scope is null)
        {
            throw new KeyNotFoundException($"Organization scope {scopeId} was not found for client {clientId}.");
        }

        if (!scope.IsEnabled)
        {
            throw new InvalidOperationException("The selected organization scope is disabled.");
        }

        if (connectionId.HasValue && (scope.ClientId != clientId || scope.ConnectionId != connectionId.Value))
        {
            throw new InvalidOperationException("The selected organization scope does not belong to this connection.");
        }

        var sources = await discovery.ListSourceOptionsAsync(clientId, scope.Id, projectId, kind, ct, connectionId);
        var source = sources.FirstOrDefault(option =>
            string.Equals(option.CanonicalSourceRef.Provider, reference!.Provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(option.CanonicalSourceRef.Value, reference!.Value, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            throw new InvalidOperationException("The selected source is no longer available in Azure DevOps.");
        }

        var branches = await discovery.ListBranchOptionsAsync(clientId, scope.Id, projectId, kind, reference!, ct, connectionId);
        return new(
            scope, source, branches.Select(branch => branch.BranchName)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }
}
