// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Drivers;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     A host builds a client to describe a family as well as to call it, and the driver checks build one
///     against an endpoint that carries no host context. Construction therefore has to succeed with no transport
///     available, and the refusal has to wait for the call.
/// </summary>
public sealed class UnreachableClientsTests
{
    [Fact]
    public void AChatClientIsBuiltWithoutATransport()
    {
        using var client = UnreachableClients.ChatClient("Acme");

        Assert.NotNull(client);
    }

    [Fact]
    public async Task AChatCallRefusesAndNamesTheFamily()
    {
        using var client = UnreachableClients.ChatClient("Acme");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Contains("Acme", failure.Message, StringComparison.Ordinal);
        Assert.Contains("no client factory", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStreamingChatCallRefusesToo()
    {
        using var client = UnreachableClients.ChatClient("Acme");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
                // The refusal is thrown before the first update is produced.
            }
        });
    }

    [Fact]
    public async Task AnEmbeddingCallRefusesAndNamesTheFamily()
    {
        using var generator = UnreachableClients.EmbeddingGenerator("Acme");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => generator.GenerateAsync(["hello"]));

        Assert.Contains("Acme", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFamilyThatDoesNotNameItselfGetsTheSameSentenceWithoutAName()
    {
        Assert.StartsWith(
            "This connection was handed no client factory,",
            UnreachableClients.Refusal(),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "This Acme connection was handed no client factory,",
            UnreachableClients.Refusal("Acme"),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankNameReadsAsNoName(string? familyName)
    {
        Assert.Equal(UnreachableClients.Refusal(), UnreachableClients.Refusal(familyName));
    }

    [Fact]
    public void AClientAnswersForItsOwnServiceTypeAndNothingElse()
    {
        using var client = UnreachableClients.ChatClient("Acme");

        Assert.Same(client, client.GetService(typeof(IChatClient)));
        Assert.Null(client.GetService(typeof(IChatClient), serviceKey: "keyed"));
        Assert.Null(client.GetService(typeof(HttpClient)));
    }
}
