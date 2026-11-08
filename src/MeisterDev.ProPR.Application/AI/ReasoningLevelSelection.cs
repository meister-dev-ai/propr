// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.ProPR.Application.AI;

/// <summary>
///     Chooses the reasoning level of one call: the first level set along the places that can choose one, restricted to
///     the levels the model accepts.
/// </summary>
/// <remarks>
///     <para>
///         The places that choose a level are the review pass, the purpose assignment, the logical model or, for a purpose
///         that resolves through a purpose binding, the binding, the client's baseline level for the baseline review pass,
///         and the configured model's default level. Each caller states them in its own order; nothing set means that the
///         provider applies its own default.
///     </para>
///     <para>
///         A model whose levels no source knows receives a level as configured, because no list says otherwise. A model
///         with a list receives only a level the list holds. A stored level can fall out of the list when an operator
///         points a logical model at another model or edits the list; the model then receives the nearest lower level of
///         the shared order that its list holds, or no level.
///     </para>
/// </remarks>
public static class ReasoningLevelSelection
{
    /// <summary>The first of the levels that is set, or null when none is.</summary>
    /// <param name="levels">The levels of the places, in the order they apply.</param>
    public static string? FirstSet(params string?[] levels)
    {
        foreach (var level in levels)
        {
            if (!string.IsNullOrWhiteSpace(level))
            {
                return level;
            }
        }

        return null;
    }

    /// <summary>The level a model receives for <paramref name="requested" />, or null when it receives none.</summary>
    /// <param name="requested">The level the places chose, or null.</param>
    /// <param name="acceptedLevels">The levels the model accepts, or null when no source knows them.</param>
    public static string? Sendable(string? requested, IReadOnlyList<string>? acceptedLevels)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return null;
        }

        if (acceptedLevels is null || acceptedLevels.Contains(requested, StringComparer.Ordinal))
        {
            return requested;
        }

        var rank = ProviderReasoningLevels.RankOf(requested);
        if (rank < 0)
        {
            return null;
        }

        for (var lower = rank - 1; lower >= 0; lower--)
        {
            var candidate = ProviderReasoningLevels.StandardOrder[lower];
            if (acceptedLevels.Contains(candidate, StringComparer.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    ///     The reason ProPR refuses a level an operator chose for a model, or null when it accepts it. A model with a list
    ///     accepts the levels the list holds; for a model whose levels no source knows, any level name is accepted.
    /// </summary>
    /// <param name="level">The level as the request states it, already trimmed and lowercased, or null.</param>
    /// <param name="acceptedLevels">The levels the model accepts, or null when no source knows them.</param>
    /// <param name="modelName">The model, for the message.</param>
    public static string? Refusal(string? level, IReadOnlyList<string>? acceptedLevels, string modelName)
    {
        if (level is null)
        {
            return null;
        }

        if (!ProviderReasoningLevels.IsValidName(level))
        {
            return $"'{level}' is not a reasoning level name. A level name has 1 to {ProviderReasoningLevels.MaxNameLength} "
                   + "characters, lowercase letters, digits, hyphens and underscores, and starts with a letter.";
        }

        if (acceptedLevels is null || acceptedLevels.Contains(level, StringComparer.Ordinal))
        {
            return null;
        }

        return acceptedLevels.Count == 0
            ? $"The model '{modelName}' accepts no reasoning level, so '{level}' cannot be chosen for it."
            : $"The model '{modelName}' does not accept the reasoning level '{level}'. It accepts {string.Join(", ", acceptedLevels)}.";
    }

    /// <summary>A level name as a request states it, trimmed and lowercased, or null for an empty value.</summary>
    /// <param name="level">The level as stated.</param>
    public static string? Normalize(string? level)
    {
        return string.IsNullOrWhiteSpace(level) ? null : level.Trim().ToLowerInvariant();
    }
}
