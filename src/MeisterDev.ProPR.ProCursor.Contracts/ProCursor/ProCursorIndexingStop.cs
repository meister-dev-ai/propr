// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs.ProCursor;

/// <summary>The index jobs of knowledge sources after their indexing was stopped.</summary>
/// <param name="Cancelled">The pending and running index jobs that were cancelled.</param>
/// <param name="RunningUntil">
///     The lease expiry times of the cancelled jobs that were running. The ProCursor service that ran such a job can
///     write until the lease expires.
/// </param>
public sealed record ProCursorIndexingStop(int Cancelled, IReadOnlyList<DateTimeOffset> RunningUntil);
