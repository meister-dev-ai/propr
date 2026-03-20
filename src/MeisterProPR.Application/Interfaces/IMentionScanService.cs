namespace MeisterProPR.Application.Interfaces;

/// <summary>Orchestrates a single mention scan cycle across all active crawl configurations.</summary>
public interface IMentionScanService
{
    /// <summary>
    ///     Runs one scan cycle: discovers recently updated PRs, detects <c>@bot</c> mentions,
    ///     and enqueues any new <see cref="MeisterProPR.Domain.Entities.MentionReplyJob" /> items.
    /// </summary>
    Task ScanAsync(CancellationToken cancellationToken = default);
}
