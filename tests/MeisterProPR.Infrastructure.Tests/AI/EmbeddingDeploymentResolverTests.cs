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
    public async Task ResolveForClientAsync_ConfiguredEmbeddingBinding_UsesProviderNeutralModelMetadata()
    {
        var repository = Substitute.For<IAiConnectionRepository>();
        repository.GetForTierAsync(ClientId, AiConnectionModelCategory.Embedding, Arg.Any<CancellationToken>())
            .Returns(AiConnectionTestFactory.CreateEmbeddingConnection(ClientId));

        var resolver = new EmbeddingDeploymentResolver(repository);

        var deployment = await resolver.ResolveForClientAsync(ClientId, 1536, false, CancellationToken.None);

        Assert.Equal("text-embedding-3-small", deployment.DeploymentName);
        Assert.Equal("cl100k_base", deployment.Capability.TokenizerName);
        Assert.Equal(8192, deployment.Capability.MaxInputTokens);
        Assert.Equal(1536, deployment.Capability.EmbeddingDimensions);
    }

    [Fact]
    public async Task ResolveForClientAsync_DoesNotFallBackToGenericActiveProfile_WhenEmbeddingBindingMissing()
    {
        var repository = Substitute.For<IAiConnectionRepository>();
        repository.GetForTierAsync(ClientId, AiConnectionModelCategory.Embedding, Arg.Any<CancellationToken>())
            .Returns((AiConnectionDto?)null);
        repository.GetActiveForClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(AiConnectionTestFactory.CreateEmbeddingConnection(ClientId));

        var resolver = new EmbeddingDeploymentResolver(repository);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveForClientAsync(ClientId, 1536, true, CancellationToken.None));

        Assert.Equal("no_embedding_connection_configured", exception.Message);
        await repository.DidNotReceive().GetActiveForClientAsync(ClientId, Arg.Any<CancellationToken>());
    }
}
