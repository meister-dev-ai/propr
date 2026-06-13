// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetFileDiff;

/// <summary>
///     Handles <see cref="GetFileDiffQuery" /> by resolving the file path on the review job
///     and re-fetching the PR diff from the source control provider.
/// </summary>
public sealed class GetFileDiffHandler(
    IJobRepository jobRepository,
    IPullRequestFetcher pullRequestFetcher)
{
    private const string ChangeTypeUnknown = "Unknown";

    /// <summary>
    ///     Returns the unified diff for the requested file result, or a <see cref="FileDiffDto" />
    ///     that explains why the diff cannot be rendered.
    /// </summary>
    public async Task<FileDiffDto> HandleAsync(GetFileDiffQuery query, CancellationToken ct = default)
    {
        var job = jobRepository.GetByIdWithFileResultsAsync(query.JobId, ct).Result;
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
}
