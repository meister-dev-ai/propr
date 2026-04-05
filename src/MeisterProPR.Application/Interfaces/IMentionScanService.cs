// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
