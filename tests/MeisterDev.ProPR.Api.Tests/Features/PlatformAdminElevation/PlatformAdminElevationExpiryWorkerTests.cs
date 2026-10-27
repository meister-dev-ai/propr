// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Workers;
using MeisterDev.ProPR.Application.Features.PlatformAdminElevation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.PlatformAdminElevation;

/// <summary>
///     The expiry worker records the expiries of elevations once a minute. Only the shutdown of the host ends it; any
///     other failure of a sweep, a cancellation among them, is logged and the next sweep runs.
/// </summary>
public sealed class PlatformAdminElevationExpiryWorkerTests
{
    [Fact]
    public async Task ACancellationThatIsNotTheShutdownIsLoggedAndTheNextSweepRecordsTheExpiries()
    {
        var elevations = Substitute.For<IPlatformAdminElevationService>();
        elevations.RecordExpiriesAsync(Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new OperationCanceledException("The database command was cancelled."),
                _ => Task.FromResult(2));
        var logger = new ListLogger();
        var worker = CreateWorker(elevations, logger);

        await worker.SweepOnceAsync(CancellationToken.None);
        await worker.SweepOnceAsync(CancellationToken.None);

        var failure = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.IsType<OperationCanceledException>(failure.Exception);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("2", StringComparison.Ordinal));
        await elevations.Received(2).RecordExpiriesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheShutdownOfTheHostEndsTheSweepWithoutAnError()
    {
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();
        var elevations = Substitute.For<IPlatformAdminElevationService>();
        elevations.RecordExpiriesAsync(Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new OperationCanceledException(shutdown.Token));
        var logger = new ListLogger();
        var worker = CreateWorker(elevations, logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.SweepOnceAsync(shutdown.Token));

        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    private static PlatformAdminElevationExpiryWorker CreateWorker(IPlatformAdminElevationService elevations, ListLogger logger)
    {
        var services = new ServiceCollection().AddSingleton(elevations).BuildServiceProvider();
        return new PlatformAdminElevationExpiryWorker(services.GetRequiredService<IServiceScopeFactory>(), logger);
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class ListLogger : ILogger<PlatformAdminElevationExpiryWorker>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            this.Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}
