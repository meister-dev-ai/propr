// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;

namespace MeisterProPR.Infrastructure.Features.Licensing.Support;

/// <summary>Static catalog for installation-wide capability policy.</summary>
public sealed class StaticPremiumCapabilityCatalog : IPremiumCapabilityCatalog
{
    private static readonly IReadOnlyList<PremiumCapabilityDefinition> Capabilities =
    [
        new(
            PremiumCapabilityKey.SsoAuthentication,
            "Single sign-on",
            "A commercial license is required to use single sign-on, including in self-hosted deployments.",
            "Single sign-on is currently disabled for this installation."),
        new(
            PremiumCapabilityKey.ParallelReviewExecution,
            "Parallel review execution",
            "A commercial license is required to run more than one active PR review at a time, including in self-hosted deployments.",
            "Parallel review execution is currently disabled for this installation."),
        new(
            PremiumCapabilityKey.MultipleScmProviders,
            "Multiple SCM providers",
            "A commercial license is required to configure more than one SCM provider, including in self-hosted deployments.",
            "Multiple SCM providers are currently disabled for this installation."),
        new(
            PremiumCapabilityKey.CrawlConfigs,
            "Crawl configurations",
            "A commercial license is required to manage guided crawl configurations and discovery, including in self-hosted deployments.",
            "Crawl configurations are currently disabled for this installation."),
        new(
            PremiumCapabilityKey.Budgeting,
            "Budgeting",
            "A commercial license is required to set and enforce USD spend budgets, including in self-hosted deployments.",
            "Budgeting is currently disabled for this installation."),
    ];

    private static readonly IReadOnlyDictionary<string, PremiumCapabilityDefinition> CapabilityMap =
        Capabilities.ToDictionary(capability => capability.Key, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PremiumCapabilityDefinition> GetAll()
    {
        return Capabilities;
    }

    public PremiumCapabilityDefinition? Get(string capabilityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityKey);

        return CapabilityMap.TryGetValue(capabilityKey, out var definition)
            ? definition
            : null;
    }
}
