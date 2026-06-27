// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Scoped accessor that carries the per-purpose model selection through one offline harness execution.
///     The offline runtime resolver reads it to map each <c>AiPurpose</c> to a configured model. When the
///     selection is <see langword="null" /> (no tiered configuration), the resolver behaves as if no AI
///     binding were configured, preserving single-model behavior.
/// </summary>
public interface IOfflineTierModelAccessor
{
    /// <summary>Gets or sets the per-purpose model selection active for the current execution scope.</summary>
    OfflineTierModelSelection? Selection { get; set; }
}
