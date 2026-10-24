// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs.ProCursor;

/// <summary>The index jobs of knowledge sources after their indexing was stopped.</summary>
/// <param name="Cancelled">The pending index jobs that were cancelled.</param>
/// <param name="RunningSince">The start times of the index jobs that were already running and still run.</param>
public sealed record ProCursorIndexingStop(int Cancelled, IReadOnlyList<DateTimeOffset> RunningSince);
