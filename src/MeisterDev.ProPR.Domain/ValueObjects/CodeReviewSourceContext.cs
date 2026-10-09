// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>Captured source coordinates used to initialize durable code-review work.</summary>
public sealed record CodeReviewSourceContext(
    ScmProvider Provider,
    string HostBaseUrl,
    string? OwnerOrNamespace,
    string? ProjectPath,
    CodeReviewPlatformKind Platform,
    string ExternalReviewId)
{
    /// <summary>Captures the source coordinates of a normalized review reference.</summary>
    public static CodeReviewSourceContext FromReview(CodeReviewRef review) => new(
        review.Repository.Host.Provider,
        review.Repository.Host.HostBaseUrl,
        review.Repository.OwnerOrNamespace,
        review.Repository.ProjectPath,
        review.Platform,
        review.ExternalReviewId);
}
