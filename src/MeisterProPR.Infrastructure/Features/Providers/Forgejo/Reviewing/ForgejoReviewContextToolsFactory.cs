// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;

internal sealed class ForgejoReviewContextToolsFactory(
    ForgejoConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    IProCursorGateway proCursorGateway,
    IOptions<AiReviewOptions> options,
    ILogger<ForgejoReviewContextTools> logger) : IProviderReviewContextToolsFactory
{
    public ScmProvider Provider => ScmProvider.Forgejo;

    public IReviewContextTools Create(ReviewContextToolsRequest request)
    {
        return new ForgejoReviewContextTools(
            connectionVerifier,
            httpClientFactory,
            proCursorGateway,
            options,
            request,
            logger);
    }
}
