// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>Normalized lifecycle state for a provider-native review request.</summary>
public enum CodeReviewState
{
    /// <summary>Review is open.</summary>
    Open = 0,
    /// <summary>Review is in draft state.</summary>
    Draft = 1,
    /// <summary>Review has been merged.</summary>
    Merged = 2,
    /// <summary>Review is closed.</summary>
    Closed = 3,
}
