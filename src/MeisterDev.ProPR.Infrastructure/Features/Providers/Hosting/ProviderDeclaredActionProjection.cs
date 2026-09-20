// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     Turns the operations a provider add-in declared into the affordances a console renders, capping and
///     scrubbing every string on the way out.
/// </summary>
/// <remarks>
///     The label, the input labels, the hints and the placeholders were all written by the add-in, so each is
///     untrusted output and goes out through the same cap and scrub as every other string it produces. What an
///     operator has to be told before starting an action is composed here too, because it depends on the
///     connection's configured values as well as on the declaration.
/// </remarks>
public static class ProviderDeclaredActionProjection
{
    /// <summary>The actions an add-in declares, as a console offers them against one connection.</summary>
    /// <param name="declaration">What the add-in declares.</param>
    /// <param name="providerSettings">The connection's declared configuration values, by declared field name.</param>
    /// <param name="secrets">
    ///     The credential values the host holds for the connection, which every string is scrubbed of.
    /// </param>
    public static IReadOnlyList<AiDeclaredActionDto> Describe(
        ProviderDeclaration declaration,
        IReadOnlyDictionary<string, string>? providerSettings = null,
        IEnumerable<string>? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        if (declaration.Actions.Count == 0)
        {
            return [];
        }

        var held = secrets?.ToList();

        return
        [
            .. declaration.Actions.Select(action => new AiDeclaredActionDto(
                declaration.Key,
                action.Id,
                ProviderMessageGuard.Sanitize(action.Label, held, ProviderHostLimits.MaximumFieldTextLength),
                ProviderDeclaredFieldProjection.Describe(action.Inputs, held),
                ProviderActionNotices.CoLocationNotice(declaration, action, providerSettings, held))),
        ];
    }
}
