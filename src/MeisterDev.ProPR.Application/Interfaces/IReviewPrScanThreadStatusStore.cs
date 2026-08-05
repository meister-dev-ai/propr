// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     The scan record as the thread-memory state machine sees it: everything readable, and of the
///     progress facts only the last-seen thread status writable. The watermark and the reply counters
///     are unreachable through this port, which is what keeps their owners the only writers of them.
/// </summary>
public interface IReviewPrScanThreadStatusStore : IReviewPrScanReader, IReviewPrScanThreadStatusWriter;
