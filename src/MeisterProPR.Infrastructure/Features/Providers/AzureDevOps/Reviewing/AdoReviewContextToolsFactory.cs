// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

/// <summary>
///     Creates <see cref="AdoReviewContextTools" /> instances backed by Azure DevOps.
/// </summary>
public sealed class AdoReviewContextToolsFactory(
    VssConnectionFactory connectionFactory,
    IClientScmConnectionRepository connectionRepository,
    IProCursorGateway proCursorGateway,
    IOptions<AiReviewOptions> options,
    ILoggerFactory loggerFactory) : IReviewContextToolsFactory, IProviderReviewContextToolsFactory
{
    public ScmProvider Provider => ScmProvider.AzureDevOps;

    /// <inheritdoc />
    public IReviewContextTools Create(ReviewContextToolsRequest request)
    {
        var repository = request.CodeReview.Repository;
        return new AdoReviewContextTools(
            connectionFactory,
            connectionRepository,
            proCursorGateway,
            options,
            request.ProviderScopePath ?? repository.Host.HostBaseUrl,
            repository.OwnerOrNamespace,
            repository.ExternalRepositoryId,
            request.SourceBranch,
            request.CodeReview.Number,
            request.IterationId,
            request.ClientId,
            request.KnowledgeSourceIds,
            loggerFactory.CreateLogger<AdoReviewContextTools>());
    }
}
