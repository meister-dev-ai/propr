using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>AI review implementation backed by an <see cref="IChatClient" />.</summary>
public sealed class AgentAiReviewCore(IChatClient chatClient, IOptions<AiReviewOptions> options) : IAiReviewCore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public async Task<ReviewResult> ReviewAsync(
        PullRequest pullRequest,
        ReviewSystemContext systemContext,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, ReviewPrompts.SystemPrompt),
            new(ChatRole.User, ReviewPrompts.BuildUserMessage(pullRequest)),
        };

        var chatOptions = new ChatOptions { MaxOutputTokens = 8192, ModelId = options.Value.ModelId };
        var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
        var json = response.Text ?? "";

        return ParseReviewResult(json);
    }

    private static ReviewResult ParseReviewResult(string json)
    {
        var dto = JsonSerializer.Deserialize<ReviewResultDto>(json, JsonOptions)
                  ?? throw new InvalidOperationException("AI returned null response.");

        var comments = (dto.Comments ?? []).Select(c => new ReviewComment(
                c.FilePath,
                c.LineNumber,
                Enum.TryParse<CommentSeverity>(c.Severity, true, out var sev) ? sev : CommentSeverity.Info,
                c.Message ?? ""))
            .ToList();

        return new ReviewResult(dto.Summary ?? "", comments.AsReadOnly());
    }

    private sealed record ReviewResultDto(string? Summary, List<ReviewCommentDto>? Comments);

    private sealed record ReviewCommentDto(string? FilePath, int? LineNumber, string? Severity, string? Message);
}
