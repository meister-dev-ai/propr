// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Persists the scan progress recorded against a pull request: the review watermark that tracks the
///     last revision processed, and the per-thread counters that detect new human replies.
/// </summary>
/// <remarks>
///     Every fact on <see cref="ReviewPrScan" /> is written through its own operation, so a writer that
///     owns one fact cannot express a write of another. Inject the narrowest port a caller needs rather
///     than this composition, which exists for the implementation and for callers that genuinely own
///     every fact.
/// </remarks>
public interface IReviewPrScanRepository :
    IReviewPrScanThreadStatusStore,
    IReviewPrScanWatermarkStore,
    IReviewPrScanThreadPassStore,
    IReviewPrScanPendingReviewWriter;
