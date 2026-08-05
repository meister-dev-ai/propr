// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Threads.Ports;

/// <summary>
///     Runs one thread pass: resolves the reviewer-owned threads the developer has fixed and answers the ones
///     they replied to.
/// </summary>
public interface IThreadPassService
{
    /// <summary>Executes one queued thread pass end to end.</summary>
    /// <param name="job">The pass to run.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task ProcessAsync(ThreadPassJob job, CancellationToken ct = default);
}
