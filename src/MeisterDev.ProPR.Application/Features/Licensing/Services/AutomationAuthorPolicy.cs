// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Application.Features.Licensing.Services;

/// <summary>
///     Decides from an observation's own signals whether the author is automation rather than a person.
///     <para>
///         The rules are a fixed policy rather than configuration. A name list an installation could edit would
///         let it lower its own counted authors, and the count is what a license is measured against.
///     </para>
///     <para>
///         Two signal classes are covered here: what the provider states about the account, and what the
///         account is named. The third class, the identities ProPR itself is configured to act as, needs a
///         database read and is applied by the recorder that calls this.
///     </para>
///     <para>
///         Every name test is case-insensitive. The cross-provider names are matched whole rather than as
///         substrings, so an account named after one of them without being it stays counted.
///         Native account forms are interpreted by credential-free provider identity policies.
///     </para>
///     <para>
///         A name the rules match that belongs to a person is left out of the count, which undercounts. The
///         rules are kept narrow for that reason: whole names rather than patterns, and provider-specific
///         shapes only on the provider that issues them.
///     </para>
/// </summary>
public static class AutomationAuthorPolicy
{
    /// <summary>A fixed account-name suffix used by automation actors across providers.</summary>
    private const string BotLoginSuffix = "[bot]";

    /// <summary>
    ///     Automation identities that appear across providers, matched as whole names.
    ///     <para>
    ///         Names rather than patterns, because a pattern wide enough to catch a fork of one of these would
    ///         also catch people. A hosted instance that runs one of them under its own login is matched by the
    ///         bare name; account names with the <c>[bot]</c> suffix are matched by the suffix rule.
    ///     </para>
    /// </summary>
    private static readonly string[] KnownAutomationNames =
    [
        "dependabot",
        "dependabot-preview",
        "renovate",
        "renovate-bot",
        "renovate bot",
        "github-actions",
        "github actions",
        "release-please",
    ];

    /// <summary>
    ///     Returns whether the observed author is automation.
    /// </summary>
    /// <remarks>
    ///     A captured bot flag is read as a positive statement only. A false or absent flag supplies no
    ///     automation evidence, so the fixed account-name rules still apply.
    /// </remarks>
    /// <param name="observation">The author and the signals the source row carries about them.</param>
    /// <param name="nativeFacts">Native account facts supplied by the provider identity policy; the fixed exclusion decision remains here.</param>
    /// <returns><see langword="true" /> when a signal identifies automation.</returns>
    public static bool IsAutomation(AuthorActivityObservation observation, ScmNativeAccountFacts nativeFacts)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.ProviderStatesBot == true)
        {
            return true;
        }

        if (nativeFacts.IsDeletedAccount || nativeFacts.IsServiceAccount)
        {
            return true;
        }

        // The fixed suffix rule applies to automation actors across all provider families.
        if (HasBotSuffix(observation.Login))
        {
            return true;
        }

        if (IsKnownAutomationName(observation.Login) || IsKnownAutomationName(observation.DisplayName))
        {
            return true;
        }

        return nativeFacts.IsBuildService;
    }

    private static bool HasBotSuffix(string? login)
    {
        return Normalize(login)?.EndsWith(BotLoginSuffix, StringComparison.Ordinal) == true;
    }

    private static bool IsKnownAutomationName(string? name)
    {
        var candidate = Normalize(name);

        return candidate is not null
               && KnownAutomationNames.Any(known => string.Equals(candidate, known, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Trims and lower-cases a name for comparison, or returns null when the row carries none. Lower-casing
    ///     once here is what makes every rule below it case-insensitive against one form.
    /// </summary>
    private static string? Normalize(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim().ToLowerInvariant();
    }
}
