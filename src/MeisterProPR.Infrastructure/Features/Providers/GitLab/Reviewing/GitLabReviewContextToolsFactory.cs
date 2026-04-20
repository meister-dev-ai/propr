// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

internal sealed class GitLabReviewContextToolsFactory(
    GitLabConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    IProCursorGateway proCursorGateway,
    IOptions<AiReviewOptions> options,
    ILogger<GitLabReviewContextTools> logger) : IProviderReviewContextToolsFactory
{
    public ScmProvider Provider => ScmProvider.GitLab;

    public IReviewContextTools Create(ReviewContextToolsRequest request)
    {
        return new GitLabReviewContextTools(
            connectionVerifier,
            httpClientFactory,
            proCursorGateway,
            options,
            request,
            logger);
    }
}
