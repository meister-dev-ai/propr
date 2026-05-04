// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Fixture-backed pull-request fetcher used by the offline review-evaluation harness.
/// </summary>
public sealed class FixturePullRequestFetcher(IReviewEvaluationFixtureAccessor fixtureAccessor) : IPullRequestFetcher
{
    public Task<PullRequest> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        int? compareToIterationId = null,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var fixture = fixtureAccessor.Fixture ?? throw new InvalidOperationException("No review evaluation fixture is active for this scope.");
        var snapshot = fixture.PullRequestSnapshot;

        if (!string.Equals(repositoryId, snapshot.CodeReview.Repository.ExternalRepositoryId, StringComparison.Ordinal)
            || pullRequestId != snapshot.CodeReview.Number)
        {
            throw new InvalidOperationException("The active fixture does not match the requested pull request identity.");
        }

        var pullRequest = new PullRequest(
            organizationUrl,
            projectId,
            repositoryId,
            fixture.RepositorySnapshot.RepositoryName ?? repositoryId,
            pullRequestId,
            iterationId,
            snapshot.Title,
            snapshot.Description,
            snapshot.SourceBranch,
            snapshot.TargetBranch,
            snapshot.ChangedFiles
                .Select(file => new ChangedFile(
                    file.Path,
                    file.ChangeType,
                    file.FullContent,
                    file.UnifiedDiff,
                    file.IsBinary,
                    file.OriginalPath))
                .ToList()
                .AsReadOnly(),
            PrStatus.Active,
            fixture.ThreadsOrEmpty
                .Select(thread => new PrCommentThread(
                    thread.ThreadId,
                    thread.FilePath,
                    thread.LineNumber,
                    thread.Comments
                        .Select(comment => new PrThreadComment(
                            comment.AuthorName,
                            comment.Content,
                            comment.AuthorId,
                            comment.CommentId,
                            comment.PublishedAt))
                        .ToList()
                        .AsReadOnly(),
                    thread.Status))
                .ToList()
                .AsReadOnly(),
            snapshot.AllChangedFileSummaries,
            snapshot.AuthorizedIdentityId);

        return Task.FromResult(pullRequest);
    }
}
