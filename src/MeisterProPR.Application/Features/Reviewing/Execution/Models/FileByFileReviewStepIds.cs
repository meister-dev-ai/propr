// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Stable offline-only step ids for the file-by-file review workflow.
/// </summary>
public static class FileByFileReviewStepIds
{
    /// <summary>Comment relevance filter step identifier.</summary>
    public const string CommentRelevanceFilter = "comment_relevance_filter";

    /// <summary>Memory reconsideration step identifier.</summary>
    public const string MemoryReconsideration = "memory_reconsideration";

    /// <summary>Local verification step identifier.</summary>
    public const string LocalVerification = "local_verification";

    /// <summary>Quality filter step identifier.</summary>
    public const string QualityFilter = "quality_filter";

    /// <summary>PR verification step identifier.</summary>
    public const string PrVerification = "pr_verification";

    /// <summary>Final gate step identifier.</summary>
    public const string FinalGate = "final_gate";

    /// <summary>Summary reconciliation step identifier.</summary>
    public const string SummaryReconciliation = "summary_reconciliation";
}
