// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Baseline fixture validator for offline review execution.
/// </summary>
public sealed class ReviewEvaluationFixtureValidator : IReviewEvaluationFixtureValidator
{
    public Task ValidateAsync(ReviewEvaluationFixture fixture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (string.IsNullOrWhiteSpace(fixture.FixtureId))
        {
            throw new InvalidOperationException("Review evaluation fixture must define a fixture identifier.");
        }

        if (string.IsNullOrWhiteSpace(fixture.FixtureVersion))
        {
            throw new InvalidOperationException("Review evaluation fixture must define a fixture version.");
        }

        var snapshotPaths = fixture.RepositorySnapshot.Files
            .Select(file => file.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changedFilePaths = fixture.PullRequestSnapshot.ChangedFiles
            .Select(file => file.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        ValidateChangedFilesExistInSnapshot(fixture, snapshotPaths);

        var proRvExpectations = fixture.ProRVPrefilterExpectationsOrNull;
        if (proRvExpectations is null)
        {
            return Task.CompletedTask;
        }

        ValidateProRvPositiveExamples(proRvExpectations, changedFilePaths);

        return Task.CompletedTask;
    }

    private static void ValidateChangedFilesExistInSnapshot(ReviewEvaluationFixture fixture, HashSet<string> snapshotPaths)
    {
        foreach (var changedFile in fixture.PullRequestSnapshot.ChangedFiles)
        {
            if (changedFile.ChangeType == ChangeType.Delete)
            {
                continue;
            }

            if (!snapshotPaths.Contains(changedFile.Path))
            {
                throw new InvalidOperationException($"Fixture changed file '{changedFile.Path}' was not found in the repository snapshot.");
            }
        }
    }

    private static void ValidateProRvPositiveExamples(FixtureProRVPrefilterExpectations proRvExpectations, HashSet<string> changedFilePaths)
    {
        foreach (var example in proRvExpectations.PositiveExamplesOrEmpty)
        {
            if (string.IsNullOrWhiteSpace(example.Key))
            {
                throw new InvalidOperationException("Fixture ProRV prefilter expectations must define a non-empty key.");
            }

            if (string.IsNullOrWhiteSpace(example.FilePath))
            {
                throw new InvalidOperationException($"Fixture ProRV prefilter expectation '{example.Key}' must define a filePath.");
            }

            if (!changedFilePaths.Contains(example.FilePath))
            {
                throw new InvalidOperationException(
                    $"Fixture ProRV prefilter expectation '{example.Key}' references changed file '{example.FilePath}' that does not exist in pullRequestSnapshot.changedFiles.");
            }

            if (string.IsNullOrWhiteSpace(example.Description))
            {
                throw new InvalidOperationException($"Fixture ProRV prefilter expectation '{example.Key}' must define a description.");
            }

            if (example.ExpectedItemIdsOrEmpty.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException($"Fixture ProRV prefilter expectation '{example.Key}' must not contain empty expectedItemIds values.");
            }
        }
    }
}
