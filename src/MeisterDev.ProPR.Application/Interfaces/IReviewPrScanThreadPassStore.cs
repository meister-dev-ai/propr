// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     The scan record as the thread pass sees it: everything readable, and of the progress facts the thread
///     watermark and the per-thread reply counters writable, plus the power to retire rows for threads the
///     provider no longer reports. The review watermark and the last-seen thread status are unreachable
///     through this port.
/// </summary>
public interface IReviewPrScanThreadPassStore :
    IReviewPrScanReader,
    IReviewPrScanThreadPassWatermarkWriter,
    IReviewPrScanThreadReplyCountWriter,
    IReviewPrScanThreadRegistry;
