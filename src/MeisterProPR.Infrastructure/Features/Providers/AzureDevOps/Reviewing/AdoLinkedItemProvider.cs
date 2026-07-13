// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

/// <summary>
///     Retrieves the Azure DevOps work items linked to a pull request. Uses the PR-to-work-item
///     reference list and the work-item-tracking client, both obtained from the same authenticated
///     connection the review already uses. Fails soft: any error yields an empty result.
/// </summary>
public sealed partial class AdoLinkedItemProvider(
    VssConnectionFactory connectionFactory,
    IClientScmConnectionRepository connectionRepository,
    ILogger<AdoLinkedItemProvider> logger) : ILinkedItemProvider
{
    private const string TitleField = "System.Title";
    private const string TypeField = "System.WorkItemType";
    private const string DescriptionField = "System.Description";
    private const string StateField = "System.State";
    private const string WorkItemApiSegment = "/_apis/wit/workItems/";

    // Azure DevOps rejects a GetWorkItemsAsync batch larger than 200 ids; cap so a PR linked to more
    // than that degrades to the first 200 (then the eager count cap trims further) rather than losing all.
    private const int MaxWorkItemBatch = 200;

    public ScmProvider Provider => ScmProvider.AzureDevOps;

    public async Task<IReadOnlyList<LinkedItem>> DiscoverLinkedItemsAsync(
        Guid clientId,
        PullRequest pullRequest,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (gitClient, witClient) = await this.ResolveClientsAsync(clientId, pullRequest.OrganizationUrl, cancellationToken);

            var refs = await gitClient.GetPullRequestWorkItemRefsAsync(
                pullRequest.ProjectId,
                pullRequest.RepositoryId,
                pullRequest.PullRequestId,
                cancellationToken: cancellationToken);

            var ids = (refs ?? [])
                .Select(r => int.TryParse(r.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (int?)null)
                .OfType<int>()
                .Distinct()
                .Take(MaxWorkItemBatch)
                .ToList();

            if (ids.Count == 0)
            {
                return [];
            }

            var workItems = await witClient.GetWorkItemsAsync(
                ids,
                expand: WorkItemExpand.Relations,
                errorPolicy: WorkItemErrorPolicy.Omit,
                cancellationToken: cancellationToken);

            return (workItems ?? [])
                .Where(w => w?.Id is not null)
                .Select(MapSummary)
                .ToList()
                .AsReadOnly();
        }
        catch (Exception ex)
        {
            LogDiscoveryFailed(logger, pullRequest.PullRequestId, ex);
            return [];
        }
    }

    public async Task<LinkedItemDetails?> GetItemDetailsAsync(
        Guid clientId,
        PullRequest pullRequest,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseId(providerKey, out var id))
        {
            return null;
        }

        try
        {
            var (_, witClient) = await this.ResolveClientsAsync(clientId, pullRequest.OrganizationUrl, cancellationToken);
            var workItem = await witClient.GetWorkItemAsync(
                id,
                expand: WorkItemExpand.All,
                cancellationToken: cancellationToken);

            if (workItem is null)
            {
                return null;
            }

            var fields = (workItem.Fields ?? new Dictionary<string, object>())
                .ToDictionary(kvp => kvp.Key, kvp => Stringify(kvp.Value));

            return new LinkedItemDetails(
                providerKey,
                GetField(workItem.Fields, TypeField) ?? "WorkItem",
                GetField(workItem.Fields, TitleField) ?? providerKey,
                GetField(workItem.Fields, DescriptionField),
                GetField(workItem.Fields, StateField),
                fields,
                MapRelations(workItem.Relations));
        }
        catch (Exception ex)
        {
            LogItemFailed(logger, id, ex);
            return null;
        }
    }

    public async Task<IReadOnlyList<LinkedItemComment>> GetItemDiscussionAsync(
        Guid clientId,
        PullRequest pullRequest,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseId(providerKey, out var id))
        {
            return [];
        }

        try
        {
            var (_, witClient) = await this.ResolveClientsAsync(clientId, pullRequest.OrganizationUrl, cancellationToken);
            var comments = await witClient.GetCommentsAsync(
                pullRequest.ProjectId,
                id,
                cancellationToken: cancellationToken);

            return (comments?.Comments ?? [])
                .Select(c => new LinkedItemComment(
                    c.CreatedBy?.DisplayName ?? "Unknown",
                    c.CreatedDate != default ? new DateTimeOffset(c.CreatedDate, TimeSpan.Zero) : null,
                    c.Text ?? string.Empty))
                .ToList()
                .AsReadOnly();
        }
        catch (Exception ex)
        {
            LogDiscussionFailed(logger, id, ex);
            return [];
        }
    }

    public async Task<LinkedItem?> ResolveRelatedLinkAsync(
        Guid clientId,
        PullRequest pullRequest,
        string relatedTargetKey,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseId(relatedTargetKey, out var id))
        {
            return null;
        }

        try
        {
            var (_, witClient) = await this.ResolveClientsAsync(clientId, pullRequest.OrganizationUrl, cancellationToken);
            var workItem = await witClient.GetWorkItemAsync(
                id,
                expand: WorkItemExpand.Relations,
                cancellationToken: cancellationToken);

            return workItem?.Id is null ? null : MapSummary(workItem);
        }
        catch (Exception ex)
        {
            LogItemFailed(logger, id, ex);
            return null;
        }
    }

    private static LinkedItem MapSummary(WorkItem workItem)
    {
        var key = workItem.Id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        return new LinkedItem(
            key,
            GetField(workItem.Fields, TypeField) ?? "WorkItem",
            GetField(workItem.Fields, TitleField) ?? key,
            GetField(workItem.Fields, DescriptionField),
            workItem.Url,
            MapRelations(workItem.Relations));
    }

    private static IReadOnlyList<LinkedItemRef> MapRelations(IList<WorkItemRelation>? relations)
    {
        if (relations is null || relations.Count == 0)
        {
            return [];
        }

        var result = new List<LinkedItemRef>();
        foreach (var relation in relations)
        {
            // Only work-item links are resolvable; artifact/hyperlink relations yield null and are skipped.
            if (ExtractRelationTargetKey(relation.Url) is { } targetKey)
            {
                result.Add(new LinkedItemRef(FriendlyRelation(relation.Rel), targetKey, relation.Url));
            }
        }

        return result.AsReadOnly();
    }

    // Extracts the numeric work-item id from a relation URL such as ".../_apis/wit/workItems/123", tolerating
    // trailing sub-resources the SDK can append (".../workItems/123/updates"). Returns null for non-work-item
    // relations (artifact links, hyperlinks) or when no numeric id is present.
    internal static string? ExtractRelationTargetKey(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var segment = url.IndexOf(WorkItemApiSegment, StringComparison.OrdinalIgnoreCase);
        if (segment < 0)
        {
            return null;
        }

        var tail = url[(segment + WorkItemApiSegment.Length)..].Trim('/');
        var slash = tail.IndexOf('/');
        var id = slash >= 0 ? tail[..slash] : tail;
        return id.Length > 0 && id.All(char.IsDigit) ? id : null;
    }

    private static string FriendlyRelation(string? rel)
    {
        if (string.IsNullOrEmpty(rel))
        {
            return "related";
        }

        // "System.LinkTypes.Hierarchy-Forward" -> "hierarchy-forward"; keep the tail after the last dot.
        var lastDot = rel.LastIndexOf('.');
        var tail = lastDot >= 0 && lastDot < rel.Length - 1 ? rel[(lastDot + 1)..] : rel;
        return tail.ToLowerInvariant();
    }

    private static string? GetField(IDictionary<string, object>? fields, string key)
    {
        if (fields is not null && fields.TryGetValue(key, out var value))
        {
            return Stringify(value);
        }

        return null;
    }

    private static string Stringify(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static bool TryParseId(string providerKey, out int id)
    {
        return int.TryParse(providerKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    private async Task<(GitHttpClient GitClient, WorkItemTrackingHttpClient WitClient)> ResolveClientsAsync(
        Guid clientId,
        string organizationUrl,
        CancellationToken cancellationToken)
    {
        var credentials = await AdoProviderAdapterHelpers.ResolveCredentialsAsync(
            connectionRepository,
            clientId,
            organizationUrl,
            cancellationToken);
        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, cancellationToken);
        await connection.ConnectAsync(cancellationToken);
        var gitClient = await connection.GetClientAsync<GitHttpClient>(cancellationToken);
        var witClient = await connection.GetClientAsync<WorkItemTrackingHttpClient>(cancellationToken);
        return (gitClient, witClient);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Failed to discover linked work items for PR #{PullRequestId}. Proceeding without linked-item context.")]
    private static partial void LogDiscoveryFailed(ILogger logger, int pullRequestId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Failed to fetch linked work item {WorkItemId}.")]
    private static partial void LogItemFailed(ILogger logger, int workItemId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Failed to fetch discussion for linked work item {WorkItemId}.")]
    private static partial void LogDiscussionFailed(ILogger logger, int workItemId, Exception ex);
}
