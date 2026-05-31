// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>Stable identifiers for published review pipeline profiles.</summary>
public static class ReviewPipelineProfileCatalog
{
    public const string FileByFileBaselineProfileId = "file-by-file-baseline";
    public const string FileByFileCalmProfileId = "file-by-file-calm";
    public const string FileByFileBalancedProfileId = "file-by-file-balanced";
    public const string FileByFileAssertiveProfileId = "file-by-file-assertive";
    public const string AgenticBaselineProfileId = "agentic-baseline";
    public const string AgenticExperimentalProfileId = "agentic-experimental";
    public const string PrWideBaselineProfileId = "pr-wide-baseline";
}
