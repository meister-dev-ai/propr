// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Infrastructure implementation of <see cref="IThreadMemoryEmbedder" />.
///     Resolves provider-neutral chat and embedding runtimes via <see cref="IAiRuntimeResolver" />.
///     Supports an optional <see cref="IChatClient" /> override for summary-generation tests.
/// </summary>
public sealed partial class ThreadMemoryEmbedder(
    IOptions<AiReviewOptions> options,
    IAiRuntimeResolver aiRuntimeResolver,
    IChatClient? chatClient = null,
    ILogger<ThreadMemoryEmbedder>? logger = null) : IThreadMemoryEmbedder
{
    private const string FallbackSummary =
        "Thread was resolved. No AI-generated summary could be produced at this time.";

    /// <inheritdoc />
    public async Task<float[]> GenerateEmbeddingAsync(
        string compositeText,
        Guid clientId,
        CancellationToken ct = default)
    {
        var runtime = await aiRuntimeResolver.ResolveEmbeddingRuntimeAsync(
            clientId,
            AiPurpose.EmbeddingDefault,
            options.Value.MemoryEmbeddingDimensions,
            ct);

        var maxInputTokens = runtime.Model.MaxInputTokens ?? 8192;
        var tokenCount = EmbeddingTokenizerRegistry.CountTokens(
            runtime.TokenizerName,
            compositeText);
        if (tokenCount > maxInputTokens)
        {
            throw new InvalidOperationException(
                $"Embedding input exceeds the configured limit of {maxInputTokens} tokens for deployment '{runtime.Model.RemoteModelId}'.");
        }

        var result = await runtime.Generator.GenerateAsync([compositeText], cancellationToken: ct);
        return result[0].Vector.ToArray();
    }

    /// <inheritdoc />
    public async Task<string> GenerateResolutionSummaryAsync(
        string? filePath,
        string? changeExcerpt,
        string commentHistory,
        Guid clientId,
        CancellationToken ct = default)
    {
        var effectiveChatClient = chatClient;
        var modelId = options.Value.ModelId;

        if (effectiveChatClient is null)
        {
            var runtime = await aiRuntimeResolver.ResolveChatRuntimeAsync(
                clientId,
                AiPurpose.MemoryReconsideration,
                ct);
            modelId = runtime.Model.RemoteModelId;
            effectiveChatClient = runtime.ChatClient;
        }

        try
        {
            var prompt = BuildResolutionSummaryPrompt(filePath, changeExcerpt, commentHistory);
            var messages = new[]
            {
                new ChatMessage(
                    ChatRole.System,
                    """
                    You are a code review analyst. Given a resolved PR review thread, write a concise 3-5 sentence summary that addresses ALL of the following:
                    1. What problem or concern was raised in the thread.
                    2. How the thread was resolved: state explicitly whether this was resolved by a code change or closed without one (e.g., acknowledged as intentional, dismissed as out-of-scope, or closed by the reviewer without a fix).
                    3. If a code change was made, identify specifically which change resolved the issue — name the method, variable, or line change if determinable from the diff excerpt or comments. If no code change was made or the specific change cannot be determined, state that explicitly.
                    4. Why the reviewer or author accepted this outcome.
                    Be precise and factual. Do not invent information not present in the context.
                    """),
                new ChatMessage(ChatRole.User, prompt),
            };

            var response = await effectiveChatClient.GetResponseAsync(
                messages,
                new ChatOptions { ModelId = modelId },
                ct);
            var text = response.Text;
            return string.IsNullOrWhiteSpace(text) ? FallbackSummary : text.Trim();
        }
        catch (Exception ex)
        {
            LogSummaryGenerationFailed(logger, ex);
            return FallbackSummary;
        }
    }

    private static string BuildResolutionSummaryPrompt(
        string? filePath,
        string? changeExcerpt,
        string commentHistory)
    {
        var parts = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            parts.AppendLine($"File: {filePath}");
        }

        if (!string.IsNullOrWhiteSpace(changeExcerpt))
        {
            parts.AppendLine("Change excerpt:");
            parts.AppendLine(changeExcerpt.Length > 500 ? changeExcerpt[..500] : changeExcerpt);
        }
        else
        {
            parts.AppendLine(
                "Note: No diff excerpt is available. If no code change can be determined from the comments, state explicitly that the resolution did not involve a code change.");
        }

        parts.AppendLine("Comment history:");
        parts.Append(commentHistory);

        return parts.ToString();
    }
}
