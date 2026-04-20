// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Providers.Common;

internal static class ProviderResolutionUtilities
{
    public static async Task<ScmProvider> ResolveProviderAsync(
        string organizationUrl,
        Guid? clientId,
        IClientScmConnectionRepository? connectionRepository,
        CancellationToken ct)
    {
        if (!clientId.HasValue || connectionRepository is null)
        {
            return ScmProvider.AzureDevOps;
        }

        var normalizedHostBaseUrl = NormalizeHostBaseUrl(organizationUrl);
        var matchingProviders = (await connectionRepository.GetByClientIdAsync(clientId.Value, ct))
            .Where(connection => connection.IsActive)
            .Where(connection => string.Equals(
                connection.HostBaseUrl,
                normalizedHostBaseUrl,
                StringComparison.OrdinalIgnoreCase))
            .Select(connection => connection.ProviderFamily)
            .Distinct()
            .ToList();

        if (matchingProviders.Count == 1)
        {
            return matchingProviders[0];
        }

        if (matchingProviders.Count > 1)
        {
            if (LooksLikeAzureDevOpsScope(organizationUrl) && matchingProviders.Contains(ScmProvider.AzureDevOps))
            {
                return ScmProvider.AzureDevOps;
            }

            throw new InvalidOperationException(
                $"Multiple active SCM providers share host {normalizedHostBaseUrl} for client {clientId.Value}. The repository configuration provider is ambiguous.");
        }

        if (LooksLikeAzureDevOpsScope(organizationUrl))
        {
            return ScmProvider.AzureDevOps;
        }

        throw new InvalidOperationException($"No active SCM provider connection matched host {normalizedHostBaseUrl} for client {clientId.Value}.");
    }

    internal static bool LooksLikeAzureDevOpsScope(string organizationUrl)
    {
        if (!Uri.TryCreate(organizationUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, "dev.azure.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeHostBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Provider scope must be an absolute URL.", nameof(value));
        }

        var builder = new UriBuilder(uri)
        {
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };

        return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
}
