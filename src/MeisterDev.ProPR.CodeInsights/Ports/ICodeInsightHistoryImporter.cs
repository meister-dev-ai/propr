using MeisterDev.ProPR.CodeInsights.History;

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.


namespace MeisterDev.ProPR.CodeInsights.Ports;

/// <summary>
///     Replays reviews that ran before collection was switched on into the collection.
/// </summary>
/// <remarks>
///     <para>
///         Replay, never re-derive. Every fact an import writes already exists in the product's own tables: the
///         findings a job produced are persisted with its file results, the thread each posted comment belongs to
///         is recorded as provenance, and what a human did with a thread is in the retained thread. The import
///         rebuilds the same event the live path raises and hands it to the same consumers, so an imported finding
///         is indistinguishable from a collected one and no second code path can drift from the first.
///     </para>
///     <para>
///         What cannot be replayed is stated rather than approximated. A review's own result does not record what
///         became of a finding, so outcomes exist only where threads were retained, and a posted comment that was
///         never linked to a thread can never gain one.
///     </para>
/// </remarks>
public interface ICodeInsightHistoryImporter
{
    /// <summary>
    ///     Imports one window for one client and reports what it read and wrote. Bounded, repeatable, and safe to
    ///     run over a window it has already covered: jobs the collection already holds are skipped rather than
    ///     merged.
    /// </summary>
    Task<CodeInsightImportResult> ImportAsync(
        CodeInsightImportRequest request,
        CancellationToken ct = default);
}
