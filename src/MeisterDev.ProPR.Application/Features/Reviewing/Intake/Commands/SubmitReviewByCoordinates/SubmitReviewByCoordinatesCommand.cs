// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewByCoordinates;

/// <summary>Asks for a review of one pull request, named only by the coordinates ProPR already stores.</summary>
/// <remarks>
///     These are exactly the five values pull-request resolution hands back, so a caller that can address a
///     pull request can trigger its review without learning anything else about it. No commit identity
///     appears here on purpose: the SHAs are read from the provider at submission time, which is what makes
///     one request serve both a first review and a re-review after new commits.
/// </remarks>
/// <param name="ClientId">The client whose configuration and credential the review runs under.</param>
/// <param name="ProviderScopePath">Scope path exactly as the covering configuration stores it.</param>
/// <param name="ProviderProjectKey">Project, workspace, or namespace key exactly as the covering configuration stores it.</param>
/// <param name="RepositoryId">Provider repository identity.</param>
/// <param name="PullRequestId">Pull request number as the provider numbers it.</param>
public sealed record SubmitReviewByCoordinatesCommand(
    Guid ClientId,
    string ProviderScopePath,
    string ProviderProjectKey,
    string RepositoryId,
    int PullRequestId);
