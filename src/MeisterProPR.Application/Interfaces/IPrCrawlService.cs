// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Interfaces;

/// <summary>Orchestrates a single periodic PR crawl cycle.</summary>
public interface IPrCrawlService
{
    /// <summary>Runs one crawl cycle: discovers assigned PRs and creates pending review jobs.</summary>
    Task CrawlAsync(CancellationToken cancellationToken = default);
}
