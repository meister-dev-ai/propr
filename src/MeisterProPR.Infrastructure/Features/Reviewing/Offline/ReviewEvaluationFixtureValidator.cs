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

        foreach (var changedFile in fixture.PullRequestSnapshot.ChangedFiles)
        {
            if (changedFile.ChangeType == ChangeType.Delete)
            {
                continue;
            }

            if (!snapshotPaths.Contains(changedFile.Path))
            {
                throw new InvalidOperationException(
                    $"Fixture changed file '{changedFile.Path}' was not found in the repository snapshot.");
            }
        }

        return Task.CompletedTask;
    }
}
