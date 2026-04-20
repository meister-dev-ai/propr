// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using MeisterProPR.Application.Features.Crawling.Execution.Models;
using MeisterProPR.Application.Features.Crawling.Execution.Ports;
using MeisterProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterProPR.Application.Features.Crawling.Webhooks.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Features.Crawling.Webhooks.Commands.HandleProviderWebhookDelivery;

/// <summary>Validates, scopes, logs, and routes one provider-scoped webhook delivery through provider adapters.</summary>
public sealed partial class HandleProviderWebhookDeliveryHandler(
    IWebhookConfigurationRepository webhookConfigurationRepository,
    IWebhookDeliveryLogRepository webhookDeliveryLogRepository,
    IScmProviderRegistry providerRegistry,
    IClientRegistry clientRegistry,
    ISecretProtectionCodec secretProtectionCodec,
    ILogger<HandleProviderWebhookDeliveryHandler> logger,
    IPullRequestSynchronizationService pullRequestSynchronizationService,
    IProviderActivationService? providerActivationService = null)
{
    private const int OkStatusCode = 200;
    private const int BadRequestStatusCode = 400;
    private const int UnauthorizedStatusCode = 401;
    private const int NotFoundStatusCode = 404;
    private const int MaxDeliveryActionSummaries = 12;
    private const int MaxDeliveryActionSummaryLength = 280;
    private const int MaxFailureReasonLength = 280;
    private static readonly ActivitySource WebhookActivitySource = new("MeisterProPR.Webhooks", "1.0.0");
    private static readonly Meter WebhookMeter = new("MeisterProPR", "1.0.0");

    private static readonly Counter<long> WebhookDeliveryCounter = WebhookMeter.CreateCounter<long>(
        "meisterpropr_webhook_deliveries_total",
        "deliveries",
        "Total number of provider-scoped webhook deliveries processed by the shared receiver.");

    private static readonly Histogram<double> WebhookDeliveryDuration = WebhookMeter.CreateHistogram<double>(
        "meisterpropr_webhook_delivery_duration_seconds",
        "s",
        "Duration of provider-scoped webhook delivery validation and routing.");

    /// <summary>Handles one inbound provider-scoped webhook delivery.</summary>
    public async Task<WebhookRoutingDecision> HandleAsync(
        HandleProviderWebhookDeliveryCommand command,
        CancellationToken ct = default)
    {
        var startedAt = Stopwatch.StartNew();
        using var activity = WebhookActivitySource.StartActivity("webhook.delivery.handle");
        var providerTagValue = command.Provider.ToString().ToLowerInvariant();
        activity?.SetTag("webhook.provider", providerTagValue);

        if (providerActivationService is not null &&
            !await providerActivationService.IsEnabledAsync(command.Provider, ct))
        {
            return CompleteDecision(
                activity,
                startedAt,
                providerTagValue,
                null,
                false,
                CreateRejectedDecision(NotFoundStatusCode, null));
        }

        var configuration = await webhookConfigurationRepository.GetActiveByPathKeyAsync(command.PathKey, ct);
        if (configuration is null)
        {
            LogUnknownPathRejected(logger, command.Provider, command.PathKey);
            return CompleteDecision(
                activity,
                startedAt,
                providerTagValue,
                null,
                false,
                CreateRejectedDecision(NotFoundStatusCode, null));
        }

        if (MapProviderType(configuration.ProviderType) != command.Provider)
        {
            var rejected = CreateRejectedDecision(NotFoundStatusCode, null);
            await this.PersistLogAsync(configuration.Id, "unknown", null, null, null, null, rejected, ct);
            return CompleteDecision(activity, startedAt, providerTagValue, null, true, rejected);
        }

        var host = new ProviderHostRef(command.Provider, configuration.OrganizationUrl);
        var ingressService = providerRegistry.GetWebhookIngressService(command.Provider);
        var verificationSecret = secretProtectionCodec.Unprotect(
            configuration.SecretCiphertext ?? string.Empty,
            "WebhookSecret");

        if (!await ingressService.VerifyAsync(
                configuration.ClientId,
                host,
                command.Headers,
                command.Payload,
                verificationSecret,
                ct))
        {
            var rejected = CreateRejectedDecision(
                UnauthorizedStatusCode,
                "Webhook signature or authorization header was missing or invalid.");
            var eventType = TryReadEventHeader(command.Provider, command.Headers) ?? "unknown";
            await this.PersistLogAsync(configuration.Id, eventType, null, null, null, null, rejected, ct);
            LogResolvedDeliveryRejected(
                logger,
                configuration.Id,
                command.Provider,
                rejected.HttpStatusCode,
                rejected.FailureReason ?? "unauthorized");
            return CompleteDecision(activity, startedAt, providerTagValue, eventType, true, rejected);
        }

        WebhookDeliveryEnvelope delivery;
        try
        {
            delivery = await ingressService.ParseAsync(
                configuration.ClientId,
                host,
                command.Headers,
                command.Payload,
                ct);
        }
        catch (InvalidOperationException)
        {
            var rejected = CreateRejectedDecision(
                BadRequestStatusCode,
                "Webhook payload was malformed or missing required fields.");
            var eventType = TryReadEventHeader(command.Provider, command.Headers) ?? "unknown";
            await this.PersistLogAsync(configuration.Id, eventType, null, null, null, null, rejected, ct);
            LogMalformedResolvedDelivery(logger, configuration.Id, command.Provider);
            return CompleteDecision(activity, startedAt, providerTagValue, eventType, true, rejected);
        }

        var classification = Classify(delivery);
        if (classification is null)
        {
            var ignored = NormalizeDecision(
                new WebhookRoutingDecision(
                    WebhookDeliveryOutcome.Ignored,
                    OkStatusCode,
                    "ignored",
                    [
                        "Ignored delivery because the event type is unsupported or disabled for this webhook configuration.",
                    ],
                    "Unsupported or disabled event type."));
            await this.PersistLogAsync(
                configuration.Id,
                delivery.EventName,
                delivery.Repository?.ExternalRepositoryId,
                delivery.Review?.Number,
                delivery.SourceBranch,
                delivery.TargetBranch,
                ignored,
                ct);
            return CompleteDecision(activity, startedAt, providerTagValue, delivery.EventName, true, ignored);
        }

        if (!configuration.EnabledEvents.Contains(classification.EventType))
        {
            var ignored = NormalizeDecision(
                new WebhookRoutingDecision(
                    WebhookDeliveryOutcome.Ignored,
                    OkStatusCode,
                    "ignored",
                    [
                        "Ignored delivery because the event type is unsupported or disabled for this webhook configuration.",
                    ],
                    "Unsupported or disabled event type."));
            await this.PersistLogAsync(
                configuration.Id,
                delivery.EventName,
                delivery.Repository?.ExternalRepositoryId,
                delivery.Review?.Number,
                delivery.SourceBranch,
                delivery.TargetBranch,
                ignored,
                ct);
            return CompleteDecision(activity, startedAt, providerTagValue, delivery.EventName, true, ignored);
        }

        var matchedFilter = ResolveMatchingFilter(
            configuration.RepoFilters,
            delivery.Repository,
            delivery.TargetBranch);
        if (matchedFilter is null)
        {
            var rejected = CreateRejectedDecision(
                NotFoundStatusCode,
                "Delivery did not match the configured repository or branch scope.");
            await this.PersistLogAsync(
                configuration.Id,
                delivery.EventName,
                delivery.Repository?.ExternalRepositoryId,
                delivery.Review?.Number,
                delivery.SourceBranch,
                delivery.TargetBranch,
                rejected,
                ct);
            return CompleteDecision(activity, startedAt, providerTagValue, delivery.EventName, true, rejected);
        }

        if (delivery.Repository is null || delivery.Review is null)
        {
            var rejected = CreateRejectedDecision(
                BadRequestStatusCode,
                "Webhook delivery did not include a resolvable repository or pull request.");
            await this.PersistLogAsync(
                configuration.Id,
                delivery.EventName,
                delivery.Repository?.ExternalRepositoryId,
                delivery.Review?.Number,
                delivery.SourceBranch,
                delivery.TargetBranch,
                rejected,
                ct);
            return CompleteDecision(activity, startedAt, providerTagValue, delivery.EventName, true, rejected);
        }

        try
        {
            var effectiveRepository = ResolveEffectiveRepository(configuration, delivery.Repository, matchedFilter);
            var effectiveReview = new CodeReviewRef(
                effectiveRepository,
                delivery.Review.Platform,
                delivery.Review.ExternalReviewId,
                delivery.Review.Number);
            var effectiveRevision = await this.ResolveEffectiveReviewRevisionAsync(
                configuration.ClientId,
                delivery.Host.Provider,
                effectiveReview,
                delivery.Revision,
                ct);
            var configuredReviewer = await clientRegistry.GetReviewerIdentityAsync(configuration.ClientId, host, ct);
            var outcome = await pullRequestSynchronizationService.SynchronizeAsync(
                new PullRequestSynchronizationRequest
                {
                    ActivationSource = PullRequestActivationSource.Webhook,
                    SummaryLabel = classification.SummaryLabel,
                    ClientId = configuration.ClientId,
                    ProviderScopePath = configuration.OrganizationUrl,
                    ProviderProjectKey = configuration.ProjectId,
                    RepositoryId = effectiveRepository.ExternalRepositoryId,
                    PullRequestId = delivery.Review.Number,
                    PullRequestStatus = classification.PullRequestStatus,
                    Provider = delivery.Host.Provider,
                    Host = delivery.Host,
                    Repository = effectiveRepository,
                    CodeReview = effectiveReview,
                    ReviewRevision = effectiveRevision,
                    RequestedReviewerIdentity = configuredReviewer,
                    AllowReviewSubmission = !classification.RequiresLifecycleSync,
                    SourceBranch = delivery.SourceBranch,
                    TargetBranch = delivery.TargetBranch,
                },
                ct);

            var accepted = new WebhookRoutingDecision(
                WebhookDeliveryOutcome.Accepted,
                OkStatusCode,
                "accepted",
                outcome.ActionSummaries);
            await this.PersistLogAsync(
                configuration.Id,
                delivery.EventName,
                delivery.Repository.ExternalRepositoryId,
                delivery.Review.Number,
                delivery.SourceBranch,
                delivery.TargetBranch,
                accepted,
                ct);
            LogResolvedDeliveryAccepted(logger, configuration.Id, command.Provider, delivery.EventName);
            return CompleteDecision(activity, startedAt, providerTagValue, delivery.EventName, true, accepted);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var failed = NormalizeDecision(
                new WebhookRoutingDecision(
                    WebhookDeliveryOutcome.Failed,
                    OkStatusCode,
                    "failed",
                    [
                        "Webhook delivery passed validation but failed during downstream processing before any review action was recorded.",
                    ],
                    "Webhook delivery passed validation but failed during downstream processing."));
            await this.PersistLogAsync(
                configuration.Id,
                delivery.EventName,
                delivery.Repository.ExternalRepositoryId,
                delivery.Review.Number,
                delivery.SourceBranch,
                delivery.TargetBranch,
                failed,
                ct);
            LogResolvedDeliveryFailed(
                logger,
                configuration.Id,
                command.Provider,
                ex.GetType().FullName ?? ex.GetType().Name);
            return CompleteDecision(activity, startedAt, providerTagValue, delivery.EventName, true, failed);
        }
    }

    private async Task PersistLogAsync(
        Guid configurationId,
        string eventType,
        string? repositoryId,
        int? pullRequestId,
        string? sourceBranch,
        string? targetBranch,
        WebhookRoutingDecision decision,
        CancellationToken ct)
    {
        await webhookDeliveryLogRepository.AddAsync(
            configurationId,
            DateTimeOffset.UtcNow,
            eventType,
            decision.DeliveryOutcome,
            decision.HttpStatusCode,
            repositoryId,
            pullRequestId,
            sourceBranch,
            targetBranch,
            decision.ActionSummaries,
            decision.FailureReason,
            ct);
    }

    private async Task<ReviewRevision?> ResolveEffectiveReviewRevisionAsync(
        Guid clientId,
        ScmProvider provider,
        CodeReviewRef review,
        ReviewRevision? revision,
        CancellationToken ct)
    {
        if (!RequiresRevisionRefresh(revision))
        {
            return revision;
        }

        var refreshedRevision = await providerRegistry
            .GetCodeReviewQueryService(provider)
            .GetLatestRevisionAsync(clientId, review, ct);

        if (refreshedRevision is not null)
        {
            LogRefreshedWebhookRevision(logger, provider, review.ExternalReviewId, review.Number);
            return refreshedRevision;
        }

        return revision;
    }

    private static bool RequiresRevisionRefresh(ReviewRevision? revision)
    {
        if (revision is null)
        {
            return false;
        }

        return !LooksLikeCommitSha(revision.HeadSha)
               || !LooksLikeCommitSha(revision.BaseSha)
               || (revision.StartSha is not null && !LooksLikeCommitSha(revision.StartSha));
    }

    private static bool LooksLikeCommitSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length is < 7 or > 64)
        {
            return false;
        }

        foreach (var character in trimmed)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static ProviderWebhookClassification? Classify(WebhookDeliveryEnvelope delivery)
    {
        return delivery.DeliveryKind switch
        {
            "pull_request.created" => new ProviderWebhookClassification(
                WebhookEventType.PullRequestCreated,
                "pull request created",
                PrStatus.Active,
                false),
            "pull_request.updated" => new ProviderWebhookClassification(
                WebhookEventType.PullRequestUpdated,
                "pull request updated",
                PrStatus.Active,
                false),
            "pull_request.commented" => new ProviderWebhookClassification(
                WebhookEventType.PullRequestCommented,
                "pull request commented",
                PrStatus.Active,
                false),
            "reviewer_assignment" => new ProviderWebhookClassification(
                WebhookEventType.PullRequestUpdated,
                "reviewer assignment",
                PrStatus.Active,
                false),
            "pull_request.closed" => new ProviderWebhookClassification(
                WebhookEventType.PullRequestUpdated,
                "pull request closed",
                PrStatus.Abandoned,
                true),
            "pull_request.merged" => new ProviderWebhookClassification(
                WebhookEventType.PullRequestUpdated,
                "pull request merged",
                PrStatus.Completed,
                true),
            _ => null,
        };
    }

    private static RepositoryRef ResolveEffectiveRepository(
        WebhookConfigurationDto configuration,
        RepositoryRef repository,
        WebhookRepoFilterDto matchedFilter)
    {
        var canonicalRepositoryId = matchedFilter.CanonicalSourceRef?.Provider is not null
                                    && string.Equals(
                                        matchedFilter.CanonicalSourceRef.Provider,
                                        "azureDevOps",
                                        StringComparison.OrdinalIgnoreCase)
                                    && !string.IsNullOrWhiteSpace(matchedFilter.CanonicalSourceRef.Value)
            ? matchedFilter.CanonicalSourceRef.Value
            : null;

        if (string.IsNullOrWhiteSpace(canonicalRepositoryId))
        {
            return repository;
        }

        var carriesExplicitProjectIdentity =
            !string.Equals(
                repository.OwnerOrNamespace,
                repository.ExternalRepositoryId,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                repository.ProjectPath,
                repository.ExternalRepositoryId,
                StringComparison.OrdinalIgnoreCase);

        var ownerOrNamespace = carriesExplicitProjectIdentity
            ? repository.OwnerOrNamespace
            : configuration.ProjectId;
        var projectPath = carriesExplicitProjectIdentity
            ? repository.ProjectPath
            : configuration.ProjectId;

        if (string.Equals(repository.ExternalRepositoryId, canonicalRepositoryId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(repository.OwnerOrNamespace, ownerOrNamespace, StringComparison.Ordinal)
            && string.Equals(repository.ProjectPath, projectPath, StringComparison.Ordinal))
        {
            return repository;
        }

        return new RepositoryRef(repository.Host, canonicalRepositoryId, ownerOrNamespace, projectPath);
    }

    private static WebhookRepoFilterDto? ResolveMatchingFilter(
        IReadOnlyList<WebhookRepoFilterDto> filters,
        RepositoryRef? repository,
        string? targetBranch)
    {
        if (filters.Count == 0)
        {
            return new WebhookRepoFilterDto(Guid.Empty, string.Empty, []);
        }

        if (repository is null)
        {
            return null;
        }

        var normalizedTargetBranch = StripRefsHeads(targetBranch);
        var repositoryCandidates = new[]
            {
                repository.ExternalRepositoryId,
                repository.ProjectPath,
                repository.ProjectPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(),
            }
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var filter in filters)
        {
            var filterMatchesRepository = repositoryCandidates.Any(candidate =>
                string.Equals(candidate, filter.CanonicalSourceRef?.Value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate, filter.RepositoryName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate, filter.DisplayName, StringComparison.OrdinalIgnoreCase));

            if (!filterMatchesRepository)
            {
                continue;
            }

            if (filter.TargetBranchPatterns.Count == 0)
            {
                return filter;
            }

            if (normalizedTargetBranch is not null &&
                filter.TargetBranchPatterns.Any(pattern => MatchesBranchPattern(pattern, normalizedTargetBranch)))
            {
                return filter;
            }
        }

        return null;
    }

    private static bool MatchesBranchPattern(string pattern, string targetBranch)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var regexPattern = "^" + Regex.Escape(pattern.Trim())
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(targetBranch, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? StripRefsHeads(string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return null;
        }

        const string prefix = "refs/heads/";
        return branchName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? branchName[prefix.Length..]
            : branchName.Trim();
    }

    private static ScmProvider MapProviderType(WebhookProviderType providerType)
    {
        return providerType switch
        {
            WebhookProviderType.AzureDevOps => ScmProvider.AzureDevOps,
            WebhookProviderType.GitHub => ScmProvider.GitHub,
            WebhookProviderType.GitLab => ScmProvider.GitLab,
            WebhookProviderType.Forgejo => ScmProvider.Forgejo,
            _ => throw new InvalidOperationException($"Webhook provider {providerType} is not supported."),
        };
    }

    private static string? TryReadHeader(IReadOnlyDictionary<string, string> headers, string headerName)
    {
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, headerName, StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }

        return null;
    }

    private static string? TryReadEventHeader(ScmProvider provider, IReadOnlyDictionary<string, string> headers)
    {
        return provider switch
        {
            ScmProvider.GitHub => TryReadHeader(headers, "X-GitHub-Event"),
            ScmProvider.GitLab => TryReadHeader(headers, "X-Gitlab-Event"),
            ScmProvider.Forgejo => TryReadHeader(headers, "X-Gitea-Event"),
            _ => null,
        };
    }

    private static WebhookRoutingDecision CompleteDecision(
        Activity? activity,
        Stopwatch stopwatch,
        string provider,
        string? eventType,
        bool resolvedConfiguration,
        WebhookRoutingDecision decision)
    {
        stopwatch.Stop();

        var normalizedEventType = string.IsNullOrWhiteSpace(eventType) ? "unknown" : eventType.Trim();
        var outcomeTag = decision.DeliveryOutcome.ToString().ToLowerInvariant();

        activity?.SetTag("webhook.configuration.resolved", resolvedConfiguration);
        activity?.SetTag("webhook.event_type", normalizedEventType);
        activity?.SetTag("webhook.delivery_outcome", outcomeTag);
        activity?.SetTag("webhook.response_status", decision.ResponseStatus ?? "none");
        activity?.SetTag("http.response.status_code", decision.HttpStatusCode);
        activity?.SetTag("webhook.action_summary_count", decision.ActionSummaries.Count);
        activity?.SetStatus(
            decision.DeliveryOutcome is WebhookDeliveryOutcome.Rejected or WebhookDeliveryOutcome.Failed
                ? ActivityStatusCode.Error
                : ActivityStatusCode.Ok,
            decision.FailureReason);

        var tags = new TagList
        {
            { "provider", provider },
            { "delivery_outcome", outcomeTag },
            { "event_type", normalizedEventType },
            { "http_status_code", decision.HttpStatusCode },
            { "configuration_resolved", resolvedConfiguration },
        };

        WebhookDeliveryCounter.Add(1, tags);
        WebhookDeliveryDuration.Record(stopwatch.Elapsed.TotalSeconds, tags);
        return decision;
    }

    private static WebhookRoutingDecision CreateRejectedDecision(int httpStatusCode, string? reason)
    {
        return NormalizeDecision(new WebhookRoutingDecision(WebhookDeliveryOutcome.Rejected, httpStatusCode, null, [], reason));
    }

    private static WebhookRoutingDecision NormalizeDecision(WebhookRoutingDecision decision)
    {
        return decision with
        {
            ActionSummaries = decision.ActionSummaries
                .Select(summary => NormalizeDiagnosticText(summary, MaxDeliveryActionSummaryLength))
                .Where(summary => !string.IsNullOrWhiteSpace(summary))
                .Take(MaxDeliveryActionSummaries)
                .Cast<string>()
                .ToList()
                .AsReadOnly(),
            FailureReason = NormalizeDiagnosticText(decision.FailureReason, MaxFailureReasonLength),
        };
    }

    private static string? NormalizeDiagnosticText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = Regex.Replace(value.Trim(), "\\s+", " ");
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength].TrimEnd() + "...";
    }

    [LoggerMessage(
        EventId = 2820,
        Level = LogLevel.Warning,
        Message = "Rejected provider webhook delivery for unknown path key {PathKey} and provider {Provider}.")]
    private static partial void LogUnknownPathRejected(ILogger logger, ScmProvider provider, string pathKey);

    [LoggerMessage(
        EventId = 2821,
        Level = LogLevel.Warning,
        Message = "Malformed provider webhook delivery for configuration {ConfigurationId} and provider {Provider}.")]
    private static partial void LogMalformedResolvedDelivery(
        ILogger logger,
        Guid configurationId,
        ScmProvider provider);

    [LoggerMessage(
        EventId = 2822,
        Level = LogLevel.Information,
        Message =
            "Accepted provider webhook delivery for configuration {ConfigurationId}, provider {Provider}, and event {EventType}.")]
    private static partial void LogResolvedDeliveryAccepted(
        ILogger logger,
        Guid configurationId,
        ScmProvider provider,
        string eventType);

    [LoggerMessage(
        EventId = 2823,
        Level = LogLevel.Warning,
        Message =
            "Rejected provider webhook delivery for configuration {ConfigurationId}, provider {Provider}, and HTTP status {StatusCode}: {Reason}.")]
    private static partial void LogResolvedDeliveryRejected(
        ILogger logger,
        Guid configurationId,
        ScmProvider provider,
        int statusCode,
        string reason);

    [LoggerMessage(
        EventId = 2824,
        Level = LogLevel.Error,
        Message =
            "Provider webhook delivery for configuration {ConfigurationId} and provider {Provider} failed during downstream processing: {ErrorType}.")]
    private static partial void LogResolvedDeliveryFailed(
        ILogger logger,
        Guid configurationId,
        ScmProvider provider,
        string errorType);

    [LoggerMessage(
        EventId = 2825,
        Level = LogLevel.Information,
        Message =
            "Refreshed invalid webhook review revision for provider {Provider} review {ExternalReviewId} (#{ReviewNumber}) using live provider metadata.")]
    private static partial void LogRefreshedWebhookRevision(
        ILogger logger,
        ScmProvider provider,
        string externalReviewId,
        int reviewNumber);

    private sealed record ProviderWebhookClassification(
        WebhookEventType EventType,
        string SummaryLabel,
        PrStatus PullRequestStatus,
        bool RequiresLifecycleSync);
}
