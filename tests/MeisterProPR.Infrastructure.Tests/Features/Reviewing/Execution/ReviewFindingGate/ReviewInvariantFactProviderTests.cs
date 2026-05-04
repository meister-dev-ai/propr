// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.ReviewFindingGate;

public sealed class ReviewInvariantFactProviderTests
{
    [Fact]
    public void DomainReviewInvariantFactProvider_ReturnsReviewCommentMessageInvariant()
    {
        var sut = new DomainReviewInvariantFactProvider();

        var facts = sut.GetFacts();

        Assert.Contains(facts, fact => fact.InvariantId == DomainReviewInvariantFactProvider.ReviewResultCommentsRequiredInvariantId);

        var fact = Assert.Single(
            facts,
            candidate => candidate.InvariantId == DomainReviewInvariantFactProvider.ReviewCommentMessageRequiredInvariantId);
        Assert.Equal(DomainReviewInvariantFactProvider.ReviewCommentMessageRequiredInvariantId, fact.InvariantId);
        Assert.Equal(InvariantFact.DomainFamily, fact.Family);
        Assert.Equal("ReviewComment constructor semantics", fact.Source);
        Assert.Equal("message_non_null_and_non_empty", fact.FactValue);
    }

    [Fact]
    public void PersistenceReviewInvariantFactProvider_ReturnsReviewFileResultsUniquenessInvariant()
    {
        var sut = new PersistenceReviewInvariantFactProvider();

        var facts = sut.GetFacts();

        var fact = Assert.Single(facts);
        Assert.Equal(PersistenceReviewInvariantFactProvider.ReviewFileResultsUniqueJobPathInvariantId, fact.InvariantId);
        Assert.Equal(InvariantFact.PersistenceFamily, fact.Family);
        Assert.Equal("EF metadata / review_file_results unique index", fact.Source);
        Assert.Equal("unique(job_id,file_path)", fact.FactValue);
    }
}
