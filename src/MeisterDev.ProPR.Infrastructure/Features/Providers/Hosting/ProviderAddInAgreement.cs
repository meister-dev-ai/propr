// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.AddIns;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     Measures what an add-in states about itself in its assembly against what its driver declares once it has
///     run.
/// </summary>
/// <remarks>
///     An administrator decides on the statement, which the host reads without executing the file. The driver's
///     declaration is what the host then enforces. A driver that declares something the administrator did not
///     approve is refused here, and the add-in stays out of the registry.
/// </remarks>
internal static class ProviderAddInAgreement
{
    public static string? Disagreement(DiscoveredProviderAddIn found, LoadedProviderAddIn loaded)
    {
        if (!string.Equals(found.Key, loaded.Key, StringComparison.Ordinal))
        {
            return $"The add-in states the identity '{found.Key}' in its assembly and its driver declares "
                   + $"'{loaded.Key}'. The two have to agree, because the activation was decided on the first.";
        }

        // The assembly states the hosts an administrator approves; the driver's declaration is the allow-list
        // the egress guard enforces. A driver contacting fewer of them is inside the decision, so only a host
        // it declares and the assembly does not state is refused. Families whose endpoints come from local
        // configuration declare a set that varies per installation, and demanding the two match exactly would
        // refuse them for a configuration change the administrator already made.
        var beyond = loaded.ReachedHostPatterns
            .Where(pattern => !found.ReachedHosts.Contains(pattern, StringComparer.Ordinal))
            .ToList();

        if (beyond.Count > 0)
        {
            return $"The add-in's driver declares hosts its assembly does not state ({Join(beyond)}). The "
                   + $"assembly states {Join(found.ReachedHosts)}, and where the family contacts is what an "
                   + "activation is decided on, so the driver may contact fewer of those hosts and no others.";
        }

        // The capability decides whether this installation's licence covers the family at all, so an add-in
        // that asks for one it did not state was approved as something else.
        if (!string.Equals(found.RequiredCapability, loaded.RequiredCapabilityKey, StringComparison.Ordinal))
        {
            return "The add-in states a different required capability in its assembly than its driver declares "
                   + $"(stated: {found.RequiredCapability ?? "none"}; declared: "
                   + $"{loaded.RequiredCapabilityKey ?? "none"}). What the family needs a licence for is part of "
                   + "what an activation is decided on.";
        }

        return null;
    }

    private static string Join(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? "none" : string.Join(", ", values);
    }
}
