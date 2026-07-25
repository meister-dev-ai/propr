// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Reads repository instruction files from the offline fixture snapshot.
/// </summary>
public sealed class FixtureRepositoryInstructionFetcher(IReviewEvaluationFixtureAccessor fixtureAccessor)
    : IRepositoryInstructionFetcher
{
    private const string InstructionsFolder = ".meister-propr/";
    private const string InstructionsFilePrefix = "instructions-";

    public Task<IReadOnlyList<RepositoryInstruction>> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        string targetBranch,
        Guid? clientId,
        CancellationToken cancellationToken)
    {
        var fixture = fixtureAccessor.Fixture ?? throw new InvalidOperationException("No review evaluation fixture is active for this scope.");

        var instructions = fixture.RepositorySnapshot.Files
            .Where(file => file.Path.StartsWith(InstructionsFolder, StringComparison.OrdinalIgnoreCase))
            .Select(file => (FileName: Path.GetFileName(file.Path), file.Content))
            .Where(entry => entry.FileName.StartsWith(InstructionsFilePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(entry => RepositoryInstruction.Parse(entry.FileName, entry.Content))
            .Where(instruction => instruction is not null)
            .Cast<RepositoryInstruction>()
            .OrderBy(instruction => instruction.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();

        return Task.FromResult<IReadOnlyList<RepositoryInstruction>>(instructions);
    }
}
