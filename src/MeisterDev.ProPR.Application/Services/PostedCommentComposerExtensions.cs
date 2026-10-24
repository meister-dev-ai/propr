// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;

namespace MeisterDev.ProPR.Application.Services;

/// <summary>Applies provider text rendering to the configured signature without changing generated markup.</summary>
public static class PostedCommentComposerExtensions
{
    /// <summary>Appends one safely rendered signature as the final paragraph.</summary>
    public static string AppendRendered(this IPostedCommentComposer composer, string? body, Func<string, string> renderText)
    {
        var unsigned = composer.StripRendered(body, renderText).TrimEnd();
        var marker = renderText(composer.Text.ReplaceLineEndings(" "));
        return string.IsNullOrWhiteSpace(unsigned) ? marker : unsigned + "\n\n" + marker;
    }

    /// <summary>Removes the current or default signature, including the provider's rendered form.</summary>
    public static string StripRendered(this IPostedCommentComposer composer, string? body, Func<string, string> renderText)
    {
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentNullException.ThrowIfNull(renderText);
        var unsigned = composer.Strip(body);
        if (string.IsNullOrWhiteSpace(unsigned))
        {
            return unsigned;
        }

        var trimmed = unsigned.TrimEnd();
        foreach (var marker in new[]
                 {
                     composer.Text,
                     renderText(composer.Text.ReplaceLineEndings(" ")),
                     renderText("*" + PostedCommentMarkerOptions.DefaultMarker + "*"),
                 })
        {
            if (trimmed == marker)
            {
                return string.Empty;
            }

            if (trimmed.EndsWith("\n" + marker, StringComparison.Ordinal))
            {
                return trimmed[..^marker.Length].TrimEnd();
            }
        }

        return unsigned;
    }
}
