// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Unit tests for <see cref="AiRepositoryInstructionEvaluator" />.
/// </summary>
public class AiRepositoryInstructionEvaluatorTests
{
    private static RepositoryInstruction CreateInstruction(string fileName, string description, string whenToUse)
    {
        return new RepositoryInstruction(
            fileName,
            description,
            whenToUse,
            $"\"\"\"\ndescription: {description}\nwhen-to-use: {whenToUse}\n\"\"\"\nBody text.");
    }

    private static ChatResponse CreateRelevantResponse(IReadOnlyList<string> relevantFileNames)
    {
        var json = JsonSerializer.Serialize(
            new
            {
                relevant_instructions = relevantFileNames,
            });
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, json));
    }

    [Fact]
    public async Task EvaluateRelevanceAsync_EmptyInstructionList_ReturnsEmptyWithoutCallingLlm()
    {
        // Arrange
        var mockClient = Substitute.For<IChatClient>();
        var sut = new AiRepositoryInstructionEvaluator(
            mockClient,
            Microsoft.Extensions.Options.Options.Create(new AiEvaluatorOptions { Endpoint = "https://test.openai.azure.com", Deployment = "test-model" }),
            Substitute.For<ILogger<AiRepositoryInstructionEvaluator>>());

        // Act
        var result = await sut.EvaluateRelevanceAsync(
            [],
            ["/src/Foo.cs"],
            CancellationToken.None);

        // Assert — empty list returned, LLM not called
        Assert.Empty(result);
        await mockClient.DidNotReceive()
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateRelevanceAsync_RelevantInstruction_IncludedInOutput()
    {
        // Arrange
        var mockClient = Substitute.For<IChatClient>();
        var instruction = CreateInstruction(
            "instructions-csharp.md",
            "C# coding standards",
            "When reviewing .cs files");

        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateRelevantResponse(["instructions-csharp.md"]));

        var sut = new AiRepositoryInstructionEvaluator(
            mockClient,
            Microsoft.Extensions.Options.Options.Create(new AiEvaluatorOptions { Endpoint = "https://test.openai.azure.com", Deployment = "test-model" }),
            Substitute.For<ILogger<AiRepositoryInstructionEvaluator>>());

        // Act
        var result = await sut.EvaluateRelevanceAsync(
            [instruction],
            ["/src/MyService.cs"],
            CancellationToken.None);

        // Assert — relevant instruction included
        Assert.Single(result);
        Assert.Equal("instructions-csharp.md", result[0].FileName);
    }

    [Fact]
    public async Task EvaluateRelevanceAsync_IrrelevantInstruction_ExcludedFromOutput()
    {
        // Arrange — LLM says no instructions are relevant
        var mockClient = Substitute.For<IChatClient>();
        var instruction = CreateInstruction(
            "instructions-database.md",
            "Database migration rules",
            "When reviewing SQL or migration files");

        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateRelevantResponse([])); // empty relevant list

        var sut = new AiRepositoryInstructionEvaluator(
            mockClient,
            Microsoft.Extensions.Options.Options.Create(new AiEvaluatorOptions { Endpoint = "https://test.openai.azure.com", Deployment = "test-model" }),
            Substitute.For<ILogger<AiRepositoryInstructionEvaluator>>());

        // Act
        var result = await sut.EvaluateRelevanceAsync(
            [instruction],
            ["/src/MyService.cs"], // only C# file, no DB migration
            CancellationToken.None);

        // Assert — irrelevant instruction excluded
        Assert.Empty(result);
    }

    [Fact]
    public async Task EvaluateRelevanceAsync_MixedInstructions_OnlyRelevantOnesReturned()
    {
        // Arrange
        var mockClient = Substitute.For<IChatClient>();
        var csharpInstruction = CreateInstruction("instructions-csharp.md", "C# standards", "When reviewing .cs files");
        var dbInstruction = CreateInstruction("instructions-db.md", "DB rules", "When reviewing SQL files");
        var secInstruction = CreateInstruction("instructions-security.md", "Security rules", "For auth-related files");

        // LLM returns only csharp and security as relevant
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateRelevantResponse(["instructions-csharp.md", "instructions-security.md"]));

        var sut = new AiRepositoryInstructionEvaluator(
            mockClient,
            Microsoft.Extensions.Options.Options.Create(new AiEvaluatorOptions { Endpoint = "https://test.openai.azure.com", Deployment = "test-model" }),
            Substitute.For<ILogger<AiRepositoryInstructionEvaluator>>());

        // Act
        var result = await sut.EvaluateRelevanceAsync(
            [csharpInstruction, dbInstruction, secInstruction],
            ["/src/AuthService.cs", "/src/UserController.cs"],
            CancellationToken.None);

        // Assert — only relevant instructions returned, preserving original RepositoryInstruction objects
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.FileName == "instructions-csharp.md");
        Assert.Contains(result, r => r.FileName == "instructions-security.md");
        Assert.DoesNotContain(result, r => r.FileName == "instructions-db.md");
    }

    [Fact]
    public async Task EvaluateRelevanceAsync_LlmReturnsMalformedJson_ThrowsOrReturnsEmpty()
    {
        // Arrange — LLM returns unexpected/malformed JSON
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "this is not valid json")));

        var instruction = CreateInstruction("instructions-test.md", "Test rules", "For test files");

        var sut = new AiRepositoryInstructionEvaluator(
            mockClient,
            Microsoft.Extensions.Options.Options.Create(new AiEvaluatorOptions { Endpoint = "https://test.openai.azure.com", Deployment = "test-model" }),
            Substitute.For<ILogger<AiRepositoryInstructionEvaluator>>());

        // Act & Assert — now always returns empty list (fail-safe)
        var result = await sut.EvaluateRelevanceAsync(
            [instruction],
            ["/tests/FooTests.cs"],
            CancellationToken.None);

        Assert.Empty(result);
    }
}
