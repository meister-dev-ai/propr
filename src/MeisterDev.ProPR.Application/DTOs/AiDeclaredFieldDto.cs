// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     What has to hold for a declared field to be shown.
/// </summary>
/// <param name="FieldName">The field whose value decides.</param>
/// <param name="EqualsValue">The value that makes the dependent field visible.</param>
public sealed record AiDeclaredFieldVisibilityDto(string FieldName, string EqualsValue);

/// <summary>
///     The current value of one read-only computed field, as of this read.
/// </summary>
/// <remarks>
///     Recomputed on every read and stored nowhere, because it is composed from other fields and a stored copy
///     stops matching the moment one of them changes. It is checked where it is rendered rather than where it
///     would be stored, since it never is; a value the installation's egress rules refuse is reported here with
///     the reason and leaves the rest of the form usable.
/// </remarks>
/// <param name="Name">The declared field the value belongs to.</param>
/// <param name="Value">The value as the family computed it, capped and scrubbed.</param>
/// <param name="Refusal">
///     Why this installation would refuse the value, or null when it permits it.
/// </param>
public sealed record AiComputedFieldDto(string Name, string Value, string? Refusal = null);

/// <summary>
///     One configuration value a provider family asks an operator for, described well enough for a console to
///     render it without knowing anything about the family.
/// </summary>
/// <remarks>
///     Every string here was written by the family, so each one is capped and scrubbed before it is returned.
///     A family that declares no fields reports an empty set, which the families compiled into this build
///     do: their address, credential and verification state are columns of their own.
/// </remarks>
/// <param name="Name">The key the value is submitted, stored and read back under.</param>
/// <param name="Label">What an operator sees where the value is entered.</param>
/// <param name="Kind">The value shape, from the closed vocabulary a console renders.</param>
/// <param name="IsRequired">Whether the connection can be saved without it.</param>
/// <param name="IsSecret">Whether the value is credential material, which a console masks and never shows.</param>
/// <param name="IsComputed">
///     Whether the host computes the value and shows it read-only. A computed value is recomputed wherever it is
///     shown and is never submitted or stored.
/// </param>
/// <param name="Hint">Guidance shown under the input, for a field whose label is not enough on its own.</param>
/// <param name="Placeholder">Example text shown in the empty input.</param>
/// <param name="DefaultValue">The value a new connection starts with.</param>
/// <param name="Choices">The options a choice field offers; empty for every other shape.</param>
/// <param name="VisibleWhen">What has to hold for the field to be shown, or null for one always shown.</param>
public sealed record AiDeclaredFieldDto(
    string Name,
    string Label,
    ProviderFieldKind Kind,
    bool IsRequired,
    bool IsSecret,
    bool IsComputed,
    string? Hint = null,
    string? Placeholder = null,
    string? DefaultValue = null,
    IReadOnlyList<string>? Choices = null,
    AiDeclaredFieldVisibilityDto? VisibleWhen = null)
{
    /// <summary>
    ///     The values a Choice field offers, empty for every other shape.
    /// </summary>
    /// <remarks>
    ///     Empty rather than null, because the contract describes a collection and a client that trusted it
    ///     dereferenced null on any field that is not a choice.
    /// </remarks>
    public IReadOnlyList<string> Choices { get; init; } = Choices ?? [];
}
