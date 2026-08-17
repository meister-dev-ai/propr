// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

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

    /// <summary>
    ///     Capability key for answering <c>@</c>-mentions in pull request comments. Separate from crawl
    ///     configurations, which it used to be gated on: one licenses finding pull requests to review, this
    ///     licenses answering questions asked in them, and an installation can be entitled to either alone.
    /// </summary>
    public const string MentionAnswering = "mention-answering";

    /// <summary>Capability key for configuring and enforcing USD spend budgets.</summary>
    public const string Budgeting = "budgeting";

    /// <summary>
    ///     Capability key for collecting and viewing Code Insights quality analytics. Collection also
    ///     requires a per-client opt-in, so this capability being available is necessary but not sufficient.
    /// </summary>
    public const string CodeInsights = "code-insights";

    /// <summary>
    ///     Capability key for executing reviews on registered runners rather than in the control plane.
    ///     Separate from parallel review execution: one is about how much work runs at once, this is about
    ///     where it runs, and an installation can be licensed for either without the other.
    /// </summary>
    public const string DistributedExecution = "distributed-execution";

    /// <summary>All known premium capability keys in their canonical order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        SsoAuthentication,
        ParallelReviewExecution,
        DistributedExecution,
        MultipleScmProviders,
        CrawlConfigs,
        MentionAnswering,
        Budgeting,
        CodeInsights,
    ];
}
