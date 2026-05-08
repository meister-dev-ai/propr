// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;

namespace MeisterProPR.Infrastructure.Utilities;

/// <summary>
///     Sanitizes text content to prevent HTML injection in rendered review comments.
///     Uses the built-in HTML encoder so unsafe HTML characters are escaped consistently.
/// </summary>
internal static class HtmlSanitizer
{
    /// <summary>
    ///     Sanitizes the given text with the framework HTML encoder before it is rendered.
    ///     This prevents tags like &lt;style&gt;, &lt;script&gt;, and other HTML fragments from
    ///     being interpreted by downstream renderers.
    /// </summary>
    /// <param name="input">The text to sanitize. Can be null or empty.</param>
    /// <returns>The sanitized text with HTML metacharacters escaped.</returns>
    internal static string Sanitize(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        return WebUtility.HtmlEncode(input);
    }

    /// <summary>
    ///     Sanitizes the given stringbuilder's content in-place by escaping HTML metacharacters.
    ///     More efficient than Sanitize(string) for large content being built progressively.
    /// </summary>
    /// <param name="builder">The StringBuilder to sanitize in-place. Cannot be null.</param>
    internal static void SanitizeInPlace(StringBuilder builder)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        var encoded = WebUtility.HtmlEncode(builder.ToString());
        builder.Clear().Append(encoded);
    }
}
