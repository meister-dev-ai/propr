// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Services;

namespace MeisterDev.ProPR.Infrastructure.Tests;

/// <summary>
///     The AI-generated marker composer the adapter tests post with.
/// </summary>
internal static class TestPostedCommentComposer
{
    /// <summary>
    ///     A wording no installation default uses. A body carrying it can only have come from the composer the
    ///     test injected, so an adapter that built the marker itself or ignored the composer fails the assertion.
    /// </summary>
    internal const string DistinctiveWording = "Posted by the adapter test, not by a person.";

    /// <summary>
    ///     What <see cref="Distinctive" /> renders, spelled out here so an assertion states the body it expects
    ///     instead of reading it back off the composer under test.
    /// </summary>
    internal const string DistinctiveMarker = "*" + DistinctiveWording + "*";

    /// <summary>The composer an installation that configured no wording of its own gets.</summary>
    internal static IPostedCommentComposer Default { get; } = With(null);

    /// <summary>The composer carrying <see cref="DistinctiveWording" />.</summary>
    internal static IPostedCommentComposer Distinctive { get; } = With(DistinctiveWording);

    /// <summary>A composer carrying <paramref name="wording" />, or the default wording when it is blank.</summary>
    internal static IPostedCommentComposer With(string? wording)
    {
        return new PostedCommentComposer(Microsoft.Extensions.Options.Options.Create(new PostedCommentMarkerOptions { Marker = wording }));
    }

    /// <summary>
    ///     Asserts that <paramref name="marker" /> is the last line of <paramref name="body" /> and appears on no
    ///     other line. A body that merely contains the marker somewhere would still read as ProPR's words placed
    ///     in the middle of somebody else's, and a body carrying it twice comes from an adapter that marked a
    ///     part and then marked the whole again.
    /// </summary>
    internal static void AssertMarkedOnce(string? body, string marker)
    {
        Assert.NotNull(body);

        // A body ending in the conventional final line break would otherwise split into a trailing empty
        // element, which is not the marker line and is not a duplicate of it either; trimming the terminal
        // break first keeps the marker as the last element regardless of whether the caller's body has one.
        var normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        var lines = normalized.Split('\n');
        Assert.Equal(marker, lines[^1]);
        Assert.Single(lines, line => string.Equals(line, marker, StringComparison.Ordinal));
    }
}
