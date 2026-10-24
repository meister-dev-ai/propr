// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.RegularExpressions;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Finds a statement in a finding's own text that its trigger is not established, for example "whether callers can
///     pass null is not shown in this diff". In adjudicated reviews, findings that state such a condition had a false
///     premise far more often than findings that did not, so the verifier treats the condition as an unresolved
///     assumption and asks for an independent check. The match reads the claim, not the repository, and covers English
///     wording only; a claim in another language is treated as stating no condition.
/// </summary>
internal static partial class ClaimTriggerHedge
{
    private const int MaxPhraseLength = 120;

    [GeneratedRegex(
        @"\b(?:is|are|was|were)\s+not\s+(?:shown|established|visible|evident)\b"
        + @"|\bthe\s+(?:diff|change|available\s+diff)\s+does\s+not\s+(?:show|establish)\b"
        + @"|\bnot\s+(?:shown|established|visible)\s+(?:in|by)\s+(?:this|the)\s+(?:diff|change|file)\b"
        + @"|\bdepends\s+on\s+(?:whether|how)\b"
        + @"|\bnot\s+defined\s+in\s+this\s+file\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpenTriggerPattern();

    /// <summary>
    ///     Returns the sentence of <paramref name="claimText" /> that leaves the trigger open, cut to a bounded length, or
    ///     <see langword="null" /> when the claim states no such condition.
    /// </summary>
    internal static string? FindOpenCondition(string? claimText)
    {
        if (string.IsNullOrWhiteSpace(claimText) || OpenTriggerPattern().Match(claimText) is not { Success: true } match)
        {
            return null;
        }

        var sentenceStart = 0;
        for (var index = match.Index - 1; index >= 0; index--)
        {
            if (EndsSentence(claimText, index))
            {
                sentenceStart = index + 1;
                break;
            }
        }

        var sentenceEnd = claimText.Length;
        for (var index = match.Index + match.Length; index < claimText.Length; index++)
        {
            if (EndsSentence(claimText, index))
            {
                sentenceEnd = index;
                break;
            }
        }

        var sentence = claimText[sentenceStart..sentenceEnd].Trim();
        return sentence.Length <= MaxPhraseLength ? sentence : sentence[..MaxPhraseLength].TrimEnd() + "…";
    }

    // A line break ends a sentence. A full stop, question mark or exclamation mark ends one only when whitespace or the
    // end of the text follows it, so the dot in a file name such as Claim.cs does not split the sentence.
    private static bool EndsSentence(string text, int index) =>
        text[index] == '\n'
        || (text[index] is '.' or '!' or '?' && (index + 1 == text.Length || char.IsWhiteSpace(text[index + 1])));
}
