// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>Token and duration metrics for one review strategy stage.</summary>
public sealed record ReviewStageMetrics(
    string StageName,
    TimeSpan Duration,
    long InputTokens,
    long OutputTokens,
    bool Degraded = false);
