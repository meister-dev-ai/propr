// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.UsageReporting;

internal sealed partial class ModelUsageRecorder
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to record insight model usage for client {ClientId} on model {Model}. The tokens were spent but are not counted.")]
    private static partial void LogRecordingFailed(ILogger logger, Guid clientId, string model, Exception exception);
}
