// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Clients.Models;

/// <summary>Readiness evaluation result for one provider connection.</summary>
public sealed record ProviderConnectionReadinessResult(
    ProviderConnectionReadinessLevel ReadinessLevel,
    string HostVariant,
    string ReadinessReason,
    IReadOnlyList<string> MissingCriteria,
    IReadOnlyList<ProviderReadinessCriterionResult> CriteriaResults);
