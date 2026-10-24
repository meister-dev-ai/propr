// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>Retains supported source types when a later stage repeats or combines existing claims.</summary>
public static class FindingTypeClassificationPreserver
{
    /// <summary>Uses an unchanged claim's types or combines the types of explicitly identified source claims.</summary>
    public static FindingTypeClassification? Preserve(
        FindingTypeClassification? proposed,
        FindingTypeClassification? unchangedSource,
        IEnumerable<FindingTypeClassification?> declaredSources)
    {
        var supportedSources = declaredSources.Where(source => source is { HasSupportedTypes: true }).ToList();
        if (unchangedSource is { HasSupportedTypes: true })
        {
            return supportedSources.Count == 0 || supportedSources.All(source => ReferenceEquals(source, unchangedSource))
                ? unchangedSource
                : FindingTypeClassification.Merge(new[] { unchangedSource }.Concat(supportedSources));
        }

        if (supportedSources.Count == 0)
        {
            return proposed;
        }

        if (proposed is { HasSupportedTypes: true })
        {
            supportedSources.Add(proposed);
        }

        return FindingTypeClassification.Merge(supportedSources);
    }
}
