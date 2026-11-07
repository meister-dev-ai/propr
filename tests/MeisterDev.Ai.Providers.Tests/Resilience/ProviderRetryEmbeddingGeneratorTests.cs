// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Resilience;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Resilience;

/// <summary>
///     Covers the failure the embedding retry stage reports, which an indexing job records as its cause.
/// </summary>
public sealed class ProviderRetryEmbeddingGeneratorTests
{
    private static readonly ProviderCallTarget Target =
        new("meisterdev/openAi", "text-embedding-3-small", "Primary OpenAI");

    private static readonly ProviderRetryPolicy Immediate = new()
    {
        MaxAttempts = 2,
        BaseDelay = TimeSpan.Zero,
        MaxDelay = TimeSpan.FromSeconds(30),
        JitterFactor = 0,
    };

    // The embedding stage reports its failure the way the chat stage does: with what the provider wrote, and
    // without the credential the connection holds.
    [Fact]
    public async Task TheFailureCarriesWhatTheProviderWroteWithTheCredentialRedacted()
    {
        const string key = "sk-live-fedcba9876543210";
        using var generator = new ProviderRetryEmbeddingGenerator(
            new FailingGenerator(new HttpRequestException("refused", null, System.Net.HttpStatusCode.BadRequest)),
            Immediate,
            exception => DriverFailureMapper.ClassifyRuntimeFailure(exception) with
            {
                ProviderMessage = $"This model's maximum context length is 8192 tokens. Key {key} was used.",
            },
            Target,
            credentialValues: [key]);

        var failure = await Assert.ThrowsAsync<ProviderCallFailedException>(() => generator.GenerateAsync(["hello"]));

        Assert.Equal(1, failure.Attempts);
        Assert.DoesNotContain(key, failure.Message, StringComparison.Ordinal);
        Assert.Contains(
            "This model's maximum context length is 8192 tokens. Key [redacted] was used.",
            failure.Message,
            StringComparison.Ordinal);
    }

    /// <summary>Fails every call with the same exception.</summary>
    private sealed class FailingGenerator(Exception failure) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<GeneratedEmbeddings<Embedding<float>>>(failure);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
