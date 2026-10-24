// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>Resolves client vocabulary once, retaining the core vocabulary when custom types are unavailable.</summary>
public static class FindingTypeVocabularyLoader
{
    /// <summary>Reads a bounded snapshot without making vocabulary availability a review dependency.</summary>
    public static async Task<FindingTypeVocabulary> LoadAsync(IFindingTypeVocabularyProvider? provider, Guid clientId, ILogger logger, CancellationToken ct)
    {
        if (provider is null)
        {
            return FindingTypeVocabulary.Core;
        }

        try
        {
            var vocabulary = await provider.GetVocabularyAsync(clientId, ct);
            if (vocabulary is null || vocabulary.Version != FindingTypeTaxonomy.Version || vocabulary.CustomTypes is null)
            {
                return FindingTypeVocabulary.Core;
            }

            var custom = vocabulary.CustomTypes.Where(tag => tag is not null && tag.Id != Guid.Empty
                                                                             && !string.IsNullOrWhiteSpace(tag.Slug) && tag.Slug.Length <= 64 &&
                                                                             !FindingTypeTaxonomy.IsCoreSlug(tag.Slug)
                                                                             && !string.IsNullOrWhiteSpace(tag.DisplayName) && tag.DisplayName.Length <= 128
                                                                             && !string.IsNullOrWhiteSpace(tag.Definition) && tag.Definition.Length <= 512)
                .DistinctBy(tag => tag.Slug, StringComparer.OrdinalIgnoreCase).Take(64).ToList().AsReadOnly();
            return custom.Count == 0 ? FindingTypeVocabulary.Core : new FindingTypeVocabulary(FindingTypeTaxonomy.Version, custom);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Finding type custom vocabulary unavailable; core types remain available. {ExceptionType}", ex.GetType().Name);
            return FindingTypeVocabulary.Core;
        }
    }
}
