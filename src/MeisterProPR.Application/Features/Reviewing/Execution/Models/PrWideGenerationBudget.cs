// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Hard caps applied to a job-level PR-wide review pass so its generation stays bounded. The pass never expands
///     to the whole repository: its file set is the change set plus the caller feed, and the caps below bound how much
///     investigation work it may spend.
/// </summary>
/// <param name="MaxInvestigations">
///     Maximum number of bounded investigation tasks the pass may run. Trims the plan before investigations execute.
/// </param>
/// <param name="MaxToolCallsPerInvestigation">Maximum bounded context-tool calls a single investigation may make.</param>
/// <param name="MaxSeedFilesPerInvestigation">Maximum seed files a single investigation may open before reasoning.</param>
public sealed record PrWideGenerationBudget(
    int MaxInvestigations,
    int MaxToolCallsPerInvestigation,
    int MaxSeedFilesPerInvestigation);
