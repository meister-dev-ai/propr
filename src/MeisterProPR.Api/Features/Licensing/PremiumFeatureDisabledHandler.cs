// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Ports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.FeatureManagement.Mvc;

namespace MeisterProPR.Api.Features.Licensing;

/// <summary>Maps MVC feature-gate failures onto ProPR's premium-unavailable error payload.</summary>
public sealed class PremiumFeatureDisabledHandler : IDisabledFeaturesHandler
{
    /// <summary>Translates disabled feature keys into ProPR's structured premium-unavailable API response.</summary>
    public async Task HandleDisabledFeatures(
        IEnumerable<string> features,
        ActionExecutingContext context)
    {
        var capabilityKey = features.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(capabilityKey))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status404NotFound);
            return;
        }

        var licensingCapabilityService = context.HttpContext.RequestServices.GetService<ILicensingCapabilityService>();
        if (licensingCapabilityService is null)
        {
            context.Result = new ObjectResult(new { error = "Licensing services are unavailable." })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
            };
            return;
        }

        var capability = await licensingCapabilityService.GetCapabilityAsync(capabilityKey);
        context.Result = new PremiumFeatureUnavailableResult(capability, StatusCodes.Status403Forbidden);
    }
}
