// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Tests.Services;

/// <summary>
///     Locks the default (flag-off) deduplicator to the exact token-set Jaccard behavior the synthesis inlet has
///     always applied: same-file collapse followed by cross-file consolidation, byte-for-byte identical to
///     <see cref="FindingDeduplicator.Deduplicate" /> composed over <see cref="FindingDeduplicator.CollapseSameFileDuplicates" />.
/// </summary>
public sealed class TokenJaccardFindingDeduplicatorTests
{
    [Fact]
    public async Task DeduplicateAsync_MatchesTheStaticTokenJaccardPipeline()
    {
        var comments = new List<ReviewComment>
        {
            new("src/A.cs", 10, CommentSeverity.Warning, "Null reference risk when the config is missing at startup."),
            new("src/A.cs", 11, CommentSeverity.Warning, "Null reference risk when the config is missing at startup."),
            new("src/B.cs", 20, CommentSeverity.Error, "Unvalidated redirect target taken directly from the request query state."),
            new("src/C.cs", 30, CommentSeverity.Error, "Unvalidated redirect target taken directly from the request query state."),
            new("src/D.cs", 40, CommentSeverity.Suggestion, "Extract the retry loop into a helper for readability."),
        };

        var expected = FindingDeduplicator.Deduplicate(FindingDeduplicator.CollapseSameFileDuplicates(comments));

        var actual = await new TokenJaccardFindingDeduplicator().DeduplicateAsync(comments, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(
            expected.Select(c => (c.FilePath, c.LineNumber, c.Severity, c.Message)),
            actual.Select(c => (c.FilePath, c.LineNumber, c.Severity, c.Message)));
    }
}
