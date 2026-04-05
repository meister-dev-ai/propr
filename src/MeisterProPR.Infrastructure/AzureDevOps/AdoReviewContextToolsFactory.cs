// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>
///     Creates <see cref="AdoReviewContextTools" /> instances backed by Azure DevOps.
/// </summary>
public sealed class AdoReviewContextToolsFactory(
    VssConnectionFactory connectionFactory,
    IClientAdoCredentialRepository credentialRepository,
    IProCursorGateway proCursorGateway,
    IOptions<AiReviewOptions> options,
    ILoggerFactory loggerFactory) : IReviewContextToolsFactory
{
    /// <inheritdoc />
    public IReviewContextTools Create(
        string organizationUrl,
        string projectId,
        string repositoryId,
        string sourceBranch,
        int pullRequestId,
        int iterationId,
        Guid? clientId,
        IReadOnlyList<Guid>? knowledgeSourceIds = null)
    {
        return new AdoReviewContextTools(
            connectionFactory,
            credentialRepository,
            proCursorGateway,
            options,
            organizationUrl,
            projectId,
            repositoryId,
            sourceBranch,
            pullRequestId,
            iterationId,
            clientId,
            knowledgeSourceIds,
            loggerFactory.CreateLogger<AdoReviewContextTools>());
    }
}
