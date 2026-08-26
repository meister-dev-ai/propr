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
    public PremiumFeatureUnavailableResult(
        CapabilitySnapshot capability,
        int statusCode = StatusCodes.Status409Conflict)
        : base(
            new PremiumFeatureUnavailablePayload(
                "premium_feature_unavailable",
                capability.Key,
                capability.Message ?? $"Capability '{capability.Key}' is unavailable.",
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
