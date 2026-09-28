// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Puts the AI-generated marker on the text ProPR posts to a pull request, and takes it off again where
///     text has to be compared against comments written before the marker existed.
/// </summary>
/// <remarks>
///     Every host family builds its own comment bodies, so the wording and its rendering live here and the
///     adapters call this as the last step of building a body.
/// </remarks>
public interface IPostedCommentComposer
{
    /// <summary>
    ///     The marker as it appears at the end of a posted body, including the emphasis around it.
    /// </summary>
    string Text { get; }

    /// <summary>
    ///     Returns <paramref name="body" /> with the marker as its last line, separated by a blank line.
    /// </summary>
    /// <remarks>
    ///     A body that already ends with the marker is returned unchanged, so an adapter that composes a body
    ///     from parts that were marked already does not stack markers. A <see langword="null" />, empty or
    ///     whitespace-only body returns the marker on its own, without a leading blank line: an adapter that
    ///     has nothing to say still posts text that states where it came from.
    /// </remarks>
    string Append(string? body);

    /// <summary>
    ///     Returns <paramref name="body" /> without a trailing marker, for comparing it with a body that was
    ///     posted before the marker existed.
    /// </summary>
    /// <remarks>
    ///     The wording an installation configured and the default wording are both removed, so changing the
    ///     wording does not make older comments stop matching. A <see langword="null" /> body returns an empty
    ///     string, so a caller comparing two bodies never has to handle null itself; a body that holds only
    ///     whitespace is returned as it came in, because there is no marker on it to take off.
    /// </remarks>
    string Strip(string? body);
}
