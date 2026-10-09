// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.Compatibility;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common.Compatibility;

/// <summary>Decodes retained status text whose native provider context is unavailable.</summary>
public static class SavedThreadStatusCompatibilityDecoder
{
    public static ThreadResolutionIntent Decode(string? status)
    {
        if (string.Equals(status, "WontFix", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "ByDesign", StringComparison.OrdinalIgnoreCase))
        {
            return ThreadResolutionIntent.AcceptedByHuman;
        }

        if (string.Equals(status, "Fixed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase))
        {
            return ThreadResolutionIntent.ClaimsFix;
        }

        return ThreadResolutionIntent.Active;
    }
}
