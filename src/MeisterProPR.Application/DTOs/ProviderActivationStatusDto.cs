// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>Global provider-family activation status for installation-wide administration.</summary>
public sealed record ProviderActivationStatusDto(
    ScmProvider ProviderFamily,
    bool IsEnabled,
    bool BaselineAdapterSetRegistered,
    IReadOnlyList<string> RegisteredCapabilities,
    ProviderConnectionReadinessLevel SupportClaimReadiness,
    string SupportClaimReason,
    DateTimeOffset UpdatedAt);
