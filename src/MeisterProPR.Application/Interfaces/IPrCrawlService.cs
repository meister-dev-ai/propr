namespace MeisterProPR.Application.Interfaces;

/// <summary>Orchestrates a single periodic PR crawl cycle.</summary>
public interface IPrCrawlService
{
    /// <summary>Runs one crawl cycle: discovers assigned PRs and creates pending review jobs.</summary>
    Task CrawlAsync(CancellationToken cancellationToken = default);
}
