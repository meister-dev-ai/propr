// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class EmbeddingDeploymentResolverTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    [Fact]
    public async Task ResolveForClientAsync_LegacyConnectionWithoutCapabilities_UsesKnownModelDefaults()
    {
        var repository = Substitute.For<IAiConnectionRepository>();
        repository.GetForTierAsync(ClientId, AiConnectionModelCategory.Embedding, Arg.Any<CancellationToken>())
            .Returns(new AiConnectionDto(
                Guid.NewGuid(),
                ClientId,
                "Embedding",
                "https://example.openai.azure.com",
                ["text-embedding-3-small"],
                false,
                "text-embedding-3-small",
                DateTimeOffset.UtcNow,
                AiConnectionModelCategory.Embedding,
                [],
                "test-key"));

        var resolver = new EmbeddingDeploymentResolver(repository);

        var deployment = await resolver.ResolveForClientAsync(ClientId, 1536, false, CancellationToken.None);

        Assert.Equal("text-embedding-3-small", deployment.DeploymentName);
        Assert.Equal("cl100k_base", deployment.Capability.TokenizerName);
        Assert.Equal(8192, deployment.Capability.MaxInputTokens);
        Assert.Equal(1536, deployment.Capability.EmbeddingDimensions);
    }

    [Fact]
    public async Task ResolveForClientAsync_UnknownLegacyConnectionWithoutCapabilities_StillThrows()
    {
        var repository = Substitute.For<IAiConnectionRepository>();
        repository.GetForTierAsync(ClientId, AiConnectionModelCategory.Embedding, Arg.Any<CancellationToken>())
            .Returns(new AiConnectionDto(
                Guid.NewGuid(),
                ClientId,
                "Embedding",
                "https://example.openai.azure.com",
                ["custom-embedding-deployment"],
                false,
                "custom-embedding-deployment",
                DateTimeOffset.UtcNow,
                AiConnectionModelCategory.Embedding,
                [],
                "test-key"));

        var resolver = new EmbeddingDeploymentResolver(repository);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveForClientAsync(ClientId, 1536, false, CancellationToken.None));

        Assert.Contains("missing capability metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
