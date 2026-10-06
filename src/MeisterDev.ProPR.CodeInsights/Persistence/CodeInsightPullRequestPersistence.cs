// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.CodeInsights.Persistence;

internal static class CodeInsightPullRequestPersistence
{
    internal static async Task<Guid?> FindPullRequestIdAsync(
        MeisterProPRDbContext db,
        CodeInsightPullRequestKey key,
        CancellationToken ct)
    {
        var match = await db.CodeInsightPullRequests
            .Where(candidate => candidate.ClientId == key.ClientId
                                && candidate.RepositoryId == key.RepositoryId
                                && candidate.PullRequestId == key.PullRequestId)
            .Select(candidate => (Guid?)candidate.Id)
            .FirstOrDefaultAsync(ct);

        return match;
    }

    internal static async Task<CodeInsightPullRequest> GetOrCreatePullRequestAsync(
        MeisterProPRDbContext db,
        CodeInsightPullRequestKey key,
        string? pullRequestState,
        DateTimeOffset? lastActivityAt,
        CancellationToken ct,
        string? repositoryName = null)
    {
        var pullRequest = await db.CodeInsightPullRequests
            .FirstOrDefaultAsync(
                candidate => candidate.ClientId == key.ClientId
                             && candidate.RepositoryId == key.RepositoryId
                             && candidate.PullRequestId == key.PullRequestId,
                ct);

        var now = DateTimeOffset.UtcNow;

        if (pullRequest is null)
        {
            pullRequest = new CodeInsightPullRequest
            {
                Id = Guid.CreateVersion7(),
                ClientId = key.ClientId,
                RepositoryId = key.RepositoryId,
                PullRequestId = key.PullRequestId,
                PullRequestState = pullRequestState ?? string.Empty,
                RepositoryName = Trimmed(repositoryName),
                LastActivityAt = lastActivityAt ?? now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.CodeInsightPullRequests.Add(pullRequest);
            return pullRequest;
        }

        if (pullRequestState is not null)
        {
            pullRequest.PullRequestState = pullRequestState;
        }

        // Only ever overwritten with a name somebody actually reported: a caller that does not know one (the
        // human-thread harvest, for instance) must not erase the name a review recorded.
        if (Trimmed(repositoryName) is { } name)
        {
            pullRequest.RepositoryName = name;
        }

        // The retention anchor only ever moves forward, so a late-arriving older observation cannot
        // shorten the window an aggregate has left.
        if (lastActivityAt is not null && lastActivityAt > pullRequest.LastActivityAt)
        {
            pullRequest.LastActivityAt = lastActivityAt.Value;
        }

        pullRequest.UpdatedAt = now;
        return pullRequest;
    }

    internal static string? Trimmed(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
