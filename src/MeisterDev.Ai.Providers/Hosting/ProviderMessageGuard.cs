// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;

namespace MeisterDev.Ai.Providers.Hosting;

/// <summary>
///     Caps and scrubs a string a provider add-in produced, before the host stores it or shows it to anyone.
/// </summary>
/// <remarks>
///     <para>
///         Every string an add-in returns is untrusted output. The cap keeps a provider's whole response body
///         from reaching a column or a log line; the scrub keeps a credential out of one. Both are applied by the
///         host at the boundary rather than left to each family, because a guarantee the product states
///         unconditionally cannot rest on the diligence of whoever wrote the family.
///     </para>
///     <para>
///         The scrub is a plain search for the values the host is holding at that moment, which is affordable
///         because it stops once its result is longer than the cap and the host already has the plaintext in hand.
///         It catches a credential echoed back verbatim, which is the case that happens: several providers return
///         part of the presented key in a refusal.
///     </para>
/// </remarks>
public static class ProviderMessageGuard
{
    /// <summary>The longest a message is kept when the caller states no bound of its own.</summary>
    public const int MaximumMessageLength = 1000;

    /// <summary>What a scrubbed value is replaced with.</summary>
    private const string Elision = "[redacted]";

    /// <summary>Marks a message that was cut short, so a reader knows the rest is not missing by accident.</summary>
    private const string Truncation = "…";

    /// <summary>Caps and scrubs one message.</summary>
    /// <param name="message">What the add-in produced.</param>
    /// <param name="secrets">The credential values the host holds for the connection the message is about.</param>
    /// <param name="maximumLength">The longest the result may be.</param>
    public static string Sanitize(
        string? message,
        IEnumerable<string>? secrets = null,
        int maximumLength = MaximumMessageLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, Truncation.Length + 1);

        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        // The scrub reads the message as it arrived and stops as soon as its result is longer than the cap. A
        // provider can answer with a body of any size, and each step of the scrub writes at least one character,
        // so the scrub takes at most one step more than the cap, whatever the size of the body. Each held value is
        // compared with the message as it arrived, so a value that reaches past the point where the scrub stops is
        // still matched whole.
        //
        // The input is not cut before the scrub. A cut there leaves the part of a value before the cut, which the
        // scrub does not match, and every elision earlier in the message shortens the text, because a key of 32
        // characters becomes the 10 characters of the elision text. That moves the part inside the cap.
        var scrubbed = Scrubbed(message, secrets, maximumLength);

        // Applied after the scrub: cutting first could leave a partial credential that the scrub no longer
        // matches.
        if (scrubbed.Length <= maximumLength)
        {
            return scrubbed;
        }

        var cut = maximumLength - Truncation.Length;

        // A cut between the halves of a surrogate pair emits a lone surrogate, which is not text: a text column
        // rejects it or stores a replacement character, and a JSON writer refuses the response it is in. Keeping
        // one character less keeps the pair whole.
        if (char.IsHighSurrogate(scrubbed[cut - 1]))
        {
            cut--;
        }

        return string.Concat(scrubbed.AsSpan(0, cut), Truncation);
    }

    /// <summary>
    ///     Elides every occurrence of a held value, in one pass over the message as it arrived, and stops once the
    ///     result is longer than <paramref name="maximumLength" />.
    /// </summary>
    /// <remarks>
    ///     One pass over the original text rather than one replacement per value, so what is written cannot be
    ///     read again: replacing a value puts the elision text into the message, and a value that occurs inside
    ///     that text would otherwise be matched in a later replacement and reported as a secret found in the
    ///     output. At each position the longest match wins, so a value that contains a shorter one is elided
    ///     whole rather than leaving the remainder of it in place.
    ///     <para>
    ///         The result is the scrubbed message, or a beginning of it that is longer than
    ///         <paramref name="maximumLength" />, so the caller still sees that the message was over the cap and
    ///         marks the cut.
    ///     </para>
    ///     <para>
    ///         Every value is elided for whatever its length. The callers pass credential material, or a value
    ///         that can hold it such as a default header, and a declared secret field states no minimum length, so
    ///         a short value that went unscrubbed could be a credential reaching a page. A short value that also
    ///         occurs in ordinary text costs that text its legibility, which is the cheaper of the two.
    ///     </para>
    /// </remarks>
    /// <param name="message">What the add-in produced, as it arrived.</param>
    /// <param name="secrets">The values to elide.</param>
    /// <param name="maximumLength">The longest the caller lets the result be.</param>
    private static string Scrubbed(string message, IEnumerable<string>? secrets, int maximumLength)
    {
        var held = (secrets ?? [])
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToList();

        // With nothing to elide, the message is its own scrubbed form, and the caller cuts it.
        if (held.Count == 0)
        {
            return message;
        }

        var builder = new StringBuilder(Math.Min(message.Length, maximumLength));
        var index = 0;
        while (index < message.Length && builder.Length <= maximumLength)
        {
            var matched = 0;
            foreach (var secret in held)
            {
                if (secret.Length <= message.Length - index
                    && string.CompareOrdinal(message, index, secret, 0, secret.Length) == 0)
                {
                    matched = secret.Length;
                    break;
                }
            }

            if (matched == 0)
            {
                builder.Append(message[index]);
                index++;
                continue;
            }

            builder.Append(Elision);
            index += matched;
        }

        return builder.ToString();
    }
}
