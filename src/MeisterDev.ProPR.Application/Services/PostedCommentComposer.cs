// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Application.Services;

/// <summary>
///     Default <see cref="IPostedCommentComposer" />: the configured wording, or the default one when the
///     installation did not set it, rendered as an emphasized line of its own.
/// </summary>
public sealed class PostedCommentComposer : IPostedCommentComposer
{
    private readonly string defaultMarker = Render(PostedCommentMarkerOptions.DefaultMarker);

    /// <summary>
    ///     Initializes the composer from the installation's marker wording.
    /// </summary>
    public PostedCommentComposer(IOptions<PostedCommentMarkerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configured = options.Value.Marker;
        this.Text = string.IsNullOrWhiteSpace(configured) ? this.defaultMarker : Render(configured.Trim());
    }

    /// <inheritdoc />
    public string Text { get; }

    /// <inheritdoc />
    public string Append(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return this.Text;
        }

        var trimmed = body.TrimEnd();
        return this.LastLineIsMarker(trimmed) ? body : trimmed + "\n\n" + this.Text;
    }

    /// <inheritdoc />
    public string Strip(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return body ?? string.Empty;
        }

        var trimmed = body.TrimEnd();
        if (!this.LastLineIsMarker(trimmed))
        {
            return body;
        }

        var lastBreak = trimmed.LastIndexOf('\n');
        return lastBreak < 0 ? string.Empty : trimmed[..lastBreak].TrimEnd();
    }

    private static string Render(string wording)
    {
        return "*" + wording + "*";
    }

    /// <summary>
    ///     Whether the last line of <paramref name="trimmedBody" /> is a marker and nothing else. A body that
    ///     quotes a marked comment carries the marker inside a quoted line, which this does not match.
    /// </summary>
    private bool LastLineIsMarker(string trimmedBody)
    {
        var lastBreak = trimmedBody.LastIndexOf('\n');
        var lastLine = (lastBreak < 0 ? trimmedBody : trimmedBody[(lastBreak + 1)..]).Trim();

        return string.Equals(lastLine, this.Text, StringComparison.Ordinal)
               || string.Equals(lastLine, this.defaultMarker, StringComparison.Ordinal);
    }
}
