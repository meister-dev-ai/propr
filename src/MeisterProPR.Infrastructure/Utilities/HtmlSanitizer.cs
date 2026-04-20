// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;

namespace MeisterProPR.Infrastructure.Utilities;

/// <summary>
///     Sanitizes text content to prevent HTML injection in Azure DevOps comments.
///     Escapes dangerous HTML characters while preserving markdown formatting.
/// </summary>
internal static class HtmlSanitizer
{
    /// <summary>
    ///     Sanitizes the given text by escaping HTML metacharacters that could be interpreted
    ///     as HTML tags. This prevents tags like &lt;style&gt;, &lt;script&gt;, and other
    ///     HTML elements from being rendered in Azure DevOps comments.
    ///     Markdown formatting (bold, italic, code fences, etc.) is preserved since
    ///     they do not use angle brackets. Content inside markdown code blocks is also protected.
    /// </summary>
    /// <param name="input">The text to sanitize. Can be null or empty.</param>
    /// <returns>The sanitized text with HTML metacharacters escaped.</returns>
    internal static string Sanitize(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        // Escape < and > to prevent HTML injection.
        // This is safe because:
        // 1. Markdown formatting doesn't require angle brackets (**, *, `, [text](url), etc.)
        // 2. Code blocks use backticks (```), not angle brackets
        // 3. Azure DevOps will ignore escaped angle brackets in markdown
        return input
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
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

        // Replace in reverse order to avoid offset issues.
        // First replace > (no offset change), then < (no offset change).
        builder.Replace(">", "&gt;");
        builder.Replace("<", "&lt;");
    }
}
