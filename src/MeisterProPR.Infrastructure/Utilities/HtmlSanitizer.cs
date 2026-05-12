// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Infrastructure.Utilities;

/// <summary>
///     Sanitizes text content to prevent HTML injection in rendered review comments.
///     Uses the built-in HTML encoder so unsafe HTML characters are escaped consistently.
/// </summary>
internal static class HtmlSanitizer
{
    private const char ZeroWidthSpace = '\u200B';

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
    ///     Renders text for provider display while preserving readable code-like content.
    ///     HTML-like markup is neutralized by breaking tag parsing rather than blanket-encoding
    ///     quotes and other safe-to-display characters.
    /// </summary>
    internal static RenderedReviewBody RenderForDisplay(string? input, ReviewBodyRenderingMode renderingMode)
    {
        var originalText = input ?? string.Empty;
        if (originalText.Length == 0)
        {
            return new RenderedReviewBody(
                originalText,
                string.Empty,
                renderingMode,
                Array.Empty<string>(),
                false);
        }

        var builder = new StringBuilder(originalText.Length);
        var transformed = false;

        for (var index = 0; index < originalText.Length; index++)
        {
            if (originalText[index] == '<' && ShouldNeutralizeTagStart(originalText, index))
            {
                builder.Append('<');
                builder.Append(ZeroWidthSpace);
                transformed = true;
                continue;
            }

            builder.Append(originalText[index]);
        }

        return new RenderedReviewBody(
            originalText,
            builder.ToString(),
            renderingMode,
            transformed ? ["neutralized_html_tag"] : Array.Empty<string>(),
            transformed);
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

    private static bool ShouldNeutralizeTagStart(string text, int index)
    {
        if (index >= text.Length - 1)
        {
            return false;
        }

        var next = text[index + 1];
        if (next is '!' or '/' or '?')
        {
            return HasTagClosure(text, index + 1);
        }

        if (!char.IsLetter(next))
        {
            return false;
        }

        return HasTagClosure(text, index + 1);
    }

    private static bool HasTagClosure(string text, int startIndex)
    {
        for (var index = startIndex; index < text.Length; index++)
        {
            if (text[index] == '>')
            {
                return true;
            }

            if (text[index] == '<' || text[index] == '\n' || text[index] == '\r')
            {
                return false;
            }
        }

        return false;
    }
}
