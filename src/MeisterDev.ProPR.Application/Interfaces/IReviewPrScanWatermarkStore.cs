// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     The scan record as the file pass sees it: everything readable, and of the progress facts only the
///     review watermark writable. The thread watermark and the per-thread counters are unreachable through
///     this port, which is what keeps the thread pass their only writer.
/// </summary>
public interface IReviewPrScanWatermarkStore : IReviewPrScanReader, IReviewPrScanWatermarkWriter;
