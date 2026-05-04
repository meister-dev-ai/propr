// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI.ProCursor;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class ProCursorEmbeddingServiceTests
{
    private static readonly Guid ClientId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    [Fact]
    public async Task GenerateEmbeddingsAsync_WithResolvedEmbeddingRuntime_UsesRuntimeGenerator()
    {
        var connection = AiConnectionTestFactory.CreateEmbeddingConnection(ClientId);
        var binding = connection.PurposeBindings.Single(candidate => candidate.Purpose == AiPurpose.EmbeddingDefault);
        var model = connection.ConfiguredModels.Single(candidate => candidate.Id == binding.ConfiguredModelId);
        var firstVector = new[] { 0.1f, 0.2f };
        var secondVector = new[] { 0.3f, 0.4f };

        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new GeneratedEmbeddings<Embedding<float>>(
                [
                    new Embedding<float>(firstVector),
                    new Embedding<float>(secondVector),
                ]));

        var runtime = Substitute.For<IResolvedAiEmbeddingRuntime>();
        runtime.Connection.Returns(connection);
        runtime.Binding.Returns(binding);
        runtime.Model.Returns(model);
        runtime.Generator.Returns(generator);
        runtime.TokenizerName.Returns(model.TokenizerName!);
        runtime.Dimensions.Returns(model.EmbeddingDimensions!.Value);

        var runtimeResolver = Substitute.For<IAiRuntimeResolver>();
        runtimeResolver.ResolveEmbeddingRuntimeAsync(
                ClientId,
                AiPurpose.EmbeddingDefault,
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(runtime);

        var options = Microsoft.Extensions.Options.Options.Create(
            new ProCursorOptions
            {
                EmbeddingDimensions = model.EmbeddingDimensions!.Value,
            });

        var service = new ProCursorEmbeddingService(options, runtimeResolver);

        var result = await service.GenerateEmbeddingsAsync(ClientId, ["alpha", "beta"]);

        Assert.Equal(2, result.Count);
        Assert.Equal([0.1f, 0.2f], result[0]);
        Assert.Equal([0.3f, 0.4f], result[1]);
    }
}
