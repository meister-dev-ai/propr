// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.ComponentModel.DataAnnotations;

namespace MeisterDev.ProPR.Application.Options;

/// <summary>
///     Bounds on what an executor may ship in one batch.
///     <para>
///         Protocol payloads are already a known volume problem on the read side, and batching them is an
///         easy way to make the write side worse. A ceiling turns that into a refusal the executor can act
///         on by splitting, rather than a request that times out or a row nobody can read back.
///     </para>
/// </summary>
public sealed class RunnerIngestOptions
{
    /// <summary>
    ///     Maximum number of events, file results, and spend records combined in one batch.
    ///     Bound to <c>RUNNER_INGEST_MAX_ITEMS_PER_BATCH</c>.
    /// </summary>
    [Range(1, 10000, ErrorMessage = "MaxItemsPerBatch must be between 1 and 10000.")]
    public int MaxItemsPerBatch { get; set; } = 500;

    /// <summary>
    ///     Maximum serialised size of one batch, in bytes. Enforced at the transport edge, where the size is
    ///     known before the body is materialised.
    ///     Bound to <c>RUNNER_INGEST_MAX_BATCH_BYTES</c>.
    /// </summary>
    [Range(64 * 1024, 64 * 1024 * 1024, ErrorMessage = "MaxBatchBytes must be between 64 KiB and 64 MiB.")]
    public int MaxBatchBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    ///     How long the control plane may take to apply a batch before the executor should slow down. The
    ///     freshness ceiling for live trace: an executor that keeps within it keeps the protocol viewer
    ///     usable while the review is still running.
    ///     Bound to <c>RUNNER_INGEST_FRESHNESS_SECONDS</c>.
    /// </summary>
    [Range(1, 300, ErrorMessage = "FreshnessSeconds must be between 1 and 300.")]
    public int FreshnessSeconds { get; set; } = 15;
}
