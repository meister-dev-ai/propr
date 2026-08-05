// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Services;

public sealed partial class MentionReplyProvenanceReconciler
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "MentionReplyProvenanceReconciler: rewrote provenance for {RecoveredCount} of {ExaminedCount} recent mention answers that had none")]
    private static partial void LogProvenanceRecovered(ILogger logger, int recoveredCount, int examinedCount);
}
