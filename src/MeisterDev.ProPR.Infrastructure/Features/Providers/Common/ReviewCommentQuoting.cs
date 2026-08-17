// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     Builds the quoted reply a provider without a native thread reply uses to answer a comment.
/// </summary>
/// <remarks>
///     Forgejo, and the conversation of a GitHub pull request, have no thread to reply into. The reply is a
///     new comment that opens with a markdown blockquote of the comment it answers, which is the form those
///     providers' own quote-reply buttons produce. Blockquotes nest, so quoting an answer in a follow-up
///     question keeps the sequence readable.
/// </remarks>
internal static class ReviewCommentQuoting
{
    /// <summary>
    ///     How much of the quoted comment is carried. A question long enough to exceed this is being quoted
    ///     for recognition, not for reading, and the answer is what the reader came for.
    /// </summary>
    private const int MaxQuotedCharacters = 800;

    /// <summary>How many lines of the quoted comment are carried, for the same reason.</summary>
    private const int MaxQuotedLines = 12;

    /// <summary>
    ///     Prefixes <paramref name="reply" /> with a blockquote of <paramref name="quotedComment" />, or
    ///     returns the reply unchanged when there is nothing to quote.
    /// </summary>
    internal static string BuildQuotedReply(string? quotedComment, string reply)
    {
        if (string.IsNullOrWhiteSpace(quotedComment))
        {
            return reply;
        }

        var builder = new StringBuilder();
        var lines = quotedComment.ReplaceLineEndings("\n").Split('\n');
        var carried = 0;
        var truncated = false;

        foreach (var line in lines)
        {
            if (carried >= MaxQuotedLines || builder.Length >= MaxQuotedCharacters)
            {
                truncated = true;
                break;
            }

            // An empty line inside a quote still needs its marker, or the block ends there and the rest of
            // the question renders as if the answer had said it.
            builder.Append("> ").Append(Shorten(line)).Append('\n');
            carried++;
        }

        if (truncated)
        {
            builder.Append("> …\n");
        }

        return builder.Append('\n').Append(reply).ToString();
    }

    private static string Shorten(string line)
    {
        return line.Length <= MaxQuotedCharacters ? line : line[..MaxQuotedCharacters] + "…";
    }
}
