// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Reads repository exclusion rules from the offline fixture snapshot.
/// </summary>
public sealed class FixtureRepositoryExclusionFetcher(IReviewEvaluationFixtureAccessor fixtureAccessor)
    : IRepositoryExclusionFetcher
{
    private const string ExcludeFilePath = ".meister-propr/exclude";

    public Task<ReviewExclusionRules> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        string targetBranch,
        Guid? clientId,
        CancellationToken cancellationToken)
    {
        var fixture = fixtureAccessor.Fixture ?? throw new InvalidOperationException("No review evaluation fixture is active for this scope.");
        var file = fixture.RepositorySnapshot.Files.FirstOrDefault(entry => string.Equals(entry.Path, ExcludeFilePath, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            return Task.FromResult(ReviewExclusionRules.Default);
        }

        var patterns = file.Content
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrEmpty(line) && !line.StartsWith('#'))
            .ToList();

        return Task.FromResult(patterns.Count == 0 ? ReviewExclusionRules.Empty : ReviewExclusionRules.FromPatterns(patterns));
    }
}
