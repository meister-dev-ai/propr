// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterProPR.ProRV.Abstractions;
using MeisterProPR.ProRV.Knowledge;
using MeisterProPR.ProRV.Models;
using MeisterProPR.ProRV.Prompting;
using Microsoft.Extensions.AI;

namespace MeisterProPR.ProRV.Core;

/// <summary>
///     Default ProRV prefilter implementation.
/// </summary>
public sealed class ProRVPrefilter : IProRVPrefilter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IProRVKnowledgeCatalog catalog;
    private readonly ProRVPromptFactory promptFactory;

    internal ProRVPrefilter(IProRVKnowledgeCatalog catalog, ProRVPromptFactory promptFactory)
    {
        this.catalog = catalog;
        this.promptFactory = promptFactory;
    }

    /// <inheritdoc />
    public async Task<ProRVPrefilterResult> RankRelevantItemsAsync(
        ProRVPrefilterRequest request,
        IChatClient chatClient,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(chatClient);

        var language = ResolveLanguage(request);
        if (string.IsNullOrWhiteSpace(language))
        {
            return new ProRVPrefilterResult(
                ProRVPrefilterStatus.UnsupportedLanguage,
                request.FilePath,
                null,
                [],
                failureReason: "The file path and technology hints could not be mapped to a supported ProRV language.");
        }

        var checks = this.catalog.GetCheckIndex(language);
        if (checks.Count == 0)
        {
            return new ProRVPrefilterResult(
                ProRVPrefilterStatus.EmptyCatalog,
                request.FilePath,
                language,
                [],
                failureReason: $"No ProRV checks are available for language '{language}'.");
        }

        var response = await chatClient.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, this.promptFactory.BuildApplicabilitySystemPrompt()),
                new ChatMessage(ChatRole.User, this.promptFactory.BuildApplicabilityUserMessage(language, request, checks)),
            ],
            chatOptions,
            cancellationToken);

        var responseText = response.Text ?? string.Empty;
        var inputTokens = response.Usage?.InputTokenCount;
        var outputTokens = response.Usage?.OutputTokenCount;

        // Native MEAI usage properties: the OpenAI adapter populates these on both the
        // Chat-Completions and Responses paths. No provider reports cache-write today.
        var cachedInputTokens = response.Usage?.CachedInputTokenCount;
        var reasoningTokens = response.Usage?.ReasoningTokenCount;
        if (!TryParseRankedChecks(responseText, checks, request.MaxResults, out var rankedChecks))
        {
            return new ProRVPrefilterResult(
                ProRVPrefilterStatus.UnparseableResponse,
                request.FilePath,
                language,
                [],
                responseText,
                "The ProRV prefilter model response could not be parsed.",
                inputTokens,
                outputTokens,
                cachedInputTokens: cachedInputTokens,
                reasoningTokens: reasoningTokens);
        }

        var items = rankedChecks
            .Select(item =>
            {
                var check = checks.First(candidate => string.Equals(candidate.Id, item.Id, StringComparison.Ordinal));
                return new ProRVRelevantItem(
                    check.Id,
                    check.Title,
                    check.ShortDescription,
                    this.catalog.GetInstruction(language, check.Id),
                    item.Reason,
                    item.Score,
                    check.Severity,
                    check.Precision,
                    check.Tags);
            })
            .ToList()
            .AsReadOnly();

        return new ProRVPrefilterResult(
            ProRVPrefilterStatus.Success,
            request.FilePath,
            language,
            items,
            responseText,
            inputTokens: inputTokens,
            outputTokens: outputTokens,
            cachedInputTokens: cachedInputTokens,
            reasoningTokens: reasoningTokens);
    }

    internal static string? ResolveLanguage(ProRVPrefilterRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            return request.Language.Trim().ToLowerInvariant();
        }

        var explicitLanguage = TryResolveExplicitLanguage(request.FilePath);
        if (!string.IsNullOrWhiteSpace(explicitLanguage))
        {
            return explicitLanguage;
        }

        if (request.TechnologyHints.Any(IsCsharpTechnologyHint))
        {
            return "csharp";
        }

        if (request.TechnologyHints.Any(IsJavascriptTechnologyHint))
        {
            return "javascript";
        }

        if (request.TechnologyHints.Any(IsCppTechnologyHint))
        {
            return "cpp";
        }

        if (request.TechnologyHints.Any(IsGoTechnologyHint))
        {
            return "go";
        }

        if (request.TechnologyHints.Any(IsJavaTechnologyHint))
        {
            return "java";
        }

        if (request.TechnologyHints.Any(IsPythonTechnologyHint))
        {
            return "python";
        }

        if (request.TechnologyHints.Any(IsRubyTechnologyHint))
        {
            return "ruby";
        }

        if (request.TechnologyHints.Any(IsRustTechnologyHint))
        {
            return "rust";
        }

        if (request.TechnologyHints.Any(IsSwiftTechnologyHint))
        {
            return "swift";
        }

        return null;
    }

    private static string? TryResolveExplicitLanguage(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".csx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".cshtml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".razor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".props", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".targets", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".config", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".resx", StringComparison.OrdinalIgnoreCase))
        {
            return "csharp";
        }

        if (string.Equals(extension, ".js", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".jsx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".mjs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".cjs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".ts", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".tsx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".mts", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".cts", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            return "javascript";
        }

        if (string.Equals(extension, ".c", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".cc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".cpp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".cxx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".h", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".hh", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".hpp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".hxx", StringComparison.OrdinalIgnoreCase))
        {
            return "cpp";
        }

        if (string.Equals(extension, ".go", StringComparison.OrdinalIgnoreCase))
        {
            return "go";
        }

        if (string.Equals(extension, ".java", StringComparison.OrdinalIgnoreCase))
        {
            return "java";
        }

        if (string.Equals(extension, ".py", StringComparison.OrdinalIgnoreCase))
        {
            return "python";
        }

        if (string.Equals(extension, ".rb", StringComparison.OrdinalIgnoreCase))
        {
            return "ruby";
        }

        if (string.Equals(extension, ".rs", StringComparison.OrdinalIgnoreCase))
        {
            return "rust";
        }

        if (string.Equals(extension, ".swift", StringComparison.OrdinalIgnoreCase))
        {
            return "swift";
        }

        return filePath.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase) &&
               (string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase))
            ? "actions"
            : null;
    }

    private static bool TryParseRankedChecks(
        string responseText,
        IReadOnlyList<ProRVCheckDefinition> checks,
        int maxResults,
        out IReadOnlyList<ParsedRankedCheck> rankedChecks)
    {
        rankedChecks = [];
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<PrefilterResponse>(StripMarkdownCodeFences(responseText), JsonOptions);
            if (parsed?.RankedChecks is null)
            {
                return false;
            }

            var checksById = checks.ToDictionary(check => check.Id, StringComparer.Ordinal);
            var ranked = parsed.RankedChecks
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .Where(item => checksById.ContainsKey(item.Id!))
                .Select(item => new ParsedRankedCheck(
                    item.Id!,
                    Math.Clamp(item.Score, 0, 100),
                    item.Reason?.Trim() ?? string.Empty))
                .DistinctBy(item => item.Id, StringComparer.Ordinal)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .Take(Math.Max(1, maxResults))
                .ToList();

            rankedChecks = ranked.AsReadOnly();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsCsharpTechnologyHint(string hint)
    {
        return string.Equals(hint, "csharp", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "dotnet", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, ".net", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "aspnet", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "asp.net", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "msbuild", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "nuget", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJavascriptTechnologyHint(string hint)
    {
        return string.Equals(hint, "javascript", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "js", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "typescript", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "ts", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "node", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "nodejs", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "react", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "vue", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "vite", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "npm", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "frontend", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "web", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCppTechnologyHint(string hint)
    {
        return string.Equals(hint, "cpp", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "c++", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "cxx", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "clang", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "cmake", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGoTechnologyHint(string hint)
    {
        return string.Equals(hint, "go", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "golang", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJavaTechnologyHint(string hint)
    {
        return string.Equals(hint, "java", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "jvm", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "gradle", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "maven", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPythonTechnologyHint(string hint)
    {
        return string.Equals(hint, "python", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "py", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "django", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "flask", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "fastapi", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRubyTechnologyHint(string hint)
    {
        return string.Equals(hint, "ruby", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "rails", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRustTechnologyHint(string hint)
    {
        return string.Equals(hint, "rust", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "cargo", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSwiftTechnologyHint(string hint)
    {
        return string.Equals(hint, "swift", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "swiftui", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hint, "xcode", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripMarkdownCodeFences(string responseText)
    {
        var trimmed = responseText.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
        {
            return trimmed.Trim('`').Trim();
        }

        var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFence <= firstNewline)
        {
            return trimmed[(firstNewline + 1)..].Trim();
        }

        return trimmed[(firstNewline + 1)..closingFence].Trim();
    }

    private sealed class PrefilterResponse
    {
        [JsonPropertyName("ranked_checks")]
        public List<RankedCheck>? RankedChecks { get; set; }
    }

    private sealed class RankedCheck
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("score")]
        public int Score { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }

    private sealed record ParsedRankedCheck(string Id, int Score, string Reason);
}
