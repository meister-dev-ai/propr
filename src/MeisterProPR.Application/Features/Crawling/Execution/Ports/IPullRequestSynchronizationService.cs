// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Crawling.Execution.Models;

namespace MeisterProPR.Application.Features.Crawling.Execution.Ports;

/// <summary>Coordinates source-neutral pull-request synchronization for crawl and webhook activations.</summary>
public interface IPullRequestSynchronizationService
{
    /// <summary>Synchronizes lifecycle, thread-memory, and review-intake decisions for one pull request activation.</summary>
    Task<PullRequestSynchronizationOutcome> SynchronizeAsync(
        PullRequestSynchronizationRequest request,
        CancellationToken ct = default);
}
