// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Crawling.Configuration;

/// <summary>Returns bounded rich discovery pages with shared refresh coordination.</summary>
public interface IClientPullRequestOverviewService
{
    Task<ClientPullRequestOverviewPage> ReadAsync(Guid clientId, ClientPullRequestOverviewRequest request, CancellationToken ct);
}
