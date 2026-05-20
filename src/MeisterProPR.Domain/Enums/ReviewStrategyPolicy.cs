// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>Defines which persisted review strategies are currently active for new review selection and execution.</summary>
public static class ReviewStrategyPolicy
{
    /// <summary>Returns <see langword="true" /> when the strategy can be selected for new reviews.</summary>
    public static bool IsSelectable(ReviewStrategy strategy)
    {
        return strategy == ReviewStrategy.FileByFile;
    }

    /// <summary>Returns a user-facing validation message for disabled strategies.</summary>
    public static string GetDisabledSelectionMessage(ReviewStrategy strategy)
    {
        return $"Review strategy '{strategy}' is currently disabled. Only '{ReviewStrategy.FileByFile}' is selectable for new reviews.";
    }

    /// <summary>Returns a runtime failure message for persisted jobs that still reference a disabled strategy.</summary>
    public static string GetDisabledExecutionMessage(ReviewStrategy strategy)
    {
        return $"Review strategy '{strategy}' is currently disabled and cannot execute. Requeue the review with '{ReviewStrategy.FileByFile}'.";
    }
}
