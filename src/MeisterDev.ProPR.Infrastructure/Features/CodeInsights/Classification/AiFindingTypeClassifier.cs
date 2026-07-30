// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Globalization;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Application.Features.CodeInsights.Taxonomy;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Classification;

/// <summary>
///     Model-backed finding-type classifier. Resolves a configured model via
///     <see cref="AiPurpose.InsightsClassification" /> and asks it to place one finding in the client's
///     vocabulary, judge the level of code it concerns, and say whether the code in question is absent,
///     wrong, or unnecessary.
/// </summary>
/// <remarks>
///     <para>
///         It classifies from the finding itself (its text, its anchor, its severity, and which pass produced
///         it) rather than from the diff. The finding text is a description of the problem written for a
///         human, which is exactly what the established defect taxonomies classify from, and the alternative
///         would mean storing a diff excerpt per finding: a second copy of data the review archive may already
///         hold, on a path that runs long after the diff is out of scope. The cost is that the level axis is
///         judged from the anchor and the producing pass rather than from the surrounding code.
///     </para>
///     <para>
///         Never throws except for cancellation. The result distinguishes a model that was asked and produced
///         nothing usable from a purpose with no model bound at all, because only the first should cost the
///         finding one of its attempts.
///     </para>
/// </remarks>
internal sealed partial class AiFindingTypeClassifier(
    IAiRuntimeResolver aiRuntimeResolver,
    ILogger<AiFindingTypeClassifier> logger) : IFindingTypeClassifier
{
    /// <summary>
    ///     Bounds the finding text handed to the model. A finding message is prose and normally far shorter
    ///     than this; the ceiling exists so one pathological finding cannot cost many times what the others do.
    /// </summary>
    private const int MaxMessageChars = 4000;

    private const int MaxTagsInPrompt = 64;

    /// <inheritdoc />
    public string ClassifierVersion => "finding-type-v1";

    public async Task<FindingClassificationResult> ClassifyAsync(
        FindingClassificationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IResolvedAiChatRuntime runtime;
        try
        {
            runtime = await aiRuntimeResolver
                .ResolveChatRuntimeAsync(request.ClientId, AiPurpose.InsightsClassification, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AiPurposeBindingNotConfiguredException ex)
        {
            // A configuration choice rather than a fault: this client has no model bound for the purpose and no
            // fallback reached one either. Reported distinctly, and (because nothing was asked) it must not cost
            // the finding an attempt, or fixing the configuration afterwards would come too late for it.
            LogBindingUnavailable(logger, request.FindingId, ex);
            return FindingClassificationResult.NoModelBound();
        }
        catch (Exception ex)
        {
            LogResolutionFailed(logger, request.FindingId, ex);
            return FindingClassificationResult.Unusable();
        }

        try
        {
            var response = await runtime.ChatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, BuildSystemPrompt(request.Vocabulary)),
                    new ChatMessage(ChatRole.User, BuildUserMessage(request)),
                ],
                new ChatOptions(),
                ct).ConfigureAwait(false);

            var verdict = TryParse(response.Text, request.Vocabulary);
            if (verdict is null)
            {
                LogUnusableResponse(logger, request.FindingId);
                return FindingClassificationResult.Unusable();
            }

            return FindingClassificationResult.Classified(verdict);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCallFailed(logger, request.FindingId, ex);
            return FindingClassificationResult.Unusable();
        }
    }

    private static string BuildSystemPrompt(CodeInsightTaxonomyDto vocabulary)
    {
        var prompt = new StringBuilder();
        prompt.Append(
            "You classify a single code-review finding for quality analytics. You are told what the finding "
            + "says; you do not need the surrounding code. Choose from the vocabulary below and nothing else.\n\n"
            + "TYPES (choose one or more that genuinely apply; most findings have exactly one, and a finding "
            + "that is, say, both a missing check and a security hole should carry both):\n");

        // The definition an operator reads is the definition the model is given. One string, one meaning.
        foreach (var tag in vocabulary.CoreTags.Take(MaxTagsInPrompt))
        {
            prompt.Append("- ").Append(tag.Slug).Append(": ").Append(tag.Definition).Append('\n');
        }

        if (vocabulary.CustomTags.Count > 0)
        {
            prompt.Append(
                "\nADDITIONAL TYPES defined by this team (apply only where they clearly fit; they are extra, "
                + "not replacements, always also give at least one type from the list above):\n");
            foreach (var tag in vocabulary.CustomTags.Take(MaxTagsInPrompt))
            {
                prompt.Append("- ").Append(tag.Slug).Append(": ").Append(tag.Definition).Append('\n');
            }
        }

        prompt.Append(
            "\nLEVEL: how much code the problem spans, not where it sits in the diff. Exactly one of:\n"
            + "- statement: contained in the line or two cited\n"
            + "- member: one method, property, or function body\n"
            + "- type: a class and its members, meaning responsibility placement, invariants, "
            + "state across methods\n"
            + "- file: a whole file or module beyond a single type\n"
            + "- crossFile: several files, a component, or the architecture\n"
            + "\nQUALIFIER, exactly one of:\n"
            + "- missing: something required is absent\n"
            + "- incorrect: something is present but does the wrong thing\n"
            + "- extraneous: something is present that should not be\n"
            + "\nRespond with ONLY a JSON object and nothing else:\n"
            + "{\"types\":[\"slug\",…],\"level\":\"statement|member|type|file|crossFile\","
            + "\"qualifier\":\"missing|incorrect|extraneous\",\"confidence\":0.0}\n"
            + "confidence is your own 0–1 estimate that the types are right. Never invent a type that is not "
            + "listed above.");

        return prompt.ToString();
    }

    private static string BuildUserMessage(FindingClassificationRequest request)
    {
        var message = request.Message.Length > MaxMessageChars
            ? string.Concat(request.Message.AsSpan(0, MaxMessageChars), "\n…(truncated)")
            : request.Message;

        var anchor = request.FilePath is null
            ? "the pull request as a whole (no single file)"
            : request.LineNumber is null
                ? request.FilePath
                : $"{request.FilePath}:{request.LineNumber.Value.ToString(CultureInfo.InvariantCulture)}";

        // Which pass produced the finding is the strongest available signal for the level axis: a
        // pull-request-wide pass looks across files by construction, so its findings are rarely statement-level.
        var origin = string.IsNullOrWhiteSpace(request.OriginPassKind)
            ? "unknown"
            : request.OriginPassKind;

        return $"Anchor: {anchor}\nSeverity as assigned by the reviewer: {request.Severity}\n"
               + $"Produced by review pass: {origin}\n\nFinding:\n{message}";
    }

    private static FindingTypeVerdict? TryParse(string? responseText, CodeInsightTaxonomyDto vocabulary)
    {
        var json = ExtractJsonObject(responseText);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var (coreSlugs, customTagIds) = ResolveTypes(root, vocabulary);

            // At least one core type is the contract. Without it there is nothing comparable across clients to
            // record, and a custom tag alone would leave the finding invisible to every cross-client view.
            if (coreSlugs.Count == 0)
            {
                return null;
            }

            return new FindingTypeVerdict(
                coreSlugs,
                customTagIds,
                ReadLevel(root),
                ReadQualifier(root),
                ReadConfidence(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (List<string> CoreSlugs, List<Guid> CustomTagIds) ResolveTypes(
        JsonElement root,
        CodeInsightTaxonomyDto vocabulary)
    {
        var coreSlugs = new List<string>();
        var customTagIds = new List<Guid>();

        if (!root.TryGetProperty("types", out var typesElement) || typesElement.ValueKind != JsonValueKind.Array)
        {
            return (coreSlugs, customTagIds);
        }

        var customBySlug = vocabulary.CustomTags
            .GroupBy(tag => tag.Slug, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);

        foreach (var element in typesElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var slug = element.GetString()?.Trim();
            if (string.IsNullOrEmpty(slug))
            {
                continue;
            }

            // Resolve against the vocabulary that was supplied. Anything else is dropped: a label nothing
            // defines cannot be aggregated, compared, or explained to whoever reads the chart.
            var core = vocabulary.CoreTags.FirstOrDefault(tag => string.Equals(tag.Slug, slug, StringComparison.OrdinalIgnoreCase));
            if (core is not null)
            {
                if (!coreSlugs.Contains(core.Slug, StringComparer.OrdinalIgnoreCase))
                {
                    coreSlugs.Add(core.Slug);
                }

                continue;
            }

            if (customBySlug.TryGetValue(slug, out var customTagId) && !customTagIds.Contains(customTagId))
            {
                customTagIds.Add(customTagId);
            }
        }

        return (coreSlugs, customTagIds);
    }

    private static CodeInsightFindingLevel ReadLevel(JsonElement root)
    {
        var raw = root.TryGetProperty("level", out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim()
            : null;

        return raw?.ToLowerInvariant() switch
        {
            "statement" => CodeInsightFindingLevel.Statement,
            "member" => CodeInsightFindingLevel.Member,
            "type" => CodeInsightFindingLevel.Type,
            "file" => CodeInsightFindingLevel.File,
            "crossfile" => CodeInsightFindingLevel.CrossFile,
            // An unreadable level defaults to the narrowest claim rather than the widest: over-stating blast
            // radius would inflate exactly the number an operator would act on.
            _ => CodeInsightFindingLevel.Statement,
        };
    }

    private static CodeInsightFindingQualifier ReadQualifier(JsonElement root)
    {
        var raw = root.TryGetProperty("qualifier", out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim()
            : null;

        return raw?.ToLowerInvariant() switch
        {
            "missing" => CodeInsightFindingQualifier.Missing,
            "extraneous" => CodeInsightFindingQualifier.Extraneous,
            _ => CodeInsightFindingQualifier.Incorrect,
        };
    }

    private static double ReadConfidence(JsonElement root)
    {
        if (root.TryGetProperty("confidence", out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out var confidence))
        {
            return Math.Clamp(confidence, 0d, 1d);
        }

        return 0d;
    }

    private static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "No insights-classification model is bound for finding {FindingId}; it stays unclassified.")]
    private static partial void LogBindingUnavailable(ILogger logger, Guid findingId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Resolving the insights-classification runtime failed for finding {FindingId}. "
                  + "This is a fault, not a missing binding.")]
    private static partial void LogResolutionFailed(ILogger logger, Guid findingId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The insights-classification call failed for finding {FindingId}; it will be retried.")]
    private static partial void LogCallFailed(ILogger logger, Guid findingId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The insights classifier returned nothing usable for finding {FindingId} "
                  + "(no in-vocabulary type); it will be retried.")]
    private static partial void LogUnusableResponse(ILogger logger, Guid findingId);
}
