// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Services;

/// <summary>
///     Pure-logic utility for detecting bot mentions in pull request comment content.
/// </summary>
public static class MentionDetector
{
    /// <summary>
    ///     Returns <c>true</c> if <paramref name="content" /> contains a mention of the
    ///     reviewer identified by <paramref name="reviewerGuid" />.
    /// </summary>
    /// <remarks>
    ///     ADO stores mentions as <c>@&lt;GUID&gt;</c> in raw comment text, e.g.
    ///     <c>@&lt;0CAEB875-08D2-6D69-88FB-302B06D21993&gt; What do you think?</c>
    ///     Matching is case-insensitive to handle both upper- and lower-case GUID representations.
    /// </remarks>
    /// <param name="content">Raw comment content.</param>
    /// <param name="reviewerGuid">VSS identity GUID of the reviewer to detect.</param>
    /// <returns><c>true</c> if the content mentions the reviewer; otherwise <c>false</c>.</returns>
    public static bool IsMentioned(string content, Guid reviewerGuid)
    {
        return content.Contains($"@<{reviewerGuid}>", StringComparison.OrdinalIgnoreCase);
    }
}
