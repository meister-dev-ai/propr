// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Models;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>Persisted installation-wide override for one premium capability.</summary>
public sealed class PremiumCapabilityOverrideRecord
{
    public string CapabilityKey { get; set; } = string.Empty;

    public PremiumCapabilityOverrideState OverrideState { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}
