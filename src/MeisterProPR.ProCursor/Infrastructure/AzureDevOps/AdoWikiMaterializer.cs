// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.ProCursor.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.AzureDevOps.ProCursor;

/// <summary>
///     Materializes a git-backed Azure DevOps wiki for ProCursor indexing.
/// </summary>
public partial class AdoWikiMaterializer(
    IProCursorScmBroker scmBroker,
    IOptions<ProCursorOptions> options,
    ILogger<AdoWikiMaterializer> logger)
#pragma warning disable CS9107 // Parameter is captured into the state of the enclosing type and its value is also passed to the base constructor. The value might be captured by the base class as well.
    : AdoGitProCursorMaterializerBase(scmBroker, options, logger)
#pragma warning restore CS9107 // Parameter is captured into the state of the enclosing type and its value is also passed to the base constructor. The value might be captured by the base class as well.
{
    /// <inheritdoc />
    public override ProCursorSourceKind SourceKind => ProCursorSourceKind.AdoWiki;

    /// <inheritdoc />
    protected override void LogMaterializedSource(
        ILogger logger,
        Guid sourceId,
        string branchName,
        string commitSha,
        int fileCount)
    {
        LogMaterializedWikiSource(logger, sourceId, branchName, commitSha, fileCount);
    }
}
