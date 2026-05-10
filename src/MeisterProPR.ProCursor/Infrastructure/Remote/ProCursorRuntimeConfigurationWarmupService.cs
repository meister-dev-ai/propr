// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Exceptions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.ProCursor.Infrastructure.Remote;

/// <summary>
///     Warms the in-memory runtime configuration cache before background work starts.
/// </summary>
public sealed partial class ProCursorRuntimeConfigurationWarmupService(
    IProCursorRuntimeConfigurationCache cache,
    ILogger<ProCursorRuntimeConfigurationWarmupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await cache.WarmAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is ProCursorDependencyUnavailableException or HttpRequestException)
        {
            LogWarmupDeferred(logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Deferred ProCursor runtime-configuration warmup because ProPR was unavailable during host startup")]
    private static partial void LogWarmupDeferred(ILogger logger, Exception ex);
}
