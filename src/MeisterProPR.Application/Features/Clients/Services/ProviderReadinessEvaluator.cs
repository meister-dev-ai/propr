// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Clients.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Clients.Services;

/// <summary>Evaluates current readiness for one client provider connection.</summary>
public sealed class ProviderReadinessEvaluator(
    IClientScmScopeRepository scopeRepository,
    IClientReviewerIdentityRepository reviewerIdentityRepository,
    IScmProviderRegistry providerRegistry,
    IProviderReadinessProfileCatalog readinessProfileCatalog) : IProviderReadinessEvaluator
{
    /// <summary>Evaluates the readiness of a provider connection for the specified client.</summary>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="connection">The client SCM connection to evaluate.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The provider connection readiness evaluation result.</returns>
    public async Task<ProviderConnectionReadinessResult> EvaluateAsync(
        Guid clientId,
        ClientScmConnectionDto connection,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var profile = readinessProfileCatalog.GetProfile(connection.ProviderFamily, connection.HostBaseUrl);
        var scopes = await scopeRepository.GetByConnectionIdAsync(clientId, connection.Id, ct);
        var reviewerIdentity = await reviewerIdentityRepository.GetByConnectionIdAsync(clientId, connection.Id, ct);

        var hasEnabledScope = scopes.Any(scope => scope.IsEnabled);
        var hasReviewerIdentity = reviewerIdentity is not null;
        var adapterSetRegistered = providerRegistry.IsRegistered(connection.ProviderFamily);
        var verificationStatus = NormalizeVerificationStatus(connection.VerificationStatus);
        var criteriaResults = BuildCriteriaResults(
            connection,
            profile,
            hasEnabledScope,
            hasReviewerIdentity,
            adapterSetRegistered,
            verificationStatus);

        if (!connection.IsActive)
        {
            return BuildResult(
                connection.LastVerifiedAt is null
                    ? ProviderConnectionReadinessLevel.Configured
                    : ProviderConnectionReadinessLevel.Degraded,
                profile.HostVariant,
                "Connection is disabled.",
                criteriaResults);
        }

        if (!adapterSetRegistered)
        {
            return BuildResult(
                ProviderConnectionReadinessLevel.Configured,
                profile.HostVariant,
                "Provider baseline adapter registration is incomplete.",
                criteriaResults);
        }

        if (verificationStatus is VerificationState.Failed or VerificationState.Stale)
        {
            return BuildResult(
                connection.LastVerifiedAt is null
                    ? ProviderConnectionReadinessLevel.Configured
                    : ProviderConnectionReadinessLevel.Degraded,
                profile.HostVariant,
                connection.LastVerificationError ?? "Connection verification no longer satisfies onboarding readiness.",
                criteriaResults);
        }

        if (verificationStatus != VerificationState.Verified)
        {
            return BuildResult(
                ProviderConnectionReadinessLevel.Configured,
                profile.HostVariant,
                "Connection has not completed onboarding verification yet.",
                criteriaResults);
        }

        var workflowCriteriaSatisfied = hasEnabledScope && hasReviewerIdentity && profile.IsWorkflowComplete;
        if (workflowCriteriaSatisfied)
        {
            return BuildResult(
                ProviderConnectionReadinessLevel.WorkflowComplete,
                profile.HostVariant,
                "Connection meets onboarding and workflow-complete readiness criteria.",
                criteriaResults);
        }

        return BuildResult(
            ProviderConnectionReadinessLevel.OnboardingReady,
            profile.HostVariant,
            "Connection is verified for onboarding, but workflow-complete readiness criteria are still missing.",
            criteriaResults);
    }

    private static ProviderConnectionReadinessResult BuildResult(
        ProviderConnectionReadinessLevel readinessLevel,
        string hostVariant,
        string readinessReason,
        IReadOnlyList<ProviderReadinessCriterionResult> criteriaResults)
    {
        var missingCriteria = criteriaResults
            .Where(result => string.Equals(result.Status, "unsatisfied", StringComparison.Ordinal))
            .Select(result => result.Summary)
            .Distinct(StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();

        return new ProviderConnectionReadinessResult(
            readinessLevel,
            hostVariant,
            readinessReason,
            missingCriteria,
            criteriaResults);
    }

    private static IReadOnlyList<ProviderReadinessCriterionResult> BuildCriteriaResults(
        ClientScmConnectionDto connection,
        ProviderReadinessProfile profile,
        bool hasEnabledScope,
        bool hasReviewerIdentity,
        bool adapterSetRegistered,
        VerificationState verificationStatus)
    {
        return new List<ProviderReadinessCriterionResult>
        {
            Criterion(
                "connection.active",
                "connection",
                connection.IsActive,
                "Connection must be active."),
            Criterion(
                "connection.verification",
                "connection",
                verificationStatus == VerificationState.Verified,
                verificationStatus switch
                {
                    VerificationState.Failed => connection.LastVerificationError ?? "Connection verification failed.",
                    VerificationState.Stale => "Connection needs re-verification before it can be treated as ready.",
                    VerificationState.Unknown => "Connection has not been verified yet.",
                    _ => "Connection passed onboarding verification.",
                }),
            Criterion(
                "provider.baselineAdapterSet",
                "providerFamily",
                adapterSetRegistered,
                "Provider baseline adapter set is not registered."),
            Criterion(
                "connection.enabledScope",
                "connection",
                hasEnabledScope,
                "Enabled scope selection is required for workflow-complete readiness."),
            Criterion(
                "connection.reviewerIdentity",
                "connection",
                hasReviewerIdentity,
                "Configured reviewer identity is required for workflow-complete readiness."),
            Criterion(
                "profile.manualReview",
                "hostVariant",
                profile.ManualReviewReady,
                $"{profile.ProviderFamily} {profile.HostVariant} manual review proof is not yet marked workflow-complete."),
            Criterion(
                "profile.automaticWorkflow",
                "hostVariant",
                profile.AutomaticWorkflowReady,
                $"{profile.ProviderFamily} {profile.HostVariant} automatic workflow proof is not yet marked workflow-complete."),
            Criterion(
                "profile.lifecycleContinuity",
                "hostVariant",
                profile.LifecycleContinuityReady,
                $"{profile.ProviderFamily} {profile.HostVariant} lifecycle continuity proof is not yet marked workflow-complete."),
            Criterion(
                "profile.securityBaseline",
                "hostVariant",
                profile.SecurityBaselineReady,
                $"{profile.ProviderFamily} {profile.HostVariant} security baseline proof is not yet marked workflow-complete."),
            Criterion(
                "profile.observabilityBaseline",
                "hostVariant",
                profile.ObservabilityBaselineReady,
                $"{profile.ProviderFamily} {profile.HostVariant} observability baseline proof is not yet marked workflow-complete."),
        }.AsReadOnly();
    }

    private static ProviderReadinessCriterionResult Criterion(string key, string scope, bool satisfied, string summary)
    {
        return new ProviderReadinessCriterionResult(key, scope, satisfied ? "satisfied" : "unsatisfied", summary);
    }

    private static VerificationState NormalizeVerificationStatus(string status)
    {
        return status.Trim().ToLowerInvariant() switch
        {
            "verified" => VerificationState.Verified,
            "failed" => VerificationState.Failed,
            "stale" => VerificationState.Stale,
            _ => VerificationState.Unknown,
        };
    }

    private enum VerificationState
    {
        Unknown,
        Verified,
        Failed,
        Stale,
    }
}
