// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.ReviewArchive;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Support;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetFileDiff;

/// <summary>
///     Handles <see cref="GetFileDiffQuery" /> by resolving the file path on the review job. When diff
///     retention is enabled for the owning connection and a diff has been retained for the file, the
///     stored copy is served; otherwise the diff is re-fetched on demand from the source control provider.
///     This is the diagnostics/UI read path only — it never alters the diff used during a live review.
/// </summary>
public sealed class GetFileDiffHandler(
    IJobRepository jobRepository,
    IPullRequestFetcher pullRequestFetcher,
    IReviewArchiveStore? reviewArchiveStore = null,
    IClientScmConnectionRepository? scmConnectionRepository = null)
{
    private const string ChangeTypeUnknown = "Unknown";

    /// <summary>
    ///     Returns the unified diff for the requested file result, or a <see cref="FileDiffDto" />
    ///     that explains why the diff cannot be rendered.
    /// </summary>
    public async Task<FileDiffDto> HandleAsync(GetFileDiffQuery query, CancellationToken ct = default)
    {
        var job = await jobRepository.GetByIdWithFileResultsAsync(query.JobId, ct).ConfigureAwait(false);
        if (job is null)
        {
            return new FileDiffDto(
                string.Empty,
                string.Empty,
                ChangeTypeUnknown,
                false,
                null,
                FileDiffAvailability.NotFound,
                $"Review job {query.JobId} was not found.");
        }

        var fileResult = job.FileReviewResults.FirstOrDefault(result => result.Id == query.FileResultId);
        if (fileResult is null)
        {
            return new FileDiffDto(
                string.Empty,
                string.Empty,
                ChangeTypeUnknown,
                false,
                null,
                FileDiffAvailability.NotFound,
                $"File result {query.FileResultId} was not found for review job {query.JobId}.");
        }

        var storedDiff = await this.TryGetStoredFileDiffAsync(job, fileResult.FilePath, ct).ConfigureAwait(false);
        if (storedDiff is not null)
        {
            return storedDiff;
        }

        try
        {
            var changedFile = await pullRequestFetcher.FetchFileDiffAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                fileResult.FilePath,
                clientId: job.ClientId,
                cancellationToken: ct).ConfigureAwait(false);

            if (changedFile is null)
            {
                return new FileDiffDto(
                    fileResult.FilePath,
                    string.Empty,
                    ChangeTypeUnknown,
                    false,
                    null,
                    FileDiffAvailability.NotFound,
                    $"File '{fileResult.FilePath}' was not found in the pull request's changed files.");
            }

            if (changedFile.IsBinary)
            {
                return new FileDiffDto(
                    changedFile.Path,
                    string.Empty,
                    MapChangeType(changedFile.ChangeType),
                    true,
                    changedFile.OriginalPath,
                    FileDiffAvailability.Binary,
                    "Binary files do not have a renderable diff.");
            }

            return new FileDiffDto(
                changedFile.Path,
                changedFile.UnifiedDiff,
                MapChangeType(changedFile.ChangeType),
                false,
                changedFile.OriginalPath,
                FileDiffAvailability.Available,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new FileDiffDto(
                fileResult.FilePath,
                string.Empty,
                ChangeTypeUnknown,
                false,
                null,
                FileDiffAvailability.ProviderUnavailable,
                $"The source control provider could not be reached. {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Attempts to serve the diff from the review-archive store. Returns null (so the caller falls back to
    // the remote fetch) when retention is not wired in, the owning connection has diff retention off, or no
    // diff has been retained for this file. This read path never touches the diff used during a live review.
    private async Task<FileDiffDto?> TryGetStoredFileDiffAsync(ReviewJob job, string filePath, CancellationToken ct)
    {
        if (reviewArchiveStore is null || scmConnectionRepository is null)
        {
            return null;
        }

        var host = job.ProviderHost;
        var connections = await scmConnectionRepository.GetByClientIdAsync(job.ClientId, ct).ConfigureAwait(false);
        var connection = RetentionConnectionResolver.Resolve(connections, host.Provider, host.HostBaseUrl);
        if (connection is null || !connection.StoreDiffs)
        {
            return null;
        }

        // Scope the stored lookup to the job's own revision — the same key the diff ingestion stored the
        // increment under. Passing null would return the newest retained diff across all iterations, which
        // can belong to a later revision than the one this job reviewed (the remote fallback below is also
        // scoped by the job's iteration).
        var revisionKey = ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId);
        var storedDiff = await reviewArchiveStore
            .GetFileDiffAsync(job.ClientId, job.RepositoryId, job.PullRequestId, revisionKey, filePath, ct)
            .ConfigureAwait(false);
        if (storedDiff is null)
        {
            return null;
        }

        if (storedDiff.IsBinary)
        {
            return new FileDiffDto(
                storedDiff.FilePath,
                string.Empty,
                MapRetainedChangeType(storedDiff.ChangeType),
                true,
                null,
                FileDiffAvailability.Binary,
                "Binary files do not have a renderable diff.");
        }

        return new FileDiffDto(
            storedDiff.FilePath,
            storedDiff.UnifiedDiff,
            MapRetainedChangeType(storedDiff.ChangeType),
            false,
            null,
            FileDiffAvailability.Available,
            null);
    }

    private static string MapChangeType(ChangeType changeType)
    {
        return changeType switch
        {
            ChangeType.Add => "Added",
            ChangeType.Edit => "Modified",
            ChangeType.Delete => "Deleted",
            ChangeType.Rename => "Renamed",
            _ => ChangeTypeUnknown,
        };
    }

    // Maps the persisted change-type token (as written by the retention ingestion path) onto the same
    // display values used for the remote-fetched diff.
    private static string MapRetainedChangeType(string changeType)
    {
        return changeType switch
        {
            "Added" => "Added",
            "Modified" => "Modified",
            "Deleted" => "Deleted",
            "Renamed" => "Renamed",
            _ => ChangeTypeUnknown,
        };
    }
}
