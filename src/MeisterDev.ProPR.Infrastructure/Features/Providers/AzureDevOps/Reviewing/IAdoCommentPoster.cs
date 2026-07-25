// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

/// <summary>Provider-local seam for posting Azure DevOps review comments.</summary>
public interface IAdoCommentPoster
{
    Task<ReviewCommentPostingDiagnosticsDto> PostAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        ReviewResult result,
        Guid? clientId = null,
        IReadOnlyList<PrCommentThread>? existingThreads = null,
        AzureDevOpsPublicationContext? publicationContext = null,
        ReviewerIdentity? publicationIdentity = null,
        CancellationToken cancellationToken = default);
}
