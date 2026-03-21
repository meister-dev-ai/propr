using System.Diagnostics;
using MeisterProPR.Api.Telemetry;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Api.Workers;

/// <summary>Background worker that periodically crawls ADO for assigned PRs and creates review jobs.</summary>
public sealed class AdoPrCrawlerWorker(
    IServiceScopeFactory scopeFactory,
    ReviewJobMetrics metrics,
    IConfiguration configuration,
    ILogger<AdoPrCrawlerWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue("PR_CRAWL_INTERVAL_SECONDS", 60);
        if (intervalSeconds < 10)
        {
            intervalSeconds = 10;
        }

        logger.LogInformation("AdoPrCrawlerWorker started. Crawl interval: {Interval}s", intervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        try
        {
            // Run immediately on startup, then on each subsequent tick.
            await this.RunCrawlCycleAsync(stoppingToken);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await this.RunCrawlCycleAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }

        logger.LogInformation("AdoPrCrawlerWorker stopped.");
    }

    private async Task RunCrawlCycleAsync(CancellationToken ct)
    {
        using var activity = ReviewJobTelemetry.Source.StartActivity("AdoPrCrawlerWorker.CrawlCycle");
        var sw = Stopwatch.StartNew();
        try
        {
            using var scope = scopeFactory.CreateScope();
            var crawlService = scope.ServiceProvider.GetRequiredService<IPrCrawlService>();
            await crawlService.CrawlAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception in PR crawl cycle — worker continues");
        }
        finally
        {
            sw.Stop();
            metrics.CrawlDuration.Record(sw.Elapsed.TotalSeconds);
        }
    }
}
