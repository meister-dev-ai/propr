// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;

namespace MeisterProPR.Infrastructure.Features.Licensing.Support;

/// <summary>Static catalog for the initial commercial premium capability set.</summary>
public sealed class StaticPremiumCapabilityCatalog : IPremiumCapabilityCatalog
{
    private static readonly IReadOnlyList<PremiumCapabilityDefinition> Capabilities =
    [
        new(
            PremiumCapabilityKey.SsoAuthentication,
            "Single sign-on",
            "Commercial edition is required to use single sign-on.",
            "Single sign-on is currently disabled for this installation."),
        new(
            PremiumCapabilityKey.ParallelReviewExecution,
            "Parallel review execution",
            "Commercial edition is required to run more than one active PR review at a time.",
            "Parallel review execution is currently disabled for this installation."),
        new(
            PremiumCapabilityKey.MultipleScmProviders,
            "Multiple SCM providers",
            "Commercial edition is required to configure more than one SCM provider.",
            "Multiple SCM providers are currently disabled for this installation."),
        new(
            PremiumCapabilityKey.CrawlConfigs,
            "Crawl configurations",
            "Commercial edition is required to manage guided crawl configurations and discovery.",
            "Crawl configurations are currently disabled for this installation."),
        new(
            PremiumCapabilityKey.ProCursor,
            "ProCursor",
            "Commercial edition is required to use ProCursor knowledge sources, indexing, and usage reporting.",
            "ProCursor is currently disabled for this installation."),
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
