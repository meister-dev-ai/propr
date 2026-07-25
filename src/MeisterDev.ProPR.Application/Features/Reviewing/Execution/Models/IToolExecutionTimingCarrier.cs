// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Implemented by structured tool results that expose ordered phase timings.
/// </summary>
public interface IToolExecutionTimingCarrier
{
    /// <summary>
    ///     Ordered timing segments captured during the tool execution.
    /// </summary>
    IReadOnlyList<ProtocolEventPhaseTiming>? PhaseTimings { get; }
}
