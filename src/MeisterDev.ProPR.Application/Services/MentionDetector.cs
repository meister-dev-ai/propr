// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
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
    ///     The supplied identity policy defines the native mention grammar and identifier comparison.
    /// </remarks>
    /// <param name="content">Raw comment content.</param>
    /// <param name="reviewerGuid">Normalized identity GUID of the reviewer to detect.</param>
    /// <returns><c>true</c> if the content mentions the reviewer; otherwise <c>false</c>.</returns>
    public static bool IsMentioned(string content, Guid reviewerGuid, IScmIdentityPolicy policy)
    {
        return IsMentioned(
            content,
            new ReviewerIdentity(new(policy.Provider, "https://localhost"), reviewerGuid.ToString(), reviewerGuid.ToString(), reviewerGuid.ToString(), false),
            policy);
    }

    /// <summary>
    ///     Returns <c>true</c> if <paramref name="content" /> contains a provider-native mention of
    ///     <paramref name="reviewer" />.
    /// </summary>
    public static bool IsMentioned(string content, ReviewerIdentity reviewer, IScmIdentityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(reviewer);

        var asked = StripQuotedLines(content);

        if (string.IsNullOrWhiteSpace(asked))
        {
            return false;
        }

        return policy.IsMentioned(asked, reviewer);
    }

    /// <summary>
    ///     Drops markdown blockquote lines, so a mention is read from what the comment says rather than from
    ///     what it repeats.
    /// </summary>
    /// <remarks>
    ///     Replies can quote earlier messages. Quoted mentions are removed so a previous question does not
    ///     create another reply job. The remaining text is checked regardless of author because a reviewer
    ///     account can also be used by a person asking a new question.
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
}
