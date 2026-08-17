// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Services;

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
        var asked = StripQuotedLines(content);

        if (string.IsNullOrWhiteSpace(asked))
        {
            return false;
        }

        return asked.Contains($"@<{reviewerGuid}>", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Returns <c>true</c> if <paramref name="content" /> contains a provider-native mention of
    ///     <paramref name="reviewer" />.
    /// </summary>
    public static bool IsMentioned(string content, ReviewerIdentity reviewer)
    {
        ArgumentNullException.ThrowIfNull(reviewer);

        var asked = StripQuotedLines(content);

        if (string.IsNullOrWhiteSpace(asked))
        {
            return false;
        }

        return reviewer.Host.Provider switch
        {
            ScmProvider.AzureDevOps => Guid.TryParse(reviewer.ExternalUserId, out var reviewerGuid) &&
                                       asked.Contains($"@<{reviewerGuid}>", StringComparison.OrdinalIgnoreCase),
            ScmProvider.GitHub or ScmProvider.GitLab or ScmProvider.Forgejo => ContainsLoginMention(
                asked,
                reviewer.Login),
            _ => false,
        };
    }

    /// <summary>
    ///     Drops markdown blockquote lines, so a mention is read from what the comment says rather than from
    ///     what it repeats.
    /// </summary>
    /// <remarks>
    ///     Quoting is how a reply refers to an earlier message where the provider offers no thread, and it is
    ///     what ProPR's own answers do on GitHub and Forgejo. A quoted mention is therefore a repetition, not
    ///     a question: reading it as one would have an answer that quotes a question be taken for a new
    ///     question, answered, and quoted in turn, on every scan.
    ///     Keyed on the quote rather than on who wrote the comment, because the two are not the same thing. An
    ///     installation whose reviewer identity is an account a person also posts from would lose every real
    ///     question to an author check, and a person quoting an earlier message to ask something new is asking
    ///     something new whoever they are.
    /// </remarks>
    private static string StripQuotedLines(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        if (!content.Contains('>', StringComparison.Ordinal))
        {
            return content;
        }

        var kept = content
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Where(line => !IsQuotedLine(line));

        return string.Join('\n', kept);
    }

    /// <summary>
    ///     Reports whether a line opens a markdown blockquote. Markdown allows up to three spaces of
    ///     indentation before the marker, and nesting only repeats it.
    /// </summary>
    private static bool IsQuotedLine(string line)
    {
        var index = 0;
        while (index < line.Length && index < 3 && line[index] == ' ')
        {
            index++;
        }

        return index < line.Length && line[index] == '>';
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
