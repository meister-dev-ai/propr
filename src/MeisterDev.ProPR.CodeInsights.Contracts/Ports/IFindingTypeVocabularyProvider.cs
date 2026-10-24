// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>Provides active client custom types without coupling review execution to analytics persistence.</summary>
public interface IFindingTypeVocabularyProvider
{
    /// <summary>Captures the assignable vocabulary for a review.</summary>
    Task<FindingTypeVocabulary> GetVocabularyAsync(Guid clientId, CancellationToken ct = default);
}
