// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

internal sealed class GitHubCodeReviewPublicationService : ICodeReviewPublicationService
{
    private readonly GitHubLifecyclePublicationService _lifecyclePublicationService;

    public GitHubCodeReviewPublicationService(
        GitHubConnectionVerifier connectionVerifier,
        IHttpClientFactory httpClientFactory)
        : this(new GitHubLifecyclePublicationService(connectionVerifier, httpClientFactory))
    {
    }

    internal GitHubCodeReviewPublicationService(GitHubLifecyclePublicationService lifecyclePublicationService)
    {
        this._lifecyclePublicationService = lifecyclePublicationService;
    }

    public ScmProvider Provider => ScmProvider.GitHub;

    public async Task<ReviewCommentPostingDiagnosticsDto> PublishReviewAsync(
        Guid clientId,
        CodeReviewRef review,
        ReviewRevision revision,
        ReviewResult result,
        ReviewerIdentity reviewer,
        CancellationToken ct = default)
    {
        return await this._lifecyclePublicationService.PublishReviewAsync(
            clientId,
            review,
            revision,
            result,
            reviewer,
            ct);
    }
}
