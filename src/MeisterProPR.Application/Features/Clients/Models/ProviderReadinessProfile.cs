// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Clients.Models;

/// <summary>Curated support-bar evidence for one provider family and host variant.</summary>
public sealed record ProviderReadinessProfile(
    ScmProvider ProviderFamily,
    string HostVariant,
    bool ManualReviewReady,
    bool AutomaticWorkflowReady,
    bool LifecycleContinuityReady,
    bool SecurityBaselineReady,
    bool ObservabilityBaselineReady,
    string Notes)
{
    /// <summary>Gets a value indicating whether all workflow readiness requirements are met.</summary>
    public bool IsWorkflowComplete =>
        this.ManualReviewReady
        && this.AutomaticWorkflowReady
        && this.LifecycleContinuityReady
        && this.SecurityBaselineReady
        && this.ObservabilityBaselineReady;
}
