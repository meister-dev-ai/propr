// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>Evidence-based impact, occurrence likelihood and correctness confidence with versioned risk derivation.</summary>
[JsonConverter(typeof(FindingRiskAssessmentJsonConverter))]
public sealed record FindingRiskAssessment
{
    /// <summary>Rubric used for newly generated assessments.</summary>
    public const string CurrentRubricVersion = "finding-risk-v1";

    /// <summary>Maximum retained length of each rationale.</summary>
    public const int MaxReasonLength = 1024;

    /// <summary>Creates an immutable assessment. Unsupported or incomplete evidence remains unscored.</summary>
    public FindingRiskAssessment(
        FindingImpact impact,
        FindingLikelihood likelihood,
        string? impactReason,
        string? likelihoodReason,
        string? source,
        string? rubricVersion,
        FindingConfidence confidence = FindingConfidence.Unknown,
        string? confidenceReason = null,
        string? confidenceSource = null)
    {
        this.Impact = Enum.IsDefined(impact) ? impact : FindingImpact.Unknown;
        this.Likelihood = Enum.IsDefined(likelihood) ? likelihood : FindingLikelihood.Unknown;
        this.ImpactReason = BoundedText(impactReason, MaxReasonLength);
        this.LikelihoodReason = BoundedText(likelihoodReason, MaxReasonLength);
        this.Source = BoundedText(source, 128);
        this.RubricVersion = BoundedText(rubricVersion, 128);
        this.ConfidenceReason = BoundedText(confidenceReason, MaxReasonLength);
        this.ConfidenceSource = confidenceSource is null ? this.Source : BoundedText(confidenceSource, 128);
        this.Confidence = Enum.IsDefined(confidence) && this.ConfidenceReason is not null && this.ConfidenceSource is not null &&
                          this.RubricVersion == CurrentRubricVersion
            ? confidence
            : FindingConfidence.Unknown;
    }

    /// <summary>Consequence when the assessed occurrence scenario happens.</summary>
    public FindingImpact Impact { get; }

    /// <summary>Occurrence likelihood independent of confidence that the finding is correct.</summary>
    public FindingLikelihood Likelihood { get; }

    /// <summary>Strength of correctness evidence, independent of impact and occurrence likelihood.</summary>
    public FindingConfidence Confidence { get; }

    /// <summary>Evidence and unresolved assumptions supporting correctness confidence.</summary>
    public string? ConfidenceReason { get; }

    /// <summary>Producer of the confidence. It equals Source unless a separate step, such as verification, set the confidence.</summary>
    public string? ConfidenceSource { get; }

    /// <summary>Evidence explaining the assessed consequence.</summary>
    public string? ImpactReason { get; }

    /// <summary>Evidence explaining the occurrence scenario and its likelihood.</summary>
    public string? LikelihoodReason { get; }

    /// <summary>Assessment producer recorded by the server.</summary>
    public string? Source { get; }

    /// <summary>Version of the risk rubric.</summary>
    public string? RubricVersion { get; }

    /// <summary>Derived risk severity; incomplete, unsupported, and inapplicable assessments are unknown.</summary>
    public FindingRiskSeverity Severity =>
        this.RubricVersion != CurrentRubricVersion || this.ImpactReason is null || this.LikelihoodReason is null || this.Source is null
            ? FindingRiskSeverity.Unknown
            : (this.Impact, this.Likelihood) switch
            {
                (FindingImpact.Low, FindingLikelihood.Unlikely or FindingLikelihood.Possible or FindingLikelihood.Likely) => FindingRiskSeverity.Low,
                (FindingImpact.Moderate, FindingLikelihood.Unlikely) => FindingRiskSeverity.Low,
                (FindingImpact.Moderate, FindingLikelihood.Possible or FindingLikelihood.Likely) => FindingRiskSeverity.Medium,
                (FindingImpact.High, FindingLikelihood.Unlikely) => FindingRiskSeverity.Medium,
                (FindingImpact.High, FindingLikelihood.Possible or FindingLikelihood.Likely) => FindingRiskSeverity.High,
                (FindingImpact.Critical, FindingLikelihood.Unlikely or FindingLikelihood.Possible) => FindingRiskSeverity.High,
                (FindingImpact.Critical, FindingLikelihood.Likely) => FindingRiskSeverity.Critical,
                _ => FindingRiskSeverity.Unknown,
            };

    private static string? BoundedText(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum ? null : value.Trim();
}

internal sealed class FindingRiskAssessmentJsonConverter : JsonConverter<FindingRiskAssessment>
{
    public override FindingRiskAssessment? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new FindingRiskAssessment(
            ReadEnum<FindingImpact>(ReadProperty(value, "Impact")),
            ReadEnum<FindingLikelihood>(ReadProperty(value, "Likelihood")),
            ReadText(ReadProperty(value, "ImpactReason")),
            ReadText(ReadProperty(value, "LikelihoodReason")),
            ReadText(ReadProperty(value, "Source")),
            ReadText(ReadProperty(value, "RubricVersion")),
            ReadEnum<FindingConfidence>(ReadProperty(value, "Confidence")),
            ReadText(ReadProperty(value, "ConfidenceReason")),
            ReadConfidenceSource(ReadProperty(value, "ConfidenceSource")));
    }

    public override void Write(Utf8JsonWriter writer, FindingRiskAssessment value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteProperty(writer, "Impact", value.Impact, options);
        WriteProperty(writer, "Likelihood", value.Likelihood, options);
        WriteProperty(writer, "Confidence", value.Confidence, options);
        WriteProperty(writer, "ConfidenceReason", value.ConfidenceReason, options);
        WriteProperty(writer, "ConfidenceSource", value.ConfidenceSource, options);
        WriteProperty(writer, "ImpactReason", value.ImpactReason, options);
        WriteProperty(writer, "LikelihoodReason", value.LikelihoodReason, options);
        WriteProperty(writer, "Source", value.Source, options);
        WriteProperty(writer, "RubricVersion", value.RubricVersion, options);
        WriteProperty(writer, "Severity", value.Severity, options);
        writer.WriteEndObject();
    }

    private static void WriteProperty<T>(Utf8JsonWriter writer, string name, T value, JsonSerializerOptions options)
    {
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(name) ?? name);
        JsonSerializer.Serialize(writer, value, options);
    }

    private static JsonElement ReadProperty(JsonElement value, string name)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name.Replace("_", string.Empty), name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return default;
    }

    private static string? ReadText(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ReadConfidenceSource(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        _ => string.Empty,
    };

    private static T ReadEnum<T>(JsonElement value) where T : struct, Enum
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            var parsed = (T)Enum.ToObject(typeof(T), number);
            return Enum.IsDefined(parsed) ? parsed : default;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Replace("_", string.Empty);
            foreach (var candidate in Enum.GetValues<T>())
            {
                if (string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return default;
    }
}
