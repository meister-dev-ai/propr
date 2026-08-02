// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Globalization;
using System.Text.Json;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Taxonomy;
using MeisterDev.ProPR.CodeInsights.Classification.Prompts;

namespace MeisterDev.ProPR.CodeInsights.Classification;

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
    IModelUsageRecorder usageRecorder,
    ILogger<AiFindingTypeClassifier> logger) : IFindingTypeClassifier, IDisposable
{
    private readonly SemaphoreSlim _resolutionGate = new(1, 1);
    private IResolvedAiChatRuntime? _resolvedRuntime;
    private Guid _resolvedClientId;

    public void Dispose()
    {
        this._resolutionGate.Dispose();
    }

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
            runtime = await this.ResolveRuntimeAsync(request.ClientId, ct).ConfigureAwait(false);
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

            // Before the response is judged usable or not: the tokens were spent either way, and a classifier
            // that only counted the calls it could parse would understate what the client was billed.
            await usageRecorder.RecordAsync(request.ClientId, runtime, response, ct).ConfigureAwait(false);

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

    /// <summary>
    ///     Resolves the classification runtime once and shares it across the findings being classified.
    /// </summary>
    /// <remarks>
    ///     Findings are classified concurrently, and resolution reads the logical-model catalog, the connection,
    ///     and the client through repositories that share one scoped <c>DbContext</c>. Letting those reads overlap
    ///     threw "a second operation was started on this context instance", which this class caught and reported
    ///     as a fault, so findings went unclassified for a reason that had nothing to do with the model. The gate
    ///     removes the overlap and the cache means a run pays for resolution once rather than once per finding.
    ///     A failure is not cached: it is reported per finding, and a transient fault must not disable
    ///     classification for the rest of the run.
    /// </remarks>
    private async Task<IResolvedAiChatRuntime> ResolveRuntimeAsync(Guid clientId, CancellationToken ct)
    {
        await this._resolutionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (this._resolvedRuntime is not null && this._resolvedClientId == clientId)
            {
                return this._resolvedRuntime;
            }

            var runtime = await aiRuntimeResolver
                .ResolveChatRuntimeAsync(clientId, AiPurpose.InsightsClassification, ct)
                .ConfigureAwait(false);
            this._resolvedRuntime = runtime;
            this._resolvedClientId = clientId;
            return runtime;
        }
        finally
        {
            this._resolutionGate.Release();
        }
    }

    private static string BuildSystemPrompt(CodeInsightTaxonomyDto vocabulary)
    {
        // The definition an operator reads is the definition the model is given. One string, one meaning.
        return InsightPrompts.FindingTypeSystem(
            new InsightPromptModels.FindingTypeSystemModel(
                ToTagModels(vocabulary.CoreTags.Take(MaxTagsInPrompt).Select(tag => (tag.Slug, tag.Definition))),
                vocabulary.CustomTags.Count > 0,
                ToTagModels(vocabulary.CustomTags.Take(MaxTagsInPrompt).Select(tag => (tag.Slug, tag.Definition)))));
    }

    private static IReadOnlyList<InsightPromptModels.InsightTagModel> ToTagModels(IEnumerable<(string Slug, string Definition)> tags)
    {
        return tags.Select(tag => new InsightPromptModels.InsightTagModel(tag.Slug, tag.Definition)).ToList();
    }

    private static string BuildUserMessage(FindingClassificationRequest request)
    {
        var message = request.Message.Length > MaxMessageChars
            ? string.Concat(request.Message.AsSpan(0, MaxMessageChars), "\n…(truncated)")
            : request.Message;

        var anchor = BuildAnchor(request);

        // Which pass produced the finding is the strongest available signal for the level axis: a
        // pull-request-wide pass looks across files by construction, so its findings are rarely statement-level.
        var origin = string.IsNullOrWhiteSpace(request.OriginPassKind)
            ? "unknown"
            : request.OriginPassKind;

        return InsightPrompts.FindingTypeUser(
            new InsightPromptModels.FindingTypeUserModel(
                anchor,
                request.Severity.ToString(),
                origin,
                message));
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

    private static string BuildAnchor(FindingClassificationRequest request)
    {
        if (request.FilePath is null)
        {
            return "the pull request as a whole (no single file)";
        }

        if (request.LineNumber is null)
        {
            return request.FilePath;
        }

        return $"{request.FilePath}:{request.LineNumber.Value.ToString(CultureInfo.InvariantCulture)}";
    }
}
