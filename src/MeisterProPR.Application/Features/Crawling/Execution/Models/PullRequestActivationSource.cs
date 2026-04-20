// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Crawling.Execution.Models;

/// <summary>Identifies what activation source woke up shared pull-request synchronization.</summary>
public enum PullRequestActivationSource
{
    /// <summary>The pull request was discovered by the periodic crawler.</summary>
    Crawl = 0,

    /// <summary>The pull request was activated by a webhook delivery.</summary>
    Webhook = 1,
}
