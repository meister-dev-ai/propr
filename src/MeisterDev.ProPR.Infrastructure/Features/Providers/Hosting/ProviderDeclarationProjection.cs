// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     Turns what a provider add-in declares about itself into what a console renders beside its configuration
///     fields: the family's name, the names of the shapes it speaks and authenticates with, and what it says
///     about the connection values the host collects for every family.
/// </summary>
/// <remarks>
///     <para>
///         A console that kept its own table of these would show a key instead of a name for every family
///         installed after the console shipped. Resolving them here also keeps one rule for what a name is: what
///         the family states, and the member name read at its word boundaries where it states nothing.
///     </para>
///     <para>
///         Every string here was written by the add-in, so each one is capped and scrubbed on the way out for the
///         reasons given on <see cref="ProviderDeclaredFieldProjection" />.
///     </para>
/// </remarks>
public static class ProviderDeclarationProjection
{
    /// <summary>The family's name, as a console shows it beside its key.</summary>
    /// <param name="declaration">What the add-in declares.</param>
    /// <param name="secrets">
    ///     The credential values the host holds for the connection this is rendered against, which the name is
    ///     scrubbed of. Empty where the declaration is described without a connection.
    /// </param>
    public static string Label(ProviderDeclaration declaration, IEnumerable<string>? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        // The key rather than an empty heading, so a family that declares a blank label still reads as itself.
        var label = Text(declaration.Label, secrets);
        return string.IsNullOrEmpty(label) ? declaration.Key : label;
    }

    /// <summary>The protocol modes this family speaks, each with the name an operator sees.</summary>
    /// <param name="declaration">What the add-in declares.</param>
    /// <param name="secrets">The credential values every name is scrubbed of.</param>
    public static IReadOnlyList<AiProtocolModeOptionDto> ProtocolModes(
        ProviderDeclaration declaration,
        IEnumerable<string>? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        var held = secrets?.ToList();
        var declared = declaration.ProtocolModes;

        return
        [
            .. declared.Supported.Select(mode => new AiProtocolModeOptionDto(
                mode,
                Stated(declared.Labels.GetValueOrDefault(mode), held) ?? FromModeName(mode))),
        ];
    }

    /// <summary>
    ///     The authentication modes this family authenticates with, each with the name an operator sees and whether
    ///     the family still reads it but no longer offers it.
    /// </summary>
    /// <param name="declaration">What the add-in declares.</param>
    /// <param name="secrets">The credential values every name is scrubbed of.</param>
    /// <remarks>
    ///     A superseded shape is reported flagged rather than left out. The family still reads it, so a profile
    ///     saved under it has to open on it and be named by it; dropping it here would leave a console with the
    ///     stored value and no name for it.
    /// </remarks>
    public static IReadOnlyList<AiAuthModeOptionDto> AuthModes(
        ProviderDeclaration declaration,
        IEnumerable<string>? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        var held = secrets?.ToList();

        return
        [
            .. declaration.AuthModes.Select(declared => new AiAuthModeOptionDto(
                declared.Mode,
                Stated(declared.Label, held) ?? FromModeName(declared.Mode),
                declared.Superseded)),
        ];
    }

    /// <summary>
    ///     What this family says about the display name, the base URL and the default query parameters, or null
    ///     where it says nothing about any of them.
    /// </summary>
    /// <param name="declaration">What the add-in declares.</param>
    /// <param name="secrets">The credential values every string is scrubbed of.</param>
    public static AiProviderConnectionFormDto? ConnectionForm(
        ProviderDeclaration declaration,
        IEnumerable<string>? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        if (declaration.ConnectionForm is not { } form)
        {
            return null;
        }

        var held = secrets?.ToList();

        return new AiProviderConnectionFormDto(
            Stated(form.NamePlaceholder, held),
            Stated(form.BaseUrlPlaceholder, held),
            Stated(form.BaseUrlHint, held),
            Stated(form.RequiredQueryParam, held),
            Stated(form.QueryParamPlaceholder, held));
    }

    /// <summary>
    ///     Reads a mode as an operator sees it: the qualifier dropped and the mode name read at its word
    ///     boundaries, so <c>meisterdev/anthropic:AnthropicMessages</c> becomes "Anthropic Messages".
    /// </summary>
    /// <param name="mode">The mode, qualified or reserved.</param>
    /// <remarks>
    ///     The qualifier says which family declared the mode, which the form already shows beside it, so a name
    ///     carrying it would repeat the family on every option.
    /// </remarks>
    public static string FromModeName(string mode)
    {
        ArgumentNullException.ThrowIfNull(mode);

        return FromMemberName(ProviderVocabulary.Split(mode).ModeName);
    }

    /// <summary>
    ///     Reads a name at its word boundaries, so <c>AnthropicMessages</c> becomes "Anthropic Messages".
    /// </summary>
    /// <param name="memberName">The name to read.</param>
    /// <remarks>
    ///     A space goes before a capital that starts a word: one following a lower-case letter or a digit, and
    ///     one that starts a word inside a run of capitals, so <c>XApiKey</c> becomes "X Api Key" rather than
    ///     "XApi Key". This is what a family overrides where the result is not how the shape is spelled.
    /// </remarks>
    public static string FromMemberName(string memberName)
    {
        ArgumentNullException.ThrowIfNull(memberName);

        if (memberName.Length == 0)
        {
            return memberName;
        }

        var spaced = new StringBuilder(memberName.Length + 8);
        spaced.Append(memberName[0]);

        for (var index = 1; index < memberName.Length; index++)
        {
            var current = memberName[index];
            var startsWord = char.IsUpper(current)
                             && (!char.IsUpper(memberName[index - 1])
                                 || (index + 1 < memberName.Length && char.IsLower(memberName[index + 1])));

            if (startsWord)
            {
                spaced.Append(' ');
            }

            spaced.Append(current);
        }

        return spaced.ToString();
    }

    /// <summary>
    ///     One family-authored string, capped and scrubbed, or null where the family stated nothing. A blank
    ///     string is not a statement, so it reads back as null and whatever falls back applies.
    /// </summary>
    private static string? Stated(string? value, IEnumerable<string>? secrets)
    {
        var text = Text(value, secrets);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string Text(string? value, IEnumerable<string>? secrets)
    {
        return ProviderMessageGuard.Sanitize(value, secrets, ProviderHostLimits.MaximumFieldTextLength);
    }
}
