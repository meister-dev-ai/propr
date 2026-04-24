// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Licensing.Models;

/// <summary>Catalog entry describing one premium capability and its commercial defaults.</summary>
public sealed record PremiumCapabilityDefinition(
    string Key,
    string DisplayName,
    string CommercialRequiredMessage,
    string CommercialDisabledMessage,
    bool DefaultWhenCommercial = true,
    bool RequiresCommercial = true);
