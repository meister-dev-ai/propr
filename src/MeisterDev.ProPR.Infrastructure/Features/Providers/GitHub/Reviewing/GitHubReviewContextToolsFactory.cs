// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.CodeAnalysis;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

internal sealed class GitHubReviewContextToolsFactory(
    IProCursorGateway proCursorGateway,
    IOptions<AiReviewOptions> options,
    ILoggerFactory loggerFactory,
    IScmProviderRegistry providerRegistry,
    IStructuralCodeAnalyzer? structuralAnalyzer = null) : IProviderReviewContextToolsFactory
{
    public ScmProvider Provider => ScmProvider.GitHub;

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
