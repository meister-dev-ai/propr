// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     Turns a provider add-in's declared configuration into what a console renders, capping and scrubbing every
///     string on the way out.
/// </summary>
/// <remarks>
///     Each of these strings was written by the add-in, which makes every one of them untrusted output: a label,
///     a hint, a placeholder and a default are all values the host did not author and has not checked. They are
///     capped so a declaration cannot push a form off the screen, and scrubbed against the credential values the
///     host holds for the connection so an add-in cannot echo one back through a field description.
/// </remarks>
public static class ProviderDeclaredFieldProjection
{
    /// <summary>The connection configuration an add-in declares, as a console renders it.</summary>
    /// <param name="declaration">What the add-in declares.</param>
    /// <param name="secrets">
    ///     The credential values the host holds for the connection this is rendered against, which every string is
    ///     scrubbed of. Empty where the declaration is described without a connection, which the
    ///     permitted-provider list does.
    /// </param>
    /// <remarks>
    ///     Action inputs are not here. They belong to one action invocation, are never persisted, and are
    ///     collected where the action is started rather than on the connection form.
    /// </remarks>
    public static IReadOnlyList<AiDeclaredFieldDto> Describe(
        ProviderDeclaration declaration,
        IEnumerable<string>? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        return Describe(declaration.ConnectionFields, secrets);
    }

    /// <summary>One set of declared fields, as a console renders it.</summary>
    /// <param name="fields">
    ///     The fields to describe. This is what an action's declared inputs go through, and what a form an action
    ///     asked for goes through, so a value collected for one invocation is rendered by the same rules as a
    ///     value saved on the connection.
    /// </param>
    /// <param name="secrets">
    ///     The credential values the host holds for the connection this is rendered against, which every string
    ///     is scrubbed of.
    /// </param>
    public static IReadOnlyList<AiDeclaredFieldDto> Describe(
        IReadOnlyList<ProviderDeclaredField> fields,
        IEnumerable<string>? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var held = secrets?.ToList();

        return
        [
            .. fields.Select(field => new AiDeclaredFieldDto(
                field.Name,
                // The fallback goes through the same bounded, scrubbed rendering as the label it stands in for.
                // Using the raw name put a family-authored string on the page uncapped and unscrubbed, while the
                // name above it stays raw because the form binds values by it.
                Text(field.Label, held) ?? Text(field.Name, held) ?? string.Empty,
                field.Kind,
                field.IsRequired,
                field.IsSecret,
                field.IsComputed,
                Text(field.Hint, held),
                Text(field.Placeholder, held),
                Text(field.DefaultValue, held),
                [.. field.Choices.Select(choice => Text(choice, held) ?? string.Empty)],
                // The condition is two family-authored strings like every other one here. The value it compares
                // against is the one most likely to carry something a family should not be returning, because a
                // family is free to name a secret field as the deciding one.
                field.VisibleWhen is { } visibility
                    ? new AiDeclaredFieldVisibilityDto(
                        Text(visibility.FieldName, held) ?? string.Empty,
                        Text(visibility.EqualsValue, held) ?? string.Empty)
                    : null)),
        ];
    }

    private static string? Text(string? value, IEnumerable<string>? secrets)
    {
        return value is null
            ? null
            : ProviderMessageGuard.Sanitize(value, secrets, ProviderHostLimits.MaximumFieldTextLength);
    }
}
