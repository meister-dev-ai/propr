// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Services;

namespace MeisterDev.ProPR.CodeInsights.Tests;

/// <summary>
///     The AI-generated marker composer these tests read posted comments with.
/// </summary>
internal static class TestPostedCommentComposer
{
    /// <summary>The composer an installation that configured no wording of its own gets.</summary>
    internal static IPostedCommentComposer Default { get; } = With(null);

    /// <summary>A composer carrying <paramref name="wording" />, or the default wording when it is blank.</summary>
    internal static IPostedCommentComposer With(string? wording)
    {
        return new PostedCommentComposer(Microsoft.Extensions.Options.Options.Create(new PostedCommentMarkerOptions { Marker = wording }));
    }
}
