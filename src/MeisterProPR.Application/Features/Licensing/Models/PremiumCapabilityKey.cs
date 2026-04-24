// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Licensing.Models;

/// <summary>Stable keys for installation-wide premium capabilities.</summary>
public static class PremiumCapabilityKey
{
    /// <summary>Capability key for single sign-on authentication.</summary>
    public const string SsoAuthentication = "sso-authentication";

    /// <summary>Capability key for running more than one review job concurrently.</summary>
    public const string ParallelReviewExecution = "parallel-review-execution";

    /// <summary>Capability key for configuring more than one SCM provider connection.</summary>
    public const string MultipleScmProviders = "multiple-scm-providers";

    /// <summary>Capability key for guided crawl configuration and automated crawl setup.</summary>
    public const string CrawlConfigs = "crawl-configs";

    /// <summary>Capability key for ProCursor knowledge indexing, querying, and reporting.</summary>
    public const string ProCursor = "procursor";

    /// <summary>All known premium capability keys in their canonical order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        SsoAuthentication,
        ParallelReviewExecution,
        MultipleScmProviders,
        CrawlConfigs,
        ProCursor,
    ];
}
