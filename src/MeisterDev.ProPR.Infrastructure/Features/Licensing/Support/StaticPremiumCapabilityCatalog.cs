// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Support;

/// <summary>Static catalog for installation-wide capability policy.</summary>
public sealed class StaticPremiumCapabilityCatalog : IPremiumCapabilityCatalog
{
    private static readonly IReadOnlyList<PremiumCapabilityDefinition> Capabilities =
    [
        new(
            PremiumCapabilityKey.SsoAuthentication,
            "Single sign-on",
            "A commercial license is required to use single sign-on, including in self-hosted deployments.",
            "This installation's license does not cover single sign-on. A license that includes it is required.",
            "Single sign-on is currently disabled for this installation."),
        new(
            PremiumCapabilityKey.ParallelReviewExecution,
            "Parallel review execution",
            "A commercial license is required to run more than one active PR review at a time, including in self-hosted deployments.",
            "This installation's license does not cover parallel review execution. A license that includes it is required.",
            "Parallel review execution is currently disabled for this installation."),
        new(
            PremiumCapabilityKey.DistributedExecution,
            "Distributed review execution",
            "A commercial license is required to run reviews on registered runners, including in self-hosted deployments.",
            "This installation's license does not cover distributed review execution. A license that includes it is required.",
            "Distributed review execution is currently disabled for this installation."),
        new(
            PremiumCapabilityKey.MultipleScmProviders,
            "Multiple SCM providers",
            "A commercial license is required to configure more than one SCM provider, including in self-hosted deployments.",
            "This installation's license does not cover multiple SCM providers. A license that includes them is required.",
            "Multiple SCM providers are currently disabled for this installation."),
        new(
            PremiumCapabilityKey.CrawlConfigs,
            "Crawl configurations",
            "A commercial license is required to manage guided crawl configurations and discovery, including in self-hosted deployments.",
            "This installation's license does not cover crawl configurations. A license that includes them is required.",
            "Crawl configurations are currently disabled for this installation."),
        new(
            PremiumCapabilityKey.MentionAnswering,
            "Mention answering",
            "A commercial license is required to answer @-mentions in pull request comments, including in self-hosted deployments.",
            "This installation's license does not cover mention answering. A license that includes it is required.",
            "Mention answering is currently disabled for this installation."),
        new(
            PremiumCapabilityKey.Budgeting,
            "Budgeting",
            "A commercial license is required to set USD spend budgets and to view spend against them, including in self-hosted deployments. Budgets already set stay enforced.",
            "This installation's license does not cover budgeting. A license that includes it is required.",
            "Budgeting is currently disabled for this installation."),
        new(
            PremiumCapabilityKey.CodeInsights,
            "Code Insights",
            "A commercial license is required to collect and view code-quality insights, including in self-hosted deployments.",
            "This installation's license does not cover Code Insights. A license that includes it is required.",
            "Code Insights is currently disabled for this installation."),
        new(
            PremiumCapabilityKey.MultiTenancy,
            "Multi-tenancy",
            "A commercial license is required to use more than the built-in System tenant, including in self-hosted deployments.",
            "This installation's license does not cover multi-tenancy. A license that includes it is required.",
            "Multi-tenancy is currently disabled for this installation. Only the built-in System tenant is in use."),
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
