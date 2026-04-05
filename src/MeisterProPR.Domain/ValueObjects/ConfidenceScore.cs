// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Represents a confidence evaluation for a single review concern.
/// </summary>
/// <param name="Concern">A short description of the concern being evaluated.</param>
/// <param name="Score">Confidence score in the range 0–100.</param>
public sealed record ConfidenceScore(string Concern, int Score);
