// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.Domain.Entities;
using System.Text;
using System.Text.Json;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

internal sealed class ReviewerPerformanceLabelResolver(
    IReadOnlyDictionary<Guid, string> clientNames,
    IReadOnlyDictionary<(Guid ClientId, string RepositoryId), string?> repositoryNames,
    IReadOnlyList<ReviewerPerformanceDailyCount> cells)
{
    private readonly Dictionary<(Guid ClientId, string RepositoryId), int> _repositoryScopeCounts = cells
        .GroupBy(row => (row.ClientId, row.RepositoryId))
        .ToDictionary(group => group.Key, group => group.Select(row => row.ProviderScope).Distinct(StringComparer.Ordinal).Count());

    internal ReviewerPerformanceFacet Label(ReviewerPerformanceFacet key, string dimension)
    {
        if (dimension == "client" && Guid.TryParse(key.Id, out var client))
        {
            return key with { Label = clientNames.GetValueOrDefault(client, key.Label) };
        }

        if (dimension == "type")
        {
            return key with
            {
                Label = Taxonomy.CodeInsightCoreTaxonomy.Find(key.Id)?.DisplayName ?? (key.Id == "unclassified" ? "Unclassified" : key.Label)
            };
        }

        if (dimension == "repository")
        {
            var parts = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(Convert.FromBase64String(key.Id)))!;
            var clientId = Guid.Parse(parts[0]);
            var repositoryKey = (clientId, parts[2]);
            var name = this._repositoryScopeCounts.GetValueOrDefault(repositoryKey) == 1
                ? repositoryNames.GetValueOrDefault(repositoryKey)
                : null;
            var clientName = clientNames.GetValueOrDefault(clientId, parts[0]);
            return key with { Label = $"{name ?? parts[2]} · {clientName} · {(parts[1].Length == 0 ? "source scope unavailable" : parts[1])}" };
        }

        return key;
    }


    internal ReviewerPerformanceFacets Apply(ReviewerPerformanceFacets facets)
    {
        return facets with
        {
            Clients = facets.Clients.Select(item => this.Label(item, "client")).ToArray(),
            Repositories = facets.Repositories.Select(item => this.Label(item, "repository")).ToArray(),
            Types = facets.Types.Select(item => this.Label(item, "type")).ToArray()
        };
    }
}
