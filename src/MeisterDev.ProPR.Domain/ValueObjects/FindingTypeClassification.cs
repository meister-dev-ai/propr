// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>An assignable custom type captured for one review.</summary>
public sealed record FindingCustomTypeDefinition(Guid Id, string Slug, string DisplayName, string Definition);

/// <summary>A stable custom assignment and the label displayed by the producing review.</summary>
public sealed record FindingCustomTypeAssignment(Guid Id, string Slug, string DisplayName);

/// <summary>The vocabulary captured before any finding-producing pass starts.</summary>
public sealed record FindingTypeVocabulary(int Version, IReadOnlyList<FindingCustomTypeDefinition> CustomTypes)
{
    /// <summary>Canonical vocabulary when no client custom types are available.</summary>
    public static FindingTypeVocabulary Core { get; } = new(FindingTypeTaxonomy.Version, []);
}

/// <summary>Finding types and the independent analytics axes assigned by its producing review.</summary>
public sealed record FindingTypeClassification(
    IReadOnlyList<string> CoreSlugs,
    IReadOnlyList<FindingCustomTypeAssignment> CustomTags,
    CodeInsightFindingLevel? Level,
    CodeInsightFindingQualifier? Qualifier,
    double? Confidence,
    int TaxonomyVersion,
    string Source)
{
    /// <summary>Server-owned version identifying classification produced during review.</summary>
    public const string ReviewSource = "review-finding-type-v1";

    /// <summary>Whether supported core types were assigned during review, independently of optional analytics axes.</summary>
    [JsonIgnore]
    public bool HasSupportedTypes => this.Source == ReviewSource && this.TaxonomyVersion == FindingTypeTaxonomy.Version
                                                                 && this.CoreSlugs is not null && this.CoreSlugs.Any(FindingTypeTaxonomy.IsCoreSlug);

    /// <summary>Whether this also carries complete, valid ancillary analytics axes.</summary>
    [JsonIgnore]
    public bool IsComplete => this.Source == ReviewSource && this.TaxonomyVersion == FindingTypeTaxonomy.Version
                                                          && this.CoreSlugs is { Count: > 0 } && this.CoreSlugs.All(FindingTypeTaxonomy.IsCoreSlug)
                                                          && this.CustomTags is not null && this.CustomTags.All(tag => tag is not null && tag.Id != Guid.Empty
                                                              && !string.IsNullOrWhiteSpace(tag.Slug) && !string.IsNullOrWhiteSpace(tag.DisplayName)) &&
                                                          this.Level is { } level && Enum.IsDefined(level)
                                                          && this.Qualifier is { } qualifier && Enum.IsDefined(qualifier)
                                                          && this.Confidence is >= 0 and <= 1;

    /// <summary>Combines supported source tags while retaining conservative classification confidence.</summary>
    public static FindingTypeClassification? Merge(IEnumerable<FindingTypeClassification?> classifications)
    {
        var sources = classifications.Where(value => value is not null).Cast<FindingTypeClassification>().ToList();
        if (sources.Count == 0)
        {
            return null;
        }

        var anchor = sources[0];
        return anchor with
        {
            CoreSlugs = sources.SelectMany(value => value.CoreSlugs ?? []).Select(FindingTypeTaxonomy.Find)
                .Where(type => type is not null).Select(type => type!.Slug).Distinct(StringComparer.Ordinal).ToList().AsReadOnly(),
            CustomTags = sources.SelectMany(value => value.CustomTags ?? [])
                .Where(tag => tag is not null && tag.Id != Guid.Empty && !string.IsNullOrWhiteSpace(tag.Slug) && !string.IsNullOrWhiteSpace(tag.DisplayName))
                .DistinctBy(tag => tag.Id).Take(64).ToList().AsReadOnly(),
            Level = sources.Where(value => value.Level is { } level && Enum.IsDefined(level)).Select(value => value.Level).Max(),
            Qualifier = sources.All(value => value.Qualifier == anchor.Qualifier) && anchor.Qualifier is { } qualifier && Enum.IsDefined(qualifier)
                ? qualifier
                : null,
            Confidence = sources.Any(value => value.Confidence is not (>= 0 and <= 1))
                ? null
                : sources.Min(value => value.Confidence),
        };
    }
}
