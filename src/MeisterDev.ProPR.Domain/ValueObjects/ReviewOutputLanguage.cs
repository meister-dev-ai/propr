// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.RegularExpressions;

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>
///     The natural language ProPR writes its reviewer-facing prose in, as an IETF BCP 47 language tag.
///     The language is configured per client and is never detected from the pull request, so the language of a
///     review is the same on every surface and does not vary with what the model happened to read.
///     Fixed labels ProPR itself renders around the model's prose stay English.
/// </summary>
public static partial class ReviewOutputLanguage
{
    /// <summary>The language every client starts on.</summary>
    public const string Default = "en";

    /// <summary>Longest accepted language tag, wide enough for a language, a script and a region subtag.</summary>
    public const int MaxTagLength = 16;

    /// <summary>
    ///     Whether <paramref name="tag" /> is a language tag ProPR accepts: a two or three letter primary
    ///     language subtag, optionally followed by hyphen-separated script, region or variant subtags.
    /// </summary>
    public static bool IsValidTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var trimmed = tag.Trim();

        return trimmed.Length <= MaxTagLength && TagPattern().IsMatch(trimmed);
    }

    /// <summary>
    ///     Returns the stored tag, or <see cref="Default" /> when nothing usable is stored. A client row written
    ///     before the setting existed, or one holding a blank value, reads as the default rather than as "unset",
    ///     so no caller has to decide what an absent language means.
    /// </summary>
    public static string Normalize(string? tag)
    {
        return IsValidTag(tag) ? tag!.Trim() : Default;
    }

    [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.None, 1000)]
    private static partial Regex TagPattern();
}
