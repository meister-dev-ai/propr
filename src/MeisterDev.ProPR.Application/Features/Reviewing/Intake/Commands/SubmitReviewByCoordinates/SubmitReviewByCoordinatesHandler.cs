// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Queries.ResolvePullRequest;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewByCoordinates;

/// <summary>
///     Starts a review of one pull request from its coordinates alone, for callers that cannot supply the
///     commit identity a review job needs.
/// </summary>
/// <remarks>
///     <para>
///         Review intake is addressed by revision: a job carries the base and head SHAs the review ran
///         against, and that is what tells one revision of a pull request from the next. A caller looking at
///         a pull request page knows its coordinates and nothing about its commits, and may have no
///         source-control credential of its own to go and ask. This command closes that gap by asking the
///         provider on the client's behalf, which is also why one request serves both the first review and
///         every re-review after new commits: the answer is read fresh each time.
///     </para>
///     <para>
///         The coordinates must fall inside a crawl or webhook configuration of the named client. That match
///         does two jobs. It is the authorization boundary, because without it a caller holding a role on one
///         client could point that client's credential at any host it liked; and it is the only authoritative
///         source of the provider family, which the coordinates do not carry. Matching is by exact agreement
///         on the scope path and project key, which is narrower than address-based resolution needs to be:
///         these values arrive already stored rather than parsed out of a web address. A repository inside a
///         covered scope is accepted without being named, because a configuration that names no repositories
///         covers all of them, and webhook filters record names where the caller has an identity.
///     </para>
///     <para>
///         Submission itself goes through the same shared synchronization path the crawler and webhooks use,
///         so duplicate detection, superseded-job cancellation, blocked pull requests and profile resolution
///         behave identically however a review was triggered. Only the two change-detection guards are
///         bypassed, and only because they exist to stop the automatic loop repeating itself: an explicit
///         request is the deliberate action they defer to. Budget is deliberately not checked here either. An
///         over-budget client is held at execution, where the crawl path holds it too, so one place decides
///         and one place explains.
///     </para>
/// </remarks>
public sealed partial class SubmitReviewByCoordinatesHandler(
    ICrawlConfigurationRepository crawlConfigurationRepository,
    IWebhookConfigurationRepository webhookConfigurationRepository,
    IScmProviderRegistry providerRegistry,
    IPullRequestSynchronizationService synchronizationService,
    ILogger<SubmitReviewByCoordinatesHandler> logger)
{
    private const string SummaryLabel = "an explicitly requested review";

    /// <summary>Resolves the pull request from its coordinates and queues a review of its current revision.</summary>
    /// <param name="command">The coordinates to review.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    ///     A named outcome, with the job to follow when one exists. A refusal is a normal answer here rather
    ///     than an exception, because the caller renders every one of them to a person.
    /// </returns>
    public async Task<SubmitReviewByCoordinatesResult> HandleAsync(
        SubmitReviewByCoordinatesCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var coverage = await this.FindCoveringConfigurationAsync(command, cancellationToken);
        if (coverage is null)
        {
            LogNotCovered(logger, command.ClientId, command.RepositoryId, command.PullRequestId);
            return new SubmitReviewByCoordinatesResult(
                SubmitReviewByCoordinatesOutcome.NotAuthorized,
                Reason: "No crawl or webhook configuration of this client covers the supplied coordinates.");
        }

        var host = new ProviderHostRef(coverage.Provider, coverage.ProviderScopePath);
        var repository = await this.ResolveRepositoryAsync(coverage, host, command, cancellationToken);
        var review = new CodeReviewRef(
            repository,
            CodeReviewPlatformKind.PullRequest,
            command.PullRequestId.ToString(CultureInfo.InvariantCulture),
            command.PullRequestId);

        ReviewDiscoveryItemDto? item;
        ICodeReviewQueryService queryService;
        try
        {
            queryService = providerRegistry.GetCodeReviewQueryService(coverage.Provider);

            // Ask for the pull request before its revision. This one call confirms the pull request exists,
            // yields the title, branches and lifecycle state the job carries as context, and on most
            // providers already includes the revision, so the common path needs no second round trip.
            item = await queryService.GetReviewAsync(command.ClientId, review, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProviderUnreachable(logger, command.ClientId, coverage.Provider.ToString(), command.PullRequestId, ex);
            return UnresolvableRevision();
        }

        if (item is null)
        {
            return new SubmitReviewByCoordinatesResult(
                SubmitReviewByCoordinatesOutcome.PullRequestNotFound,
                Reason: $"The provider reports no pull request #{command.PullRequestId} in this repository.");
        }

        if (item.ReviewState is not (CodeReviewState.Open or CodeReviewState.Draft))
        {
            return new SubmitReviewByCoordinatesResult(
                SubmitReviewByCoordinatesOutcome.NotSubmittable,
                Reason: $"Pull request #{command.PullRequestId} is {item.ReviewState.ToString().ToLowerInvariant()} and cannot be reviewed.");
        }

        ReviewRevision? revision;
        try
        {
            // The reference the adapter answered with is the one it can act on; the one built from
            // coordinates was a best effort that the adapter may have corrected while answering.
            revision = item.ReviewRevision
                       ?? await queryService.GetLatestRevisionAsync(command.ClientId, item.CodeReview, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProviderUnreachable(logger, command.ClientId, coverage.Provider.ToString(), command.PullRequestId, ex);
            return UnresolvableRevision();
        }

        if (revision is null)
        {
            return UnresolvableRevision();
        }

        PullRequestSynchronizationOutcome outcome;
        try
        {
            outcome = await synchronizationService.SynchronizeAsync(
                new PullRequestSynchronizationRequest
                {
                    ActivationSource = PullRequestActivationSource.Manual,
                    SummaryLabel = SummaryLabel,
                    ClientId = command.ClientId,
                    ProviderScopePath = coverage.ProviderScopePath,
                    ProviderProjectKey = coverage.ProviderProjectKey,
                    RepositoryId = item.Repository.ExternalRepositoryId,
                    PullRequestId = item.CodeReview.Number,
                    PullRequestStatus = PrStatus.Active,
                    Provider = coverage.Provider,
                    Host = host,
                    Repository = item.Repository,
                    CodeReview = item.CodeReview,
                    ReviewRevision = revision,
                    ReviewState = item.ReviewState,
                    AllowUnchangedResubmission = true,
                    PrTitle = item.Title,

                    // A repository reference falls back to the provider identity when nobody supplied a
                    // name, so passing it on unchecked would show a bare number where a name belongs.
                    RepositoryName = string.Equals(
                        item.Repository.RepositoryName,
                        item.Repository.ExternalRepositoryId,
                        StringComparison.Ordinal)
                        ? null
                        : item.Repository.RepositoryName,
                    SourceBranch = item.SourceBranch,
                    TargetBranch = item.TargetBranch,
                    ProCursorSourceScopeMode = coverage.ProCursorSourceScopeMode,
                    ProCursorSourceIds = coverage.ProCursorSourceIds ?? [],
                    InvalidProCursorSourceIds = coverage.InvalidProCursorSourceIds ?? [],
                    ReviewTemperature = coverage.ReviewTemperature,
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Everything the request asked about resolved; the failure is ours. Saying so plainly beats an
            // unhandled fault, which would reach the caller as a bare error with nothing to render and
            // nothing to distinguish it from the pull request itself being unreviewable.
            LogSubmissionFailed(logger, command.ClientId, command.PullRequestId, ex);
            return new SubmitReviewByCoordinatesResult(
                SubmitReviewByCoordinatesOutcome.SubmissionFailed,
                Reason: "The pull request was resolved, but queueing the review failed. Try again; if it keeps failing, the server logs carry the detail.");
        }

        return MapOutcome(outcome);
    }

    private static SubmitReviewByCoordinatesResult UnresolvableRevision()
    {
        return new SubmitReviewByCoordinatesResult(
            SubmitReviewByCoordinatesOutcome.RevisionUnresolvable,
            Reason: "The provider could not be asked for this pull request's current revision. Check the client's source-control connection and try again.");
    }

    /// <summary>Translates a synchronization decision into the answer the caller renders.</summary>
    private static SubmitReviewByCoordinatesResult MapOutcome(PullRequestSynchronizationOutcome outcome)
    {
        return outcome.ReviewDecision switch
        {
            PullRequestSynchronizationReviewDecision.Submitted =>
                new SubmitReviewByCoordinatesResult(SubmitReviewByCoordinatesOutcome.Submitted, outcome.JobId),
            PullRequestSynchronizationReviewDecision.DuplicateActiveJob =>
                new SubmitReviewByCoordinatesResult(
                    SubmitReviewByCoordinatesOutcome.DuplicateActiveJob,
                    outcome.JobId,
                    "A review of this exact revision is already running."),

            // Everything else declined to queue work and said why in its action summaries. Those sentences
            // are already written for a person, so they are the reason rather than a restatement of it.
            _ => new SubmitReviewByCoordinatesResult(
                SubmitReviewByCoordinatesOutcome.NotSubmittable,
                outcome.JobId,
                DescribeRefusal(outcome)),
        };
    }

    private static string DescribeRefusal(PullRequestSynchronizationOutcome outcome)
    {
        return outcome.ActionSummaries.Count > 0
            ? string.Join(' ', outcome.ActionSummaries)
            : "The pull request could not be submitted for review.";
    }

    /// <summary>
    ///     Finds the configuration that both authorizes this request and names the provider family it runs
    ///     against, preferring one that describes the repository.
    /// </summary>
    private async Task<PullRequestCoverage?> FindCoveringConfigurationAsync(
        SubmitReviewByCoordinatesCommand command,
        CancellationToken cancellationToken)
    {
        Guid[] clientIds = [command.ClientId];
        var crawlConfigurations = await crawlConfigurationRepository.GetByClientIdsAsync(clientIds, cancellationToken);
        var webhookConfigurations = await webhookConfigurationRepository.GetByClientIdsAsync(clientIds, cancellationToken);

        var coverages = new List<PullRequestCoverage>(crawlConfigurations.Count + webhookConfigurations.Count);
        coverages.AddRange(crawlConfigurations.Select(PullRequestCoverage.FromCrawlConfiguration));
        coverages.AddRange(webhookConfigurations.Select(PullRequestCoverage.FromWebhookConfiguration));

        return coverages
            // Scoping the read to the client is not enough on its own: a repository that over-returns would
            // otherwise let one client's request run under another client's configuration.
            .Where(coverage => coverage.ClientId == command.ClientId
                               && Matches(coverage.ProviderScopePath, command.ProviderScopePath)
                               && Matches(coverage.ProviderProjectKey, command.ProviderProjectKey)
                               && CoversRepository(coverage, command.RepositoryId))
            // An inactive configuration still authorizes. Switching a crawl or webhook configuration off
            // means "stop starting reviews by yourself", not "revoke the right to ask for one", and asking
            // by hand against an otherwise idle configuration is a mode people deliberately run in.
            .OrderByDescending(coverage => FindNamedRepository(coverage, command.RepositoryId) is not null)
            .ThenByDescending(coverage => coverage.IsActive)
            .FirstOrDefault();
    }

    /// <summary>
    ///     Decides whether a configuration that matches the scope covers this particular repository.
    /// </summary>
    /// <remarks>
    ///     Verify when verifiable. A configuration that names repositories <em>and</em> recorded their
    ///     provider identities can answer the question exactly, and is held to it: accepting an unnamed
    ///     repository there would let one covered repository's coordinates carry a request aimed at any
    ///     other repository in the scope. A configuration that names nothing covers its whole scope by
    ///     definition, and a configuration whose named repositories carry no identity cannot be checked at
    ///     all, because webhook filters record names where the caller only has an identity. Refusing those
    ///     two would reject requests that are legitimate, so the check applies exactly where it can decide.
    /// </remarks>
    private static bool CoversRepository(PullRequestCoverage coverage, string repositoryId)
    {
        if (coverage.CoveredRepositories.Count == 0)
        {
            return true;
        }

        var identified = coverage.CoveredRepositories
            .Where(repository => !string.IsNullOrWhiteSpace(repository.ExternalRepositoryId))
            .ToList();

        return identified.Count == 0
               || identified.Any(repository =>
                   string.Equals(repository.ExternalRepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Fills in the repository name the coordinates may not carry, and the project path that is built
    ///     from it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         There is no single shape for "the repository identity". Azure DevOps stores a GUID and reads
    ///         the project path as the project itself. GitHub, Forgejo and GitLab all address a repository as
    ///         <c>owner/name</c> and derive it from the project path, but what they store as the identity
    ///         differs even within one provider: GitHub and Forgejo store a number, GitLab's discovery
    ///         reports a number while a GitLab review job records the namespaced path. A request that assumed
    ///         any one of those shapes would ask some host for <c>owner/12345</c>, or drop the name entirely
    ///         and produce a clone URL with no repository in it.
    ///     </para>
    ///     <para>
    ///         So the name is resolved cheapest-first, and every rung has to tolerate all three shapes: the
    ///         covering configuration, which often recorded the name; the identity itself when it is already
    ///         a path, which needs no lookup and cannot be wrong; repository discovery, whose answer is the
    ///         adapter's own and is taken whole; and finally the project key alone, which is right for Azure
    ///         DevOps and is the best remaining answer elsewhere.
    ///     </para>
    /// </remarks>
    private async Task<RepositoryRef> ResolveRepositoryAsync(
        PullRequestCoverage coverage,
        ProviderHostRef host,
        SubmitReviewByCoordinatesCommand command,
        CancellationToken cancellationToken)
    {
        var named = FindNamedRepository(coverage, command.RepositoryId);
        if (named is not null && !string.IsNullOrWhiteSpace(named.Name))
        {
            return BuildRepositoryRef(coverage, host, command.RepositoryId, named.Name.Trim());
        }

        // An identity that is already a path carries the name in its last segment, which is what GitLab
        // records on a review job. Reading it costs nothing and cannot miss, so it comes before asking the
        // provider. Identities that are a GUID or a number contain no slash and fall through untouched.
        if (command.RepositoryId.Contains('/', StringComparison.Ordinal))
        {
            return BuildRepositoryRef(coverage, host, command.RepositoryId, command.RepositoryId);
        }

        var discovered = await this.TryDiscoverRepositoryAsync(coverage, host, command.RepositoryId, cancellationToken);
        if (discovered is not null)
        {
            return discovered;
        }

        return new RepositoryRef(host, command.RepositoryId, coverage.ProviderProjectKey, coverage.ProviderProjectKey);
    }

    private static RepositoryRef BuildRepositoryRef(
        PullRequestCoverage coverage,
        ProviderHostRef host,
        string repositoryId,
        string repositoryName)
    {
        if (coverage.Provider == ScmProvider.AzureDevOps)
        {
            // Azure DevOps reads the project path as the project itself, both for its API calls and for the
            // clone URL it builds from it, so the repository name belongs in the name field and nowhere else.
            return new RepositoryRef(
                host,
                repositoryId,
                coverage.ProviderProjectKey,
                coverage.ProviderProjectKey,
                LastSegment(repositoryName));
        }

        // The Git-hosting families address a repository as owner plus the last segment of the project path,
        // so a name recorded on its own has to be qualified with the owner the project key holds.
        var projectPath = repositoryName.Contains('/', StringComparison.Ordinal)
            ? repositoryName
            : $"{coverage.ProviderProjectKey}/{repositoryName}";

        return new RepositoryRef(host, repositoryId, coverage.ProviderProjectKey, projectPath, LastSegment(repositoryName));
    }

    private async Task<RepositoryRef?> TryDiscoverRepositoryAsync(
        PullRequestCoverage coverage,
        ProviderHostRef host,
        string repositoryId,
        CancellationToken cancellationToken)
    {
        if (!providerRegistry.IsRegistered(coverage.Provider))
        {
            return null;
        }

        try
        {
            var discovery = providerRegistry.GetRepositoryDiscoveryProvider(coverage.Provider);
            var repositories = await discovery.ListRepositoriesAsync(
                coverage.ClientId,
                host,
                coverage.ProviderProjectKey,
                cancellationToken);

            // Match on more than the identity, most specific first. Providers disagree about what they
            // store as one — GitLab discovery answers with a numeric project id while a GitLab review job
            // records the namespaced path — so comparing identities alone finds nothing for the very
            // repository that was asked about, and the caller then falls back to an answer carrying no
            // repository name at all.
            return Match(repositories, repository => repository.ExternalRepositoryId)
                   ?? Match(repositories, repository => repository.ProjectPath)
                   ?? Match(repositories, repository => LastSegment(repository.ProjectPath));

            RepositoryRef? Match(IReadOnlyList<RepositoryRef> candidates, Func<RepositoryRef, string> identity)
            {
                return candidates.FirstOrDefault(candidate =>
                    string.Equals(identity(candidate), repositoryId, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Discovery is one way of learning a name, not an answer about the pull request. Failing the
            // whole request here would invent a failure the provider has not been asked about yet.
            LogDiscoveryFailed(logger, coverage.ClientId, coverage.Provider.ToString(), repositoryId, ex);
            return null;
        }
    }

    private static CoveredRepository? FindNamedRepository(PullRequestCoverage coverage, string repositoryId)
    {
        return coverage.CoveredRepositories.FirstOrDefault(repository =>
            string.Equals(repository.ExternalRepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Matches(string? configured, string? requested)
    {
        return string.Equals(configured?.Trim(), requested?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string LastSegment(string value)
    {
        var trimmed = value.Trim().TrimEnd('/');
        var separator = trimmed.LastIndexOf('/');
        return separator < 0 ? trimmed : trimmed[(separator + 1)..];
    }

    [LoggerMessage(
        EventId = 6311,
        Level = LogLevel.Information,
        Message =
            "No configuration of client {ClientId} covers repository {RepositoryId} pull request {PullRequestId}; refusing the review request.")]
    private static partial void LogNotCovered(ILogger logger, Guid clientId, string repositoryId, int pullRequestId);

    [LoggerMessage(
        EventId = 6312,
        Level = LogLevel.Warning,
        Message =
            "Could not resolve pull request {PullRequestId} on {Provider} for client {ClientId}; the review request is refused as unresolvable.")]
    private static partial void LogProviderUnreachable(
        ILogger logger,
        Guid clientId,
        string provider,
        int pullRequestId,
        Exception exception);

    [LoggerMessage(
        EventId = 6314,
        Level = LogLevel.Error,
        Message =
            "Queueing the requested review of pull request {PullRequestId} for client {ClientId} failed after the pull request had been resolved.")]
    private static partial void LogSubmissionFailed(
        ILogger logger,
        Guid clientId,
        int pullRequestId,
        Exception exception);

    [LoggerMessage(
        EventId = 6313,
        Level = LogLevel.Debug,
        Message =
            "Repository discovery could not describe {RepositoryId} on {Provider} for client {ClientId}; continuing with the configured project key.")]
    private static partial void LogDiscoveryFailed(
        ILogger logger,
        Guid clientId,
        string provider,
        string repositoryId,
        Exception exception);
}
