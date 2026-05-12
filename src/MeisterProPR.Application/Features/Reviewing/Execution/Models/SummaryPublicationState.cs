// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Summary posting decision for a single publication pass.
/// </summary>
public sealed record SummaryPublicationState(
    string SummaryText,
    bool HasExistingBotSummary,
    bool IsIncrementalRun,
    bool ShouldPublishSummary,
    IReadOnlyList<string> CarriedForwardFilePaths);
