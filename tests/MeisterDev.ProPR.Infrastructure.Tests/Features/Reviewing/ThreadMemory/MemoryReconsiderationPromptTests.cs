// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.ThreadMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.ThreadMemory;

/// <summary>
///     Covers the single reconsideration prompt path: the shipped templates render the prompts the live
///     reconsideration call sends, and a configured prompt override replaces the system prompt the model receives.
/// </summary>
public sealed class MemoryReconsiderationPromptTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000009");

    [Fact]
    public void BuildSystemPrompt_WithoutOverride_RendersShippedTemplate()
    {
        var builder = new MemoryReconsiderationPromptBuilder();

        var prompt = builder.BuildSystemPrompt(null);

        Assert.Contains("RECONSIDERATION phase", prompt, StringComparison.Ordinal);
        Assert.Contains("confidence_evaluations", prompt, StringComparison.Ordinal);
        Assert.Contains("DISCARD", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSystemPrompt_WithConfiguredOverride_ReturnsOverrideText()
    {
        var builder = new MemoryReconsiderationPromptBuilder();
        var context = ContextWithOverride("Only keep findings a staff engineer would raise.");

        var prompt = builder.BuildSystemPrompt(context);

        Assert.Equal("Only keep findings a staff engineer would raise.", prompt);
    }

    [Fact]
    public void BuildUserMessage_WithSemanticMatch_RendersSimilarityAndResolution()
    {
        var builder = new MemoryReconsiderationPromptBuilder();
        var recordId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

        var message = builder.BuildUserMessage(
            "{\"summary\":\"Draft\"}",
            [new ThreadMemoryMatchDto(recordId, "42", "src/Foo.cs", "Previously accepted by design.", 0.92f)]);

        Assert.Contains("## Draft Findings from Initial Review", message, StringComparison.Ordinal);
        Assert.Contains("{\"summary\":\"Draft\"}", message, StringComparison.Ordinal);
        Assert.Contains("Entry 1", message, StringComparison.Ordinal);
        Assert.Contains("Similarity: 0.92", message, StringComparison.Ordinal);
        Assert.Contains(recordId.ToString(), message, StringComparison.Ordinal);
        Assert.Contains("- **File**: src/Foo.cs", message, StringComparison.Ordinal);
        Assert.Contains("- **How it was resolved**: Previously accepted by design.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUserMessage_WithAdminDismissedMatch_MarksEntryAsDismissedPattern()
    {
        var builder = new MemoryReconsiderationPromptBuilder();

        var message = builder.BuildUserMessage(
            "{}",
            [
                new ThreadMemoryMatchDto(
                    Guid.NewGuid(),
                    "0",
                    null,
                    "Nullable warnings on generated code.",
                    0.81f,
                    Source: MemorySource.AdminDismissed),
            ]);

        Assert.Contains("ADMIN-DISMISSED PATTERN", message, StringComparison.Ordinal);
        Assert.Contains("- **Dismissed pattern**: Nullable warnings on generated code.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("How it was resolved", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUserMessage_WithExactFileFallbackMatch_LabelsTheFallback()
    {
        var builder = new MemoryReconsiderationPromptBuilder();

        var message = builder.BuildUserMessage(
            "{}",
            [
                new ThreadMemoryMatchDto(
                    Guid.NewGuid(),
                    "3",
                    "src/Bar.cs",
                    "Team accepted the duplication.",
                    0.4f,
                    "exact_file_fallback"),
            ]);

        Assert.Contains("Exact file fallback", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Similarity:", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetrieveAndReconsiderAsync_WithConfiguredOverride_SendsOverriddenSystemPromptToTheModel()
    {
        var (service, chatClient) = CreateLiveReconsiderationService();
        var captured = CaptureSystemMessage(chatClient);
        var job = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "proj", "repo", 1, 1);

        await service.RetrieveAndReconsiderAsync(
            ClientId,
            job,
            "src/Foo.cs",
            "diff",
            new ReviewResult("draft summary", []),
            null,
            CancellationToken.None,
            null,
            ContextWithOverride("Reconsider in the client's own words."));

        Assert.Equal("Reconsider in the client's own words.", captured.Value);
    }

    [Fact]
    public async Task RetrieveAndReconsiderAsync_WithoutOverride_SendsTheShippedTemplateToTheModel()
    {
        var (service, chatClient) = CreateLiveReconsiderationService();
        var captured = CaptureSystemMessage(chatClient);
        var job = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "proj", "repo", 1, 1);

        await service.RetrieveAndReconsiderAsync(
            ClientId,
            job,
            "src/Foo.cs",
            "diff",
            new ReviewResult("draft summary", []),
            null);

        Assert.NotNull(captured.Value);
        Assert.Contains("RECONSIDERATION phase", captured.Value!, StringComparison.Ordinal);
    }

    private static ReviewSystemContext ContextWithOverride(string overrideText)
    {
        return new ReviewSystemContext(null, [], null)
        {
            PromptOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MemoryReconsiderationSystemPrompt"] = overrideText,
            },
        };
    }

    private static StringBox CaptureSystemMessage(IChatClient chatClient)
    {
        var box = new StringBox();
        var responseJson =
            """{"summary":"reconsidered","comments":[],"confidence_evaluations":[],"investigation_complete":true,"loop_complete":true}""";
        chatClient.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages =>
                    box.Value = messages.FirstOrDefault(message => message.Role == ChatRole.System)?.Text),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseJson))));
        return box;
    }

    private static (ThreadMemoryService Service, IChatClient ChatClient) CreateLiveReconsiderationService()
    {
        var embedder = Substitute.For<IThreadMemoryEmbedder>();
        var repository = Substitute.For<IThreadMemoryRepository>();
        var chatClient = Substitute.For<IChatClient>();

        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 0.5f });
        repository.FindSimilarAsync(
                Arg.Any<Guid>(),
                Arg.Any<float[]>(),
                Arg.Any<int>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new List<ThreadMemoryMatchDto>
                {
                    new(Guid.NewGuid(), "5", "src/Foo.cs", "Fixed by adding a null check.", 0.92f),
                });

        var service = new ThreadMemoryService(
            embedder,
            repository,
            Substitute.For<IProtocolRecorder>(),
            Substitute.For<IMemoryActivityLog>(),
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            NullLogger<ThreadMemoryService>.Instance,
            new MemoryReconsiderationPromptBuilder(),
            chatClient);

        return (service, chatClient);
    }

    private sealed class StringBox
    {
        public string? Value { get; set; }
    }
}
