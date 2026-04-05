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
    IClientAdoCredentialRepository credentialRepository,
    IOptions<ProCursorOptions> options,
    ILogger<AdoWikiMaterializer> logger)
    : AdoGitProCursorMaterializerBase(connectionFactory, credentialRepository, options, logger)
{
    /// <inheritdoc />
    public override ProCursorSourceKind SourceKind => ProCursorSourceKind.AdoWiki;

    protected override void LogMaterializedSource(ILogger logger, Guid sourceId, string branchName, string commitSha, int fileCount)
    {
        LogMaterializedWikiSource(logger, sourceId, branchName, commitSha, fileCount);
    }

    protected internal override async Task<string> ResolveRepositoryIdAsync(
        ProCursorKnowledgeSource source,
        CancellationToken ct)
    {
        var credentials = source.ClientId != Guid.Empty
            ? await credentialRepository.GetByClientIdAsync(source.ClientId, ct)
            : null;

        var wikis = await this.ListWikisAsync(source, credentials, ct);
        return AdoWikiRepositoryResolver.ResolveRepositoryId(source, wikis);
    }

    protected internal virtual async Task<IReadOnlyList<WikiV2>> ListWikisAsync(
        ProCursorKnowledgeSource source,
        ClientAdoCredentials? credentials,
        CancellationToken ct)
    {
        var connection = await connectionFactory.GetConnectionAsync(source.OrganizationUrl, credentials, ct);
        var wikiClient = connection.GetClient<WikiHttpClient>();
        var wikis = await wikiClient.GetAllWikisAsync(source.ProjectId, null, ct);
        return wikis.ToList().AsReadOnly();
    }
}
