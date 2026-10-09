// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.DependencyInjection;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

internal static class ProviderResolutionUtilities
{
    public static async Task<ScmProvider> ResolveProviderAsync(
        string organizationUrl,
        Guid? clientId,
        IClientScmConnectionRepository? connectionRepository,
        CancellationToken ct,
        IEnumerable<IScmConnectionConfigurationPolicy>? configurationPolicies = null,
        string ambiguityContext = "repository configuration")
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

        var compatibilityProviders = (configurationPolicies ?? ScmLocalPolicyFactory.CreateConfigurationPolicies())
            .ToDictionary(policy => policy.Provider)
            .Values.Where(policy => policy.MatchesCompatibilityScope(organizationUrl))
            .Select(policy => policy.Provider).ToList();
        var compatibilityProvider = compatibilityProviders.Count == 1 ? compatibilityProviders[0] : (ScmProvider?)null;

        if (matchingProviders.Count > 1)
        {
            if (compatibilityProvider.HasValue && matchingProviders.Contains(compatibilityProvider.Value))
            {
                return compatibilityProvider.Value;
            }

            throw new InvalidOperationException(
                $"Multiple active SCM providers share host {normalizedHostBaseUrl} for client {clientId.Value}. The {ambiguityContext} provider is ambiguous.");
        }

        if (compatibilityProvider.HasValue)
        {
            return compatibilityProvider.Value;
        }

        throw new InvalidOperationException($"No active SCM provider connection matched host {normalizedHostBaseUrl} for client {clientId.Value}.");
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
