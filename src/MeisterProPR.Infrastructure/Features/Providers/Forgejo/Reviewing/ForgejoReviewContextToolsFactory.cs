// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.CodeAnalysis;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;

internal sealed class ForgejoReviewContextToolsFactory(
    IProCursorGateway proCursorGateway,
    IOptions<AiReviewOptions> options,
    ILoggerFactory loggerFactory,
    IScmProviderRegistry providerRegistry,
    IStructuralCodeAnalyzer? structuralAnalyzer = null) : IProviderReviewContextToolsFactory
{
    public ScmProvider Provider => ScmProvider.Forgejo;

    public IReviewContextTools Create(ReviewContextToolsRequest request)
    {
        if (request.Workspace is null)
        {
            throw new InvalidOperationException("A local review workspace is required but was not provided to the review context tools factory.");
        }

        return new LocalGitReviewContextTools(
            request.Workspace,
            proCursorGateway,
            options,
            request,
            loggerFactory.CreateLogger<LocalGitReviewContextTools>(),
            structuralAnalyzer,
            providerRegistry);
    }
}
