// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.UsageStatistics.Http;

/// <summary>
///     Log messages for the usage-statistics transport.
///     <para>
///         All at debug level. A failed send is expected on a restricted network and requires no operator
///         action, so it is not logged as a warning.
///     </para>
/// </summary>
public sealed partial class UsageStatisticsPingClient
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Anonymous usage statistics delivered.")]
    private static partial void LogPingDelivered(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "The anonymous usage statistics receiver answered {StatusCode}; the snapshot was discarded.")]
    private static partial void LogPingRejected(ILogger logger, int statusCode);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Anonymous usage statistics could not be delivered; the snapshot was discarded.")]
    private static partial void LogPingFailed(ILogger logger, Exception exception);
}
