// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>The native review surface used by a provider.</summary>
public enum CodeReviewPlatformKind
{
    /// <summary>A pull request.</summary>
    PullRequest = 0,

    /// <summary>A merge request.</summary>
    MergeRequest = 1,
}
