using MeisterDev.ProPR.CodeInsights.Contracts;

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights.Ports;

/// <summary>
///     Persistence boundary for what became of a finding once a human dealt with it.
/// </summary>
/// <remarks>
///     Write-once by design, which is why it is its own boundary: an outcome is the evidence every quality number
///     rests on, and the rule that it may not be revised is easier to hold in a port with two methods than in one
///     that also creates and reads findings.
/// </remarks>
public interface ICodeInsightDispositionStore
{
    /// <summary>
    ///     Records what became of a finding, and returns whether this call was the one that decided it.
    ///     An already-decided finding is left exactly as it was and <see langword="false" /> is returned: a
    ///     crawl observes the same resolved thread repeatedly, and a metric already computed from a
    ///     disposition must not change underneath a report.
    /// </summary>
    Task<bool> RecordDispositionAsync(
        Guid findingId,
        CodeInsightDispositionRecord disposition,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the recorded disposition for a finding, or <see langword="null" /> when its thread has not
    ///     resolved yet.
    /// </summary>
    Task<CodeInsightDispositionRecord?> GetDispositionAsync(Guid findingId, CancellationToken ct = default);
}
