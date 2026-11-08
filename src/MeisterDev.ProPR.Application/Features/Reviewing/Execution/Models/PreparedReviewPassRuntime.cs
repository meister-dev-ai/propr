// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>The runtime resolved for a file's configured pass before its assignment inventory is closed.</summary>
public readonly record struct PreparedReviewPassRuntime(
    IResolvedAiChatRuntime? Runtime,
    string? Level,
    string? Failure)
{
    /// <summary>Retains a failed resolution so execution does not repeat it or change the captured assignment.</summary>
    public static PreparedReviewPassRuntime Failed(string failure) => new(null, null, failure);
}
