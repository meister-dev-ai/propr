// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.AzureDevOps.ProCursor;

/// <summary>
///     Materializes a git-backed Azure DevOps repository for ProCursor indexing.
/// </summary>
public partial class AdoRepositoryMaterializer(
    VssConnectionFactory connectionFactory,
    IClientScmConnectionRepository connectionRepository,
    IOptions<ProCursorOptions> options,
    ILogger<AdoRepositoryMaterializer> logger)
    : AdoGitProCursorMaterializerBase(connectionFactory, connectionRepository, options, logger)
{
    /// <inheritdoc />
    public override ProCursorSourceKind SourceKind => ProCursorSourceKind.Repository;

    /// <inheritdoc />
    protected override void LogMaterializedSource(
        ILogger logger,
        Guid sourceId,
        string branchName,
        string commitSha,
        int fileCount)
    {
        LogMaterializedRepositorySource(logger, sourceId, branchName, commitSha, fileCount);
    }
}
