// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Tests;

/// <summary>
///     The identities the adapter tests publish a review under.
/// </summary>
internal static class TestPublicationIdentity
{
    /// <summary>
    ///     Resolves the identity a review is published under, as the review orchestration does: the bot account the pull
    ///     request fetch returned, or the fallback identity when the fetch returned no account. The login and the display
    ///     name of the fallback identity are a key built from the client, the provider, the repository and the pull request.
    /// </summary>
    /// <param name="account">The account the fetch returned, or <see langword="null" /> for none.</param>
    /// <param name="clientId">The client whose connection published the review.</param>
    /// <param name="review">The pull request the review is published on.</param>
    internal static ReviewerIdentity For(string? account, Guid clientId, CodeReviewRef review)
    {
        if (account is not null)
        {
            return new ReviewerIdentity(review.Repository.Host, account, account, account, isBot: true);
        }

        var job = new ReviewJob(
            review,
            Guid.NewGuid(),
            clientId,
            review.Repository.Host.HostBaseUrl,
            review.Repository.OwnerOrNamespace,
            review.Repository.ExternalRepositoryId,
            review.Number,
            iterationId: 1);
        return FallbackPublicationIdentity.For(job);
    }
}
