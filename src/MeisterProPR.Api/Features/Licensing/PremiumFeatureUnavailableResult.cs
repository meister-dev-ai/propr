// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Models;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Features.Licensing;

/// <summary>Structured error result returned when a premium capability is not currently available.</summary>
public sealed class PremiumFeatureUnavailableResult : ObjectResult
{
    /// <summary>Initializes a premium-unavailable result for the supplied capability snapshot.</summary>
    public PremiumFeatureUnavailableResult(
        CapabilitySnapshot capability,
        int statusCode = StatusCodes.Status409Conflict)
        : base(new PremiumFeatureUnavailablePayload(
            "premium_feature_unavailable",
            capability.Key,
            capability.Message ?? $"Capability '{capability.Key}' is unavailable."))
    {
        this.StatusCode = statusCode;
    }
}

/// <summary>JSON payload used for premium-unavailable responses.</summary>
public sealed record PremiumFeatureUnavailablePayload(string Error, string Feature, string Message);
