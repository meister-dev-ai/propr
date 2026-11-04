// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     Normalized model discovery payload returned by provider drivers.
/// </summary>
/// <param name="FailureCategory">
///     Why a failed discovery failed, in the categories of a verification, so a caller that does not show the
///     provider's own text can still name the cause. Null for a discovery that did not fail.
/// </param>
/// <param name="HttpStatus">The status a failed discovery was answered with, or null without one.</param>
public sealed record AiModelDiscoveryResultDto(
    string DiscoveryStatus,
    bool ManualEntryAllowed,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<AiConfiguredModelDto> Models,
    AiVerificationFailureCategory? FailureCategory = null,
    int? HttpStatus = null)
{
    /// <summary>
    ///     Models of the model catalog that the family can serve and the provider did not list, with the source
    ///     <c>knownCatalog</c>. Present also when the provider listed nothing or the discovery failed, and empty when
    ///     the catalog holds nothing for the family.
    /// </summary>
    public IReadOnlyList<AiConfiguredModelDto> CatalogModels { get; init; } = [];
}
