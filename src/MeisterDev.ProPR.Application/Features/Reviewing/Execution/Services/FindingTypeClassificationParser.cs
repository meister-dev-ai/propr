// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>Reads optional finding classifications against the vocabulary captured for the review.</summary>
public static class FindingTypeClassificationParser
{
    /// <summary>Reads extension properties without rejecting a finding with missing or malformed metadata.</summary>
    public static FindingTypeClassification? ParseProperties(IReadOnlyDictionary<string, JsonElement>? properties, FindingTypeVocabulary? vocabulary = null)
    {
        if (properties is null)
        {
            return null;
        }

        foreach (var property in properties)
        {
            if (string.Equals(property.Key.Replace("_", string.Empty), "FindingClassification", StringComparison.OrdinalIgnoreCase))
            {
                return Parse(property.Value, vocabulary);
            }
        }

        return null;
    }

    /// <summary>Reads the classification on one finding.</summary>
    public static FindingTypeClassification? ParseFinding(JsonElement finding, FindingTypeVocabulary? vocabulary = null)
    {
        if (finding.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in finding.EnumerateObject())
        {
            if (string.Equals(property.Name.Replace("_", string.Empty), "FindingClassification", StringComparison.OrdinalIgnoreCase))
            {
                return Parse(property.Value, vocabulary);
            }
        }

        return null;
    }

    private static FindingTypeClassification? Parse(JsonElement value, FindingTypeVocabulary? vocabulary)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("types", out var types) || types.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        vocabulary ??= FindingTypeVocabulary.Core;
        var core = new List<string>();
        var custom = new List<FindingCustomTypeAssignment>();
        foreach (var item in types.EnumerateArray().Take(64))
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var slug = item.GetString()?.Trim();
            if (FindingTypeTaxonomy.Find(slug) is { } definition)
            {
                if (!core.Contains(definition.Slug, StringComparer.Ordinal))
                {
                    core.Add(definition.Slug);
                }
            }
            else if (vocabulary.CustomTypes.FirstOrDefault(tag => string.Equals(tag.Slug, slug, StringComparison.OrdinalIgnoreCase)) is { } tag
                     && custom.All(assignment => assignment.Id != tag.Id))
            {
                custom.Add(new FindingCustomTypeAssignment(tag.Id, tag.Slug, tag.DisplayName));
            }
        }

        if (core.Count == 0)
        {
            return null;
        }

        var level = ReadString(value, "level")?.Replace("_", string.Empty).Replace("-", string.Empty);
        var qualifier = ReadString(value, "qualifier");
        CodeInsightFindingLevel? parsedLevel = Enum.TryParse<CodeInsightFindingLevel>(level, true, out var l) && Enum.IsDefined(l) ? l : null;
        CodeInsightFindingQualifier? parsedQualifier = Enum.TryParse<CodeInsightFindingQualifier>(qualifier, true, out var q) && Enum.IsDefined(q) ? q : null;
        double? confidence = value.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                                                                           && c.TryGetDouble(out var number) && double.IsFinite(number) &&
                                                                           number is >= 0 and <= 1
            ? number
            : null;
        return new FindingTypeClassification(
            core.AsReadOnly(), custom.AsReadOnly(), parsedLevel, parsedQualifier, confidence,
            FindingTypeTaxonomy.Version, FindingTypeClassification.ReviewSource);
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString()?.Trim() : null;
}
