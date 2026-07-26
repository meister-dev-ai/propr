// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     The outcome of activating a connection profile, and when it was refused, which requirement was not met.
/// </summary>
/// <remarks>
///     A bare "activation failed" leaves an operator to guess between verification, bindings and model capability,
///     and the rule that decides is the repository's. Returning the reason from where the decision is made is the
///     only way to report it without a second copy of the rule that can drift.
/// </remarks>
/// <param name="Activated">Whether the profile is now the client's active one.</param>
/// <param name="Reason">Why activation was refused; <see langword="null" /> when it succeeded.</param>
public sealed record AiConnectionActivationResultDto(bool Activated, string? Reason = null)
{
    /// <summary>The profile is now active.</summary>
    public static AiConnectionActivationResultDto Success { get; } = new(true);

    /// <summary>The profile does not exist.</summary>
    public static AiConnectionActivationResultDto NotFound { get; } = new(false, "the connection profile no longer exists");

    /// <summary>Activation was refused for the stated reason.</summary>
    /// <param name="reason">Which requirement was not met, phrased for an operator.</param>
    public static AiConnectionActivationResultDto Refused(string reason)
    {
        return new AiConnectionActivationResultDto(false, reason);
    }
}
