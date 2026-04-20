// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.Wiki.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps.ProCursor;

/// <summary>
///     Materializes a git-backed Azure DevOps wiki for ProCursor indexing.
/// </summary>
public partial class AdoWikiMaterializer(
    VssConnectionFactory connectionFactory,
    IClientScmConnectionRepository connectionRepository,
    IOptions<ProCursorOptions> options,
    ILogger<AdoWikiMaterializer> logger)
#pragma warning disable CS9107 // Parameter is captured into the state of the enclosing type and its value is also passed to the base constructor. The value might be captured by the base class as well.
    : AdoGitProCursorMaterializerBase(connectionFactory, connectionRepository, options, logger)
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

    /// <inheritdoc />
    protected internal override async Task<string> ResolveRepositoryIdAsync(
        ProCursorKnowledgeSource source,
        CancellationToken ct)
    {
        var credentials = await this.ResolveConnectionCredentialsAsync(source, ct);

        var wikis = await this.ListWikisAsync(source, credentials, ct);
        return AdoWikiRepositoryResolver.ResolveRepositoryId(source, wikis);
    }

    /// <summary>
    ///     Lists all wikis for the given source.
    /// </summary>
    /// <param name="source">The ProCursor knowledge source.</param>
    /// <param name="credentials">The Azure DevOps service principal credentials.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A read-only list of wikis.</returns>
    protected internal virtual async Task<IReadOnlyList<WikiV2>> ListWikisAsync(
        ProCursorKnowledgeSource source,
        AdoServicePrincipalCredentials? credentials,
        CancellationToken ct)
    {
        var connection = await connectionFactory.GetConnectionAsync(source.ProviderScopePath, credentials, ct);
        var wikiClient = connection.GetClient<WikiHttpClient>();
        var wikis = await wikiClient.GetAllWikisAsync(source.ProviderProjectKey, null, ct);
        return wikis.ToList().AsReadOnly();
    }
}
