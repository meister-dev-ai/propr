// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     Expires the add-in action runs this host still had open when it stops.
/// </summary>
/// <remarks>
///     <para>
///         A run outlives the call that opened it, so a host that stops mid-flow leaves rows nothing is going to
///         report against: the add-in that would have reported went with the process, and the socket a completion
///         would have arrived on is closed. Left alone the row would stay pending until its window closed, which
///         tells an operator to keep waiting for something that cannot arrive.
///     </para>
///     <para>
///         Scoped to the runs this host opened, which are the ones it holds signals for. A deployment runs more
///         than one host and a run another one opened is still live when this one stops, so expiring every
///         pending row would end flows an operator is in the middle of on another replica.
///     </para>
/// </remarks>
/// <param name="invocations">Where the runs are expired.</param>
/// <param name="cancellations">Holds the runs this host opened and has not closed.</param>
/// <param name="logger">Records how many were expired.</param>
public sealed partial class ProviderInvocationShutdownExpiry(
    ProviderActionInvocations invocations,
    ProviderInvocationCancellation cancellations,
    ILogger<ProviderInvocationShutdownExpiry> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Closed before the set is read, so an invocation cannot open into the gap between the two and sit
        // Pending until its own window lapses.
        cancellations.BeginStopping();

        var open = cancellations.Live;
        if (open.Count == 0)
        {
            return;
        }

        try
        {
            var expired = await invocations.ExpireAsync(open, cancellationToken).ConfigureAwait(false);
            LogExpiredAtShutdown(logger, expired);
        }
        catch (Exception exception)
        {
            // A host that cannot reach its database while stopping still has to stop. The rows it could not
            // expire are expired by their own windows, which is the outcome this exists to improve on rather
            // than to replace.
            LogShutdownExpiryFailed(logger, exception);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Expired {Count} provider add-in action invocation(s) that were still open when this host stopped")]
    private static partial void LogExpiredAtShutdown(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not expire the provider add-in action invocations open at shutdown; their windows will expire them")]
    private static partial void LogShutdownExpiryFailed(ILogger logger, Exception exception);
}
