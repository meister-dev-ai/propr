// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Clients.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Clients.Support;

/// <summary>Static readiness evidence catalog used for provider support and host-variant classification.</summary>
public sealed class StaticProviderReadinessProfileCatalog : IProviderReadinessProfileCatalog
{
    internal const string Hosted = "hosted";
    internal const string SelfHosted = "selfHosted";

    private static readonly
        IReadOnlyDictionary<(ScmProvider ProviderFamily, string HostVariant), ProviderReadinessProfile> Profiles =
            new Dictionary<(ScmProvider ProviderFamily, string HostVariant), ProviderReadinessProfile>
            {
                [(ScmProvider.AzureDevOps, Hosted)] = new(
                    ScmProvider.AzureDevOps,
                    Hosted,
                    true,
                    true,
                    true,
                    true,
                    true,
                    "Azure DevOps Services is fully aligned to the provider support baseline."),
                [(ScmProvider.GitHub, Hosted)] = new(
                    ScmProvider.GitHub,
                    Hosted,
                    true,
                    true,
                    true,
                    true,
                    true,
                    "GitHub Cloud satisfies the current workflow-complete support bar."),
                [(ScmProvider.GitHub, SelfHosted)] = new(
                    ScmProvider.GitHub,
                    SelfHosted,
                    true,
                    true,
                    true,
                    true,
                    false,
                    "Self-hosted GitHub remains onboarding-ready until the observability proof matches the hosted baseline."),
                [(ScmProvider.GitLab, Hosted)] = new(
                    ScmProvider.GitLab,
                    Hosted,
                    true,
                    true,
                    false,
                    true,
                    true,
                    "GitLab hosted support remains onboarding-ready until lifecycle continuity proof reaches the feature 036 bar."),
                [(ScmProvider.GitLab, SelfHosted)] = new(
                    ScmProvider.GitLab,
                    SelfHosted,
                    true,
                    true,
                    false,
                    true,
                    false,
                    "Self-hosted GitLab remains onboarding-ready until lifecycle and observability proof are complete."),
                [(ScmProvider.Forgejo, Hosted)] = new(
                    ScmProvider.Forgejo,
                    Hosted,
                    true,
                    true,
                    false,
                    true,
                    true,
                    "Hosted Forgejo-family support remains onboarding-ready until lifecycle continuity proof is complete."),
                [(ScmProvider.Forgejo, SelfHosted)] = new(
                    ScmProvider.Forgejo,
                    SelfHosted,
                    true,
                    true,
                    false,
                    true,
                    false,
                    "Self-hosted Forgejo-family support remains onboarding-ready until lifecycle and observability proof are complete."),
            };

    /// <summary>Gets the readiness profile for a specific provider family and host URL.</summary>
    /// <param name="providerFamily">The SCM provider family.</param>
    /// <param name="hostBaseUrl">The base URL of the host.</param>
    /// <returns>The readiness profile for the provider and host variant.</returns>
    public ProviderReadinessProfile GetProfile(ScmProvider providerFamily, string hostBaseUrl)
    {
        var hostVariant = ResolveHostVariant(providerFamily, hostBaseUrl);
        if (Profiles.TryGetValue((providerFamily, hostVariant), out var profile))
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
        return Profiles
            .Where(entry => entry.Key.ProviderFamily == providerFamily)
            .Select(entry => entry.Value)
            .OrderBy(entry => entry.HostVariant, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
    }

    internal static string ResolveHostVariant(ScmProvider providerFamily, string hostBaseUrl)
    {
        if (!Uri.TryCreate(hostBaseUrl, UriKind.Absolute, out var uri))
        {
            return SelfHosted;
        }

        var host = uri.Host;
        return providerFamily switch
        {
            ScmProvider.AzureDevOps when host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) => Hosted,
            ScmProvider.AzureDevOps when host.EndsWith(
                ".visualstudio.com",
                StringComparison.OrdinalIgnoreCase) => Hosted,
            ScmProvider.GitHub when host.Equals("github.com", StringComparison.OrdinalIgnoreCase) => Hosted,
            ScmProvider.GitLab when host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase) => Hosted,
            ScmProvider.Forgejo when host.Equals("codeberg.org", StringComparison.OrdinalIgnoreCase) => Hosted,
            _ => SelfHosted,
        };
    }
}
