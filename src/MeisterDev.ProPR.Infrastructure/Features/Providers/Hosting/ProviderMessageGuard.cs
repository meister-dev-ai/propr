// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

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
///         because the message is short and the host already has the plaintext in hand. It catches a credential
///         echoed back verbatim, which is the case that happens: several providers return part of the presented
///         key in a refusal.
///     </para>
/// </remarks>
public static class ProviderMessageGuard
{
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
        int maximumLength = ProviderHostLimits.MaximumMessageLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, Truncation.Length + 1);

        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        // Bounded before the scrub, not after. A provider can answer with a body of any size, and scrubbing all
        // of it to keep the first thousand characters is work an untrusted party chooses the cost of. The head
        // kept is the cap plus the longest credential, so a credential straddling the cut is still matched whole
        // and the scrub-then-cut order below still holds.
        var bounded = Head(message, maximumLength, secrets);
        var scrubbed = Scrubbed(bounded, secrets);

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

    /// <summary>Elides every occurrence of a held value, in one pass over the message as it arrived.</summary>
    /// <remarks>
    ///     One pass over the original text rather than one replacement per value, so what is written cannot be
    ///     read again: replacing a value puts the elision text into the message, and a value that occurs inside
    ///     that text would otherwise be matched in a later replacement and reported as a secret found in the
    ///     output. At each position the longest match wins, so a value that contains a shorter one is elided
    ///     whole rather than leaving the remainder of it in place.
    ///     <para>
    ///         Every value is elided for whatever its length: the callers pass credential material and nothing
    ///         else, a declared secret field states no minimum length, and a short value that went unscrubbed
    ///         would be a credential reaching a page. A short one that also occurs in ordinary text costs that
    ///         text its legibility, which is the cheaper of the two.
    ///     </para>
    /// </remarks>
    private static string Head(string message, int maximumLength, IEnumerable<string>? secrets)
    {
        var longestSecret = 0;
        foreach (var secret in secrets ?? [])
        {
            if (!string.IsNullOrEmpty(secret) && secret.Length > longestSecret)
            {
                longestSecret = secret.Length;
            }
        }

        // One past the cap, so a message that was over it is still over it after this cut and the truncation
        // marker below is still appended. Cutting to exactly the cap made an over-long message look as though
        // it had fitted.
        // Saturating, because the three added together can pass int.MaxValue. Wrapping made keep negative and
        // the slice below threw for a message well under the cap.
        var keep = (int)Math.Min((long)maximumLength + longestSecret + 1, int.MaxValue);
        return message.Length <= keep ? message : message[..keep];
    }

    private static string Scrubbed(string message, IEnumerable<string>? secrets)
    {
        if (secrets is null)
        {
            return message;
        }

        var held = secrets
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToList();

        if (held.Count == 0)
        {
            return message;
        }

        var builder = new StringBuilder(message.Length);
        var index = 0;
        while (index < message.Length)
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
