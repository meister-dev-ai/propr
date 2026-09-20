// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Declaration;

/// <summary>
///     What to tell an operator about the connection values the host collects for every family: the display
///     name, the base URL, and the default query parameters.
/// </summary>
/// <remarks>
///     <para>
///         These three are host columns rather than declared fields, so a family cannot describe them through
///         <see cref="ProviderDeclaration.Fields" />. They are also where the correct value differs most between
///         families: the same base-URL box takes a resource endpoint on one family and a regional host on
///         another, and the wrong one fails with a provider error that names neither the box nor the family.
///     </para>
///     <para>
///         Every member is optional, and a family that states none of them is described by the family-neutral
///         text a console falls back to. The credential boxes are not here: a family declares those as
///         <see cref="ProviderDeclaredAuthMode.CredentialFields" />, each with its own label and hint.
///     </para>
/// </remarks>
/// <param name="NamePlaceholder">Example text for the display-name box.</param>
/// <param name="BaseUrlPlaceholder">Example text for the base-URL box.</param>
/// <param name="BaseUrlHint">Guidance under the base-URL box, saying what the address has to name.</param>
/// <param name="RequiredQueryParam">
///     A query parameter this family cannot work without, named so a console stops presenting the parameter box
///     as optional. Null for a family that needs none.
/// </param>
/// <param name="QueryParamPlaceholder">Example text for the default-query-parameter box.</param>
public sealed record ProviderConnectionForm(
    string? NamePlaceholder = null,
    string? BaseUrlPlaceholder = null,
    string? BaseUrlHint = null,
    string? RequiredQueryParam = null,
    string? QueryParamPlaceholder = null);
