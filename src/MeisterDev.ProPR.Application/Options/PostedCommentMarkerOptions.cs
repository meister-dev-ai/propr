// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel.DataAnnotations;
using MeisterDev.ProPR.CodeInsights.Contracts;

namespace MeisterDev.ProPR.Application.Options;

/// <summary>
///     The sentence ProPR puts at the end of every comment it posts, stating that the content was generated
///     by AI.
/// </summary>
/// <remarks>
///     Per installation, not per client: the statement is a property of the software that produced the text,
///     and an installation that has to answer for it needs one wording it can point at.
/// </remarks>
public sealed class PostedCommentMarkerOptions
{
    /// <summary>
    ///     The wording used when the installation has not configured one. Taken from the constant the
    ///     recall harvest matches against, so the posting side and the recognition side cannot drift apart.
    /// </summary>
    public const string DefaultMarker = HarvestedThreadEligibility.DefaultMarkerWording;

    /// <summary>
    ///     The configured wording, or <see langword="null" /> when the installation keeps
    ///     <see cref="DefaultMarker" />.
    /// </summary>
    /// <remarks>
    ///     The accepted characters are what every host renders unchanged. GitLab HTML-encodes the comment body,
    ///     so <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> and a double quote would reach the reader as entities. A
    ///     leading slash is a quick action on GitLab and would be executed instead of shown. The Markdown syntax
    ///     characters <c>*</c>, <c>_</c>, a backtick, <c>[</c>, <c>]</c> and <c>\</c> are rejected because the
    ///     composer wraps the wording in emphasis: one of them inside the wording turns the marker into a link,
    ///     into code or into differently placed emphasis, and the reader no longer sees the sentence the
    ///     installation configured. A leading number sign would make the marker a heading for the same reason.
    ///     The length bound keeps the marker small against the Azure DevOps comment cap, which the marker is
    ///     reserved from before a body is truncated. The pattern ends at <c>\z</c> and not at <c>$</c>, because
    ///     <c>$</c> also matches in front of a closing line break and would let a trailing newline through.
    ///     <para>
    ///         Control characters (<c>\p{Cc}</c>, which covers the carriage return and the line feed), format
    ///         characters (<c>\p{Cf}</c>) and the two Unicode separators <c>U+2028</c> and <c>U+2029</c> are
    ///         rejected together. A break of any of those kinds splits the marker away from the comment it
    ///         belongs to, and the invisible ones among them let a wording render as something other than the
    ///         characters it is stored as: a bidirectional override reverses the text a reviewer reads, and a
    ///         zero-width joiner leaves a stored marker that the recognition side matches and a reader does not
    ///         see.
    ///     </para>
    /// </remarks>
    [RegularExpression(
        """^(?![/#])[^<>&"\p{Cc}\p{Cf}\u2028\u2029*_`\[\]\\]{1,200}\z""",
        ErrorMessage =
            "MEISTER_AI_GENERATED_MARKER must be at most 200 characters on a single line, must not start with a "
            + "slash or a number sign, and must not contain <, >, &, a double quote, an asterisk, an underscore, "
            + "a backtick, a square bracket, a backslash, or a control, formatting or line-separator character.")]
    public string? Marker { get; set; }
}
