// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     One structured repository-overview section.
/// </summary>
public sealed record RepositoryOverviewSection(
    string Name,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Notes);

/// <summary>
///     Structured branch-specific map of repository signals used during review investigation.
/// </summary>
public sealed record RepositoryOverview(
    string Status,
    string BranchSide,
    string Branch,
    RepositoryOverviewSection Projects,
    RepositoryOverviewSection EntryPoints,
    RepositoryOverviewSection ModuleBoundaries,
    RepositoryOverviewSection TestLocations,
    RepositoryOverviewSection ConfigTouchpoints,
    RepositoryOverviewSection PersistencePaths,
    RepositoryOverviewSection RegistrationLocations,
    RepositoryOverviewSection DocsAndSpecs,
    IReadOnlyList<RepositorySearchLimitation> Limitations,
    bool Truncated,
    IReadOnlyList<ProtocolEventPhaseTiming>? PhaseTimings = null)
    : IToolExecutionTimingCarrier
{
    /// <summary>Creates a blocked overview result that remains serializable and auditable.</summary>
    public static RepositoryOverview CreateBlocked(string branchSide, string status)
    {
        return new RepositoryOverview(
            status,
            branchSide,
            string.Empty,
            EmptySection("projects"),
            EmptySection("entry_points"),
            EmptySection("module_boundaries"),
            EmptySection("test_locations"),
            EmptySection("config_touchpoints"),
            EmptySection("persistence_paths"),
            EmptySection("registration_locations"),
            EmptySection("docs_and_specs"),
            [],
            false);
    }

    public static RepositoryOverviewSection EmptySection(string name)
    {
        return new RepositoryOverviewSection(name, [], []);
    }
}

/// <summary>
///     Focused branch-specific neighborhood for one repository-relative file.
/// </summary>
public sealed record FileNeighborhood(
    string Status,
    string BranchSide,
    string Branch,
    string FilePath,
    string? OwningProjectOrModule,
    IReadOnlyList<string> NearbyTests,
    IReadOnlyList<string> ConfigTouchpoints,
    IReadOnlyList<string> RegistrationLocations,
    IReadOnlyList<string> DocsAndSpecs,
    IReadOnlyList<RepositorySearchLimitation> Limitations,
    bool Truncated,
    IReadOnlyList<ProtocolEventPhaseTiming>? PhaseTimings = null)
    : IToolExecutionTimingCarrier
{
    /// <summary>Creates a blocked neighborhood result that remains serializable and auditable.</summary>
    public static FileNeighborhood CreateBlocked(string branchSide, string filePath, string status)
    {
        return new FileNeighborhood(
            status,
            branchSide,
            string.Empty,
            filePath,
            null,
            [],
            [],
            [],
            [],
            [],
            false);
    }
}
