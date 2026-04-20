// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Services;

/// <summary>
///     Pure-logic utility for detecting bot mentions in pull request comment content.
/// </summary>
public static class MentionDetector
{
    /// <summary>
    ///     Returns <c>true</c> if <paramref name="content" /> contains a mention of the
    ///     reviewer identified by <paramref name="reviewerGuid" />.
    /// </summary>
    /// <remarks>
    ///     ADO stores mentions as <c>@&lt;GUID&gt;</c> in raw comment text, e.g.
    ///     <c>@&lt;0CAEB875-08D2-6D69-88FB-302B06D21993&gt; What do you think?</c>
    ///     Matching is case-insensitive to handle both upper- and lower-case GUID representations.
    /// </remarks>
    /// <param name="content">Raw comment content.</param>
    /// <param name="reviewerGuid">VSS identity GUID of the reviewer to detect.</param>
    /// <returns><c>true</c> if the content mentions the reviewer; otherwise <c>false</c>.</returns>
    public static bool IsMentioned(string content, Guid reviewerGuid)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return content.Contains($"@<{reviewerGuid}>", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Returns <c>true</c> if <paramref name="content" /> contains a provider-native mention of
    ///     <paramref name="reviewer" />.
    /// </summary>
    public static bool IsMentioned(string content, ReviewerIdentity reviewer)
    {
        ArgumentNullException.ThrowIfNull(reviewer);

        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return reviewer.Host.Provider switch
        {
            ScmProvider.AzureDevOps => Guid.TryParse(reviewer.ExternalUserId, out var reviewerGuid) &&
                                       IsMentioned(content, reviewerGuid),
            ScmProvider.GitHub or ScmProvider.GitLab or ScmProvider.Forgejo => ContainsLoginMention(
                content,
                reviewer.Login),
            _ => false,
        };
    }

    private static bool ContainsLoginMention(string content, string login)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return false;
        }

        var mentionToken = $"@{login}";
        var searchIndex = 0;

        while (searchIndex < content.Length)
        {
            var mentionIndex = content.IndexOf(mentionToken, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (mentionIndex < 0)
            {
                return false;
            }

            if (HasValidMentionPrefix(content, mentionIndex) &&
                HasValidMentionSuffix(content, mentionIndex + mentionToken.Length))
            {
                return true;
            }

            searchIndex = mentionIndex + mentionToken.Length;
        }

        return false;
    }

    private static bool HasValidMentionPrefix(string content, int mentionIndex)
    {
        if (mentionIndex == 0)
        {
            return true;
        }

        return !IsLoginContinuationCharacter(content[mentionIndex - 1]);
    }

    private static bool HasValidMentionSuffix(string content, int suffixIndex)
    {
        if (suffixIndex >= content.Length)
        {
            return true;
        }

        var suffix = content[suffixIndex];
        if (suffix != '.')
        {
            return !IsLoginContinuationCharacter(suffix);
        }

        return suffixIndex == content.Length - 1 || !IsLoginContinuationCharacter(content[suffixIndex + 1]);
    }

    private static bool IsLoginContinuationCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value is '_' or '-' or '.';
    }
}
