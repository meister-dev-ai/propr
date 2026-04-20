// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.TeamFoundation.Wiki.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps.ProCursor;

/// <summary>
///     Resolves the backing git repository identifier for Azure DevOps wiki knowledge sources.
/// </summary>
internal static class AdoWikiRepositoryResolver
{
    public static string ResolveRepositoryId(ProCursorKnowledgeSource source, IReadOnlyList<WikiV2> wikis)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(wikis);

        if (source.SourceKind != ProCursorSourceKind.AdoWiki)
        {
            throw new InvalidOperationException($"Source {source.Id} is not an Azure DevOps wiki source.");
        }

        var wiki = ResolveWiki(source, wikis);

        if (wiki is null)
        {
            throw new InvalidOperationException($"Unable to resolve the backing repository for wiki source {source.Id}.");
        }

        return wiki.RepositoryId.ToString();
    }

    public static string GetCanonicalWikiId(ProCursorKnowledgeSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.SourceKind != ProCursorSourceKind.AdoWiki)
        {
            throw new InvalidOperationException($"Source {source.Id} is not an Azure DevOps wiki source.");
        }

        var wikiId = !string.IsNullOrWhiteSpace(source.CanonicalSourceValue)
            ? source.CanonicalSourceValue
            : source.RepositoryId;

        if (string.IsNullOrWhiteSpace(wikiId))
        {
            throw new InvalidOperationException($"Wiki source {source.Id} is missing its backing wiki identifier.");
        }

        return wikiId.Trim();
    }

    private static WikiV2? ResolveWiki(ProCursorKnowledgeSource source, IReadOnlyList<WikiV2> wikis)
    {
        foreach (var identifier in GetCandidateIdentifiers(source))
        {
            var wiki = wikis.FirstOrDefault(candidate => MatchesWiki(candidate, identifier));
            if (wiki is not null)
            {
                return wiki;
            }
        }

        if (IsLegacyManualSource(source))
        {
            var resolvableWikis = wikis
                .Where(candidate => candidate.RepositoryId != Guid.Empty)
                .Take(2)
                .ToList();

            if (resolvableWikis.Count == 1)
            {
                return resolvableWikis[0];
            }
        }

        return null;
    }

    private static IReadOnlyList<string> GetCandidateIdentifiers(ProCursorKnowledgeSource source)
    {
        var identifiers = new List<string>(4);

        AddIdentifier(identifiers, GetCanonicalWikiId(source));
        AddIdentifier(identifiers, source.RepositoryId);
        AddIdentifier(identifiers, source.SourceDisplayName);
        AddIdentifier(identifiers, source.DisplayName);

        return identifiers;
    }

    private static void AddIdentifier(ICollection<string> identifiers, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        if (identifiers.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        identifiers.Add(trimmed);
    }

    private static bool MatchesWiki(WikiV2 wiki, string identifier)
    {
        if (wiki.RepositoryId == Guid.Empty)
        {
            return false;
        }

        return string.Equals(wiki.Id.ToString(), identifier, StringComparison.OrdinalIgnoreCase)
               || string.Equals(wiki.RepositoryId.ToString(), identifier, StringComparison.OrdinalIgnoreCase)
               || string.Equals(wiki.Name, identifier, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacyManualSource(ProCursorKnowledgeSource source)
    {
        return string.IsNullOrWhiteSpace(source.CanonicalSourceValue)
               && string.IsNullOrWhiteSpace(source.SourceDisplayName);
    }
}
