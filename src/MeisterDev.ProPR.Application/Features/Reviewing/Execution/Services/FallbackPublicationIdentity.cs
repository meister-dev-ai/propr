// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     The identity ProPR publishes a review or a notice under when the account of the client's connection is not
///     known.
/// </summary>
/// <remarks>
///     The external user id, the login and the display name of the fallback identity are a key built from the client,
///     the provider, the repository and the pull request. The key matches no account on the provider, so ProPR does not
///     take the comment of a person's account for its own, whatever that account is named. The heading of the review
///     summary is the same for every publication identity, so the key does not appear in it.
/// </remarks>
public static class FallbackPublicationIdentity
{
    /// <summary>Creates the fallback identity for the pull request of a review job.</summary>
    /// <param name="job">The review job whose result or notice is published.</param>
    /// <returns>An identity named by the key of the connection, which is not marked as a bot account.</returns>
    public static ReviewerIdentity For(ReviewJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var connectionKey = $"connection:{job.ClientId:D}:{job.Provider}:{job.RepositoryId}:{job.PullRequestId}";
        return new ReviewerIdentity(job.ProviderHost, connectionKey, connectionKey, connectionKey, isBot: false);
    }
}
