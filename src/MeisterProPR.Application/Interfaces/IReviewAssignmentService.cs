// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Assigns a normalized reviewer identity to a provider-native code review.</summary>
public interface IReviewAssignmentService
{
    /// <summary>The provider family implemented by this adapter.</summary>
    ScmProvider Provider { get; }

    /// <summary>Adds the given reviewer as an optional reviewer on the target review when supported.</summary>
    Task AddOptionalReviewerAsync(
        Guid clientId,
        CodeReviewRef review,
        ReviewerIdentity reviewer,
        CancellationToken ct = default);
}
