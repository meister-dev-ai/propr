// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Domain.Enums;

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
///         substrings, so an account named after one of them without being it stays counted. The
///         provider-specific tests match the prefix or the bracketed form that provider itself uses.
///     </para>
///     <para>
///         A name the rules match that belongs to a person is left out of the count, which undercounts. The
///         rules are kept narrow for that reason: whole names rather than patterns, and provider-specific
///         shapes only on the provider that issues them.
///     </para>
/// </summary>
public static class AutomationAuthorPolicy
{
    /// <summary>
    ///     Azure DevOps names its built-in build identities this way. Collection-scoped identities are named
    ///     with the fixed prefix; project-scoped ones carry the project name first and the organization in
    ///     brackets last, so the bracketed form is required to close the name rather than to appear inside it.
    /// </summary>
    /// <remarks>
    ///     No Azure DevOps payload this codebase reads or replays carries a service identity, so these two
    ///     forms come from how the platform names those accounts and not from an observed payload. The list is
    ///     kept to them for that reason. An identity that reaches an installation under some other shape is
    ///     counted, which overcounts rather than undercounts.
    /// </remarks>
    private const string AzureDevOpsCollectionBuildServicePrefix = "project collection build service";

    private const string AzureDevOpsProjectBuildServiceFragment = " build service (";

    /// <summary>The identifier Forgejo and Gitea report for every deleted account.</summary>
    private const string ForgejoGhostUserId = "-1";

    /// <summary>The suffix GitHub gives app accounts, also used by automation on the other providers.</summary>
    private const string BotLoginSuffix = "[bot]";

    /// <summary>GitLab names its own service accounts with these login prefixes.</summary>
    private static readonly string[] GitLabServiceAccountPrefixes = ["project_bot_", "group_bot_"];

    /// <summary>
    ///     Automation identities that appear across providers, matched as whole names.
    ///     <para>
    ///         Names rather than patterns, because a pattern wide enough to catch a fork of one of these would
    ///         also catch people. A hosted instance that runs one of them under its own login is matched by the
    ///         bare name; the hosted GitHub apps carry the <c>[bot]</c> suffix and are matched by that instead.
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
    ///     A provider's bot flag is read as a positive statement only. The review side carries it from GitHub
    ///     alone, and the mention side stores it in a column that defaults to false, so a false or absent flag
    ///     means the provider stated nothing and the name tests still apply.
    /// </remarks>
    /// <param name="observation">The author and the signals the source row carries about them.</param>
    /// <returns><see langword="true" /> when a signal identifies automation.</returns>
    public static bool IsAutomation(AuthorActivityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.ProviderStatesBot == true)
        {
            return true;
        }

        var provider = observation.Host.Provider;

        // Forgejo and Gitea report one placeholder account for every deleted user, so the identifier names no
        // single person and counting it would report every deleted account as one author.
        if (provider == ScmProvider.Forgejo
            && string.Equals(observation.ExternalUserId.Trim(), ForgejoGhostUserId, StringComparison.Ordinal))
        {
            return true;
        }

        if (provider == ScmProvider.GitLab && HasGitLabServiceAccountPrefix(observation))
        {
            return true;
        }

        // The suffix is GitHub's convention for app accounts and is also used by automation on the other
        // providers. Two definitions of a GitHub bot exist in this codebase, the account type and this suffix,
        // and which one the provider adapter should report is decided elsewhere. Reading the suffix here
        // matches the automation identities the exclusion is about and changes neither that adapter nor that
        // decision.
        if (HasBotSuffix(observation.Login))
        {
            return true;
        }

        if (IsKnownAutomationName(observation.Login) || IsKnownAutomationName(observation.DisplayName))
        {
            return true;
        }

        return provider == ScmProvider.AzureDevOps && IsAzureDevOpsBuildServiceName(observation);
    }

    /// <summary>
    ///     Tests both names. The mention side stores the one name a comment payload carries as the login and
    ///     the display name alike, so testing the login alone would miss a service account seen there.
    /// </summary>
    private static bool HasGitLabServiceAccountPrefix(AuthorActivityObservation observation)
    {
        return HasAnyPrefix(observation.Login) || HasAnyPrefix(observation.DisplayName);

        static bool HasAnyPrefix(string? name)
        {
            var candidate = Normalize(name);

            return candidate is not null
                   && GitLabServiceAccountPrefixes.Any(prefix =>
                       candidate.StartsWith(prefix, StringComparison.Ordinal));
        }
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

    private static bool IsAzureDevOpsBuildServiceName(AuthorActivityObservation observation)
    {
        return IsBuildService(observation.DisplayName) || IsBuildService(observation.Login);

        static bool IsBuildService(string? name)
        {
            var candidate = Normalize(name);

            if (candidate is null)
            {
                return false;
            }

            if (candidate.StartsWith(AzureDevOpsCollectionBuildServicePrefix, StringComparison.Ordinal))
            {
                return true;
            }

            // Anchored on the closing bracket as well, so the shape has to be the whole tail of the name. A
            // person whose display name merely mentions a build service in the middle stays counted.
            return candidate.Contains(AzureDevOpsProjectBuildServiceFragment, StringComparison.Ordinal)
                   && candidate.EndsWith(')');
        }
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
