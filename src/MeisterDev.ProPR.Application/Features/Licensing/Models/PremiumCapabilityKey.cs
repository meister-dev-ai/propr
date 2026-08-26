// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

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

    /// <summary>
    ///     Capability key for configuring USD spend budgets and for the views reporting spend against them.
    ///     Enforcement of a cap already configured is not gated on it: a cap protects the installation from
    ///     spend and keeps doing so in every edition.
    /// </summary>
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

    /// <summary>
    ///     Capability key for running more than the built-in System tenant. Everything configured per tenant -
    ///     identity providers, login policy, the AI compliance restrictions and the tenant model catalog - is
    ///     reachable only while this capability is available.
    /// </summary>
    public const string MultiTenancy = "multi-tenancy";

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
        MultiTenancy,
    ];
}
