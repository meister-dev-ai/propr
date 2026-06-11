// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Structured failure captured when a local review workspace cannot be prepared or used safely.
/// </summary>
public sealed record ReviewWorkspaceFailure(
    string Stage,
    string Code,
    string Message,
    bool Retryable,
    bool FallbackApplied);
