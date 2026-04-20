// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>Operator-facing provider operational status response.</summary>
public sealed record ProviderOperationalStatusDto(
    IReadOnlyList<ProviderConnectionOperationalStatusDto> Connections,
    IReadOnlyList<ProviderFamilyOperationalStatusDto> ProviderFamilies);

/// <summary>Operator-facing operational status for one provider connection.</summary>
public sealed record ProviderConnectionOperationalStatusDto(
    Guid ConnectionId,
    ScmProvider ProviderFamily,
    string DisplayName,
    string HostBaseUrl,
    string HostVariant,
    bool IsActive,
    string VerificationStatus,
    ProviderConnectionReadinessLevel ReadinessLevel,
    string ReadinessReason,
    IReadOnlyList<string>? MissingReadinessCriteria,
    string Health,
    DateTimeOffset? LastCheckedAt,
    string? FailureCategory,
    string StatusReason);

/// <summary>Operator-facing readiness summary for one provider family.</summary>
public sealed record ProviderFamilyOperationalStatusDto(
    ScmProvider ProviderFamily,
    bool BaselineAdapterSetRegistered,
    ProviderConnectionReadinessLevel LeastReadyLevel,
    string SummaryReason,
    int UnknownCount,
    int ConfiguredCount,
    int OnboardingReadyCount,
    int WorkflowCompleteCount,
    int DegradedCount,
    IReadOnlyList<ProviderHostVariantOperationalStatusDto> HostVariants);

/// <summary>Operator-facing readiness summary for one provider family host variant.</summary>
public sealed record ProviderHostVariantOperationalStatusDto(
    string HostVariant,
    ProviderConnectionReadinessLevel LeastReadyLevel,
    string SummaryReason,
    int UnknownCount,
    int ConfiguredCount,
    int OnboardingReadyCount,
    int WorkflowCompleteCount,
    int DegradedCount);
