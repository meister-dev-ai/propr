// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>Reads optional model assessment evidence and assigns server-owned provenance.</summary>
public static class FindingRiskAssessmentParser
{
    /// <summary>Reads assessment from unrecognized fields retained by a response DTO.</summary>
    public static FindingRiskAssessment? ParseProperties(IReadOnlyDictionary<string, JsonElement>? properties)
    {
        if (properties is null)
        {
            return null;
        }

        foreach (var property in properties)
        {
            if (string.Equals(property.Key.Replace("_", string.Empty), "RiskAssessment", StringComparison.OrdinalIgnoreCase))
            {
                return Parse(property.Value);
            }
        }

        return null;
    }

    /// <summary>Reads the assessment on a finding without invalidating findings with malformed optional metadata.</summary>
    public static FindingRiskAssessment? ParseFinding(JsonElement finding)
    {
        if (finding.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in finding.EnumerateObject())
        {
            if (string.Equals(property.Name.Replace("_", string.Empty), "RiskAssessment", StringComparison.OrdinalIgnoreCase))
            {
                return Parse(property.Value);
            }
        }

        return null;
    }

    /// <summary>Reads assessment axes and rationales. Model severity, source and version are ignored.</summary>
    public static FindingRiskAssessment? Parse(JsonElement assessment)
    {
        if (assessment.ValueKind != JsonValueKind.Object)
        {
            return new FindingRiskAssessment(
                FindingImpact.Unknown, FindingLikelihood.Unknown, null, null, "review_model", FindingRiskAssessment.CurrentRubricVersion);
        }

        var fields = new Dictionary<string, JsonElement>();
        foreach (var field in assessment.EnumerateObject())
        {
            var name = field.Name.Replace("_", string.Empty);
            if (!string.Equals(name, "Source", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "RubricVersion", StringComparison.OrdinalIgnoreCase))
            {
                fields[field.Name] = field.Value;
            }
        }

        fields["Source"] = JsonSerializer.SerializeToElement("review_model");
        fields["RubricVersion"] = JsonSerializer.SerializeToElement(FindingRiskAssessment.CurrentRubricVersion);
        var evidence = JsonSerializer.Deserialize<FindingRiskAssessment>(JsonSerializer.Serialize(fields));
        return evidence is null
            ? null
            : new FindingRiskAssessment(
                evidence.Impact,
                evidence.Likelihood,
                evidence.ImpactReason,
                evidence.LikelihoodReason,
                "review_model",
                FindingRiskAssessment.CurrentRubricVersion,
                evidence.Confidence,
                evidence.ConfidenceReason);
    }

    /// <summary>Preserves recorded evidence when a model changes only an existing finding's classification.</summary>
    public static ReviewComment PreserveUnchangedEvidence(ReviewComment parsed, IReadOnlyList<ReviewComment> originals, JsonElement? rawFinding = null)
    {
        var matches = originals.Where(original =>
            original.FilePath == parsed.FilePath && original.LineNumber == parsed.LineNumber && original.Message == parsed.Message).Take(2).ToList();
        if (matches.Count != 1)
        {
            return parsed;
        }

        var original = matches[0];
        var risk = parsed.RiskAssessment ?? original.RiskAssessment;
        if (parsed.RiskAssessment is { } assessed && original.RiskAssessment is { } recorded
                                                  && (recorded.ConfidenceSource == ReviewFindingConfidence.VerificationSource ||
                                                      rawFinding is { } raw && ConfidenceOmitted(raw)))
        {
            risk = new FindingRiskAssessment(
                assessed.Impact, assessed.Likelihood, assessed.ImpactReason, assessed.LikelihoodReason,
                assessed.Source, assessed.RubricVersion, recorded.Confidence, recorded.ConfidenceReason, recorded.ConfidenceSource);
        }

        var classification = parsed.Severity == MeisterDev.ProPR.Domain.Enums.CommentSeverity.Suggestion
                             && original.Severity is MeisterDev.ProPR.Domain.Enums.CommentSeverity.Error
                                 or MeisterDev.ProPR.Domain.Enums.CommentSeverity.Warning
            ? original.Severity
            : parsed.Severity;
        return original.WithAnchorAndSeverity(parsed.FilePath, parsed.LineNumber, classification) with
        {
            RiskAssessment = risk, FindingClassification = original.FindingClassification ?? parsed.FindingClassification
        };
    }

    private static bool ConfidenceOmitted(JsonElement finding)
    {
        foreach (var property in finding.EnumerateObject())
        {
            if (!string.Equals(property.Name.Replace("_", string.Empty), "RiskAssessment", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind == JsonValueKind.Object && !property.Value.EnumerateObject().Any(field =>
                string.Equals(field.Name.Replace("_", string.Empty), "Confidence", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(field.Name.Replace("_", string.Empty), "ConfidenceReason", StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }
}
