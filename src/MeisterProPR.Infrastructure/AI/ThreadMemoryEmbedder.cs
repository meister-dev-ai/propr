// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Infrastructure implementation of <see cref="IThreadMemoryEmbedder" />.
///     Resolves the embedding connection per-client via <see cref="IAiConnectionRepository" />
///     and creates a transient <see cref="IEmbeddingGenerator{TInput,TEmbedding}" /> via
///     <see cref="IAiEmbeddingGeneratorFactory" /> for each operation.
///     Uses <see cref="IChatClient" /> for resolution summary generation.
/// </summary>
public sealed partial class ThreadMemoryEmbedder(
    IAiConnectionRepository aiConnectionRepository,
    IAiEmbeddingGeneratorFactory embeddingGeneratorFactory,
    EmbeddingDeploymentResolver embeddingDeploymentResolver,
    IOptions<AiReviewOptions> options,
    IChatClient? chatClient = null,
    IAiChatClientFactory? aiChatClientFactory = null,
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
        var deployment = await embeddingDeploymentResolver.ResolveForClientAsync(
            clientId,
            options.Value.MemoryEmbeddingDimensions,
            allowDefaultFallback: false,
            ct);

        var tokenCount = EmbeddingTokenizerRegistry.CountTokens(
            deployment.Capability.TokenizerName,
            compositeText);
        if (tokenCount > deployment.Capability.MaxInputTokens)
        {
            throw new InvalidOperationException(
                $"Embedding input exceeds the configured limit of {deployment.Capability.MaxInputTokens} tokens for deployment '{deployment.DeploymentName}'.");
        }

        var generator = embeddingGeneratorFactory.CreateGenerator(
            deployment.Connection.EndpointUrl,
            deployment.DeploymentName,
            deployment.Connection.ApiKey,
            deployment.Capability.EmbeddingDimensions);

        var result = await generator.GenerateAsync([compositeText], cancellationToken: ct);
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
            if (aiChatClientFactory is null)
            {
                return FallbackSummary;
            }

            var activeConnection = await aiConnectionRepository.GetActiveForClientAsync(clientId, ct);
            if (activeConnection is null)
            {
                return FallbackSummary;
            }

            modelId = activeConnection.ActiveModel ?? activeConnection.Models.FirstOrDefault() ?? modelId;
            effectiveChatClient = aiChatClientFactory.CreateClient(activeConnection.EndpointUrl, activeConnection.ApiKey);
        }

        try
        {
            var prompt = BuildResolutionSummaryPrompt(filePath, changeExcerpt, commentHistory);
            var messages = new[]
            {
                new ChatMessage(ChatRole.System,
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
                cancellationToken: ct);
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
        var parts = new System.Text.StringBuilder();
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
            parts.AppendLine("Note: No diff excerpt is available. If no code change can be determined from the comments, state explicitly that the resolution did not involve a code change.");
        }

        parts.AppendLine("Comment history:");
        parts.Append(commentHistory);

        return parts.ToString();
    }
}
