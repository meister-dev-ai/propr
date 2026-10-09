// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Clients.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Clients.Support;

/// <summary>Static readiness evidence catalog used for provider support and host-variant classification.</summary>
public sealed class StaticProviderReadinessProfileCatalog(IEnumerable<IScmConnectionConfigurationPolicy> localPolicies) : IProviderReadinessProfileCatalog
{
    private readonly IReadOnlyDictionary<ScmProvider, IScmConnectionConfigurationPolicy> _localPolicies =
        localPolicies.ToDictionary(policy => policy.Provider);

    internal const string Hosted = "hosted";
    internal const string SelfHosted = "selfHosted";

    private readonly IReadOnlyDictionary<(ScmProvider ProviderFamily, string HostVariant), ProviderReadinessProfile> _profiles =
        localPolicies.SelectMany(policy => policy.ReadinessProfiles).ToDictionary(profile => (profile.ProviderFamily, profile.HostVariant));

    /// <summary>Gets the readiness profile for a specific provider family and host URL.</summary>
    /// <param name="providerFamily">The SCM provider family.</param>
    /// <param name="hostBaseUrl">The base URL of the host.</param>
    /// <returns>The readiness profile for the provider and host variant.</returns>
    public ProviderReadinessProfile GetProfile(ScmProvider providerFamily, string hostBaseUrl)
    {
        var hostVariant = this._localPolicies.TryGetValue(providerFamily, out var policy)
            ? policy.ResolveHostVariant(hostBaseUrl)
            : SelfHosted;
        if (this._profiles.TryGetValue((providerFamily, hostVariant), out var profile))
        {
            return profile;
        }

        return new ProviderReadinessProfile(
            providerFamily,
            hostVariant,
            true,
            true,
            false,
            true,
            false,
            $"{providerFamily} {hostVariant} support remains onboarding-ready until host-variant validation is complete.");
    }

    /// <summary>Gets all readiness profiles for a specific provider family.</summary>
    /// <param name="providerFamily">The SCM provider family.</param>
    /// <returns>A read-only list of readiness profiles for the provider.</returns>
    public IReadOnlyList<ProviderReadinessProfile> GetProfiles(ScmProvider providerFamily)
    {
        return this._profiles
            .Where(entry => entry.Key.ProviderFamily == providerFamily)
            .Select(entry => entry.Value)
            .OrderBy(entry => entry.HostVariant, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
    }
}
