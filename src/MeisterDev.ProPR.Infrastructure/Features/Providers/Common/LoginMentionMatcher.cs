// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Matches a supplied login token with complete identifier boundaries.</summary>
internal static class LoginMentionMatcher
{
    public static bool ContainsLoginMention(string content, string login)
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
