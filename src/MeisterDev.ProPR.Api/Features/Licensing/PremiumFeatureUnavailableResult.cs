// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Features.Licensing;

/// <summary>Structured error result returned when a premium capability is not currently available.</summary>
public sealed class PremiumFeatureUnavailableResult : ObjectResult
{
    /// <summary>Initializes a premium-unavailable result for the supplied capability snapshot.</summary>
    /// <param name="capability">The capability that is not available.</param>
    /// <param name="message">
    ///     What to report instead of the capability's own message. An endpoint that guards several settings
    ///     behind one capability passes a message naming the settings this request was refused, because the
    ///     capability's own message describes the capability and not the request.
    /// </param>
    /// <param name="statusCode">The status code to answer with.</param>
    public PremiumFeatureUnavailableResult(
        CapabilitySnapshot capability,
        string? message = null,
        int statusCode = StatusCodes.Status409Conflict)
        : base(
            new PremiumFeatureUnavailablePayload(
                "premium_feature_unavailable",
                capability.Key,
                message ?? capability.Message ?? $"Capability '{capability.Key}' is unavailable.",
                capability.Reason))
    {
        this.StatusCode = statusCode;
    }
}

/// <summary>
///     JSON payload used for premium-unavailable responses. <c>reason</c> carries which of the ways a capability
///     can be unavailable applies, so a caller can act on it without reading <c>message</c>.
/// </summary>
public sealed record PremiumFeatureUnavailablePayload(
    string Error,
    string Feature,
    string Message,
    PremiumCapabilityUnavailableReason? Reason = null);
