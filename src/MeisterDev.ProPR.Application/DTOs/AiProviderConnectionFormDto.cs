// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     What a provider family says about the connection values the host collects for every family: the display
///     name, the base URL and the default query parameters.
/// </summary>
/// <remarks>
///     Absent for a family that states nothing about them, which leaves a console showing its own family-neutral
///     text. Every string was written by the family, so each one is capped and scrubbed before it is returned,
///     the same way a declared field's label is.
/// </remarks>
/// <param name="NamePlaceholder">Example text for the display-name box, or null when the family states none.</param>
/// <param name="BaseUrlPlaceholder">Example text for the base-URL box, or null when the family states none.</param>
/// <param name="BaseUrlHint">
///     Guidance under the base-URL box, saying what the address has to name, or null when the family states none.
/// </param>
/// <param name="RequiredQueryParam">
///     A query parameter this family cannot work without, so a console stops presenting the parameter box as
///     optional. Null for a family that needs none.
/// </param>
/// <param name="QueryParamPlaceholder">
///     Example text for the default-query-parameter box, or null when the family states none.
/// </param>
public sealed record AiProviderConnectionFormDto(
    string? NamePlaceholder,
    string? BaseUrlPlaceholder,
    string? BaseUrlHint,
    string? RequiredQueryParam,
    string? QueryParamPlaceholder);
