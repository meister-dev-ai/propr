// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

internal sealed class GitHubReviewContextToolsFactory(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    IProCursorGateway proCursorGateway,
    IOptions<AiReviewOptions> options,
    ILogger<GitHubReviewContextTools> logger) : IProviderReviewContextToolsFactory
{
    public ScmProvider Provider => ScmProvider.GitHub;

    public IReviewContextTools Create(ReviewContextToolsRequest request)
    {
        return new GitHubReviewContextTools(
            connectionVerifier,
            httpClientFactory,
            proCursorGateway,
            options,
            request,
            logger);
    }
}
