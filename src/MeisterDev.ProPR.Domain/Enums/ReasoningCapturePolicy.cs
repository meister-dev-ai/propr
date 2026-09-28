// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     Whether a tenant's review jobs capture the model's reasoning into the protocol. Reasoning can contain
///     verbatim source excerpts, so a processor operating one installation for several controllers needs the
///     choice per tenant instead of only installation-wide.
/// </summary>
public enum ReasoningCapturePolicy
{
    /// <summary>
    ///     (Default) The tenant states no policy and the installation switch decides. A tenant that has never been
    ///     touched reads this way.
    /// </summary>
    InstallationDefault = 0,

    /// <summary>Reasoning is captured for this tenant's jobs even when the installation switch is off.</summary>
    Enabled = 1,

    /// <summary>
    ///     Reasoning is not captured for this tenant's jobs even when the installation switch is on. The provider
    ///     is also not asked for a reasoning summary, so less text crosses the wire. Reasoning token counts are
    ///     still recorded, because budgets are computed from them.
    /// </summary>
    Disabled = 2,
}

/// <summary>Reads a stated policy as an override of the installation-wide reasoning-capture switch.</summary>
public static class ReasoningCapturePolicyExtensions
{
    /// <summary>
    ///     What this policy states, or <see langword="null" /> when it states nothing and the installation
    ///     switch decides. Written once so every place that resolves the policy resolves it the same way.
    /// </summary>
    /// <param name="policy">The tenant's stated policy.</param>
    public static bool? AsOverride(this ReasoningCapturePolicy policy)
    {
        return policy switch
        {
            ReasoningCapturePolicy.Enabled => true,
            ReasoningCapturePolicy.Disabled => false,
            _ => null,
        };
    }
}
