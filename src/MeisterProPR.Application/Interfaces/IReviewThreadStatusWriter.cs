// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Updates provider-native review thread status.</summary>
public interface IReviewThreadStatusWriter
{
    /// <summary>The provider family implemented by this adapter.</summary>
    ScmProvider Provider { get; }

    /// <summary>Updates the target review thread status.</summary>
    Task UpdateThreadStatusAsync(
        Guid clientId,
        ReviewThreadRef thread,
        string status,
        CancellationToken ct = default);
}
