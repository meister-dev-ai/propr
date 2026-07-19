// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI.ProCursor;
using MeisterProPR.ProCursor.Options;
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
        var expectedInputs = new[] { "alpha", "beta" };
        var firstVector = new[] { 0.1f, 0.2f };
        var secondVector = new[] { 0.3f, 0.4f };
        IReadOnlyList<string>? capturedInputs = null;
        int? capturedDimensions = null;

        var embeddingBroker = Substitute.For<IProCursorEmbeddingBroker>();
        embeddingBroker.GetDeploymentAsync(
                ClientId,
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ProCursorEmbeddingDeploymentDto(
                    connection.Id,
                    binding.RemoteModelId!,
                    model.TokenizerName!,
                    model.MaxInputTokens!.Value,
                    model.EmbeddingDimensions!.Value));
        embeddingBroker.GenerateEmbeddingsAsync(
                ClientId,
                Arg.Any<IReadOnlyList<string>>(),
                model.EmbeddingDimensions!.Value,
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedInputs = ((IReadOnlyList<string>)callInfo[1]).ToArray();
                capturedDimensions = (int?)callInfo[2];
                return new ProCursorEmbeddingBatchResponse(
                    [firstVector, secondVector],
                    2,
                    0,
                    2);
            });

        var options = Microsoft.Extensions.Options.Options.Create(
            new ProCursorOptions
            {
                EmbeddingDimensions = model.EmbeddingDimensions!.Value,
            });

        var service = new ProCursorEmbeddingService(options, embeddingBroker);

        var result = await service.GenerateEmbeddingsAsync(ClientId, expectedInputs);

        Assert.Equal(2, result.Count);
        Assert.Equal([0.1f, 0.2f], result[0]);
        Assert.Equal([0.3f, 0.4f], result[1]);
        await embeddingBroker.Received(1).GetDeploymentAsync(ClientId, model.EmbeddingDimensions!.Value, Arg.Any<CancellationToken>());
        await embeddingBroker.Received(1).GenerateEmbeddingsAsync(
            ClientId,
            Arg.Any<IReadOnlyList<string>>(),
            model.EmbeddingDimensions!.Value,
            Arg.Any<CancellationToken>());
        Assert.Equal(expectedInputs, capturedInputs);
        Assert.Equal(model.EmbeddingDimensions!.Value, capturedDimensions);
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_RecordsEstimatedCost_MatchingLegacyFormula()
    {
        const string tokenizer = "cl100k_base";
        const int dimensions = 1536;
        const decimal inputRate = 0.13m;
        const decimal outputRate = 0.26m;
        const long promptTokens = 1500;
        const long completionTokens = 7;

        var deployment = new ProCursorEmbeddingDeploymentDto(
            Guid.NewGuid(),
            "text-embedding-3-small",
            tokenizer,
            8192,
            dimensions,
            inputRate,
            outputRate);

        var embeddingBroker = Substitute.For<IProCursorEmbeddingBroker>();
        embeddingBroker.GetDeploymentAsync(ClientId, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(deployment);
        embeddingBroker.GenerateEmbeddingsAsync(
                ClientId,
                Arg.Any<IReadOnlyList<string>>(),
                dimensions,
                Arg.Any<CancellationToken>())
            .Returns(
                new ProCursorEmbeddingBatchResponse(
                    [[0.1f, 0.2f]],
                    promptTokens,
                    completionTokens,
                    promptTokens + completionTokens));

        ProCursorTokenUsageCaptureRequest? captured = null;
        var recorder = Substitute.For<IProCursorTokenUsageRecorder>();
        recorder
            .When(r => r.RecordAsync(Arg.Any<ProCursorTokenUsageCaptureRequest>(), Arg.Any<CancellationToken>()))
            .Do(callInfo => captured = callInfo.Arg<ProCursorTokenUsageCaptureRequest>());

        var options = Microsoft.Extensions.Options.Options.Create(new ProCursorOptions { EmbeddingDimensions = dimensions });
        var service = new ProCursorEmbeddingService(options, embeddingBroker, recorder);

        var usageContext = new ProCursorEmbeddingUsageContext(
            Guid.NewGuid(),
            "source-name",
            "req-prefix");

        _ = await service.GenerateEmbeddingsAsync(ClientId, ["alpha"], usageContext);

        Assert.NotNull(captured);

        // The refactor to the shared calculator MUST reproduce the legacy embedding cost value exactly.
        var expected = (inputRate * promptTokens / 1_000_000m) + (outputRate * completionTokens / 1_000_000m);
        Assert.Equal(expected, captured!.EstimatedCostUsd);
        Assert.False(captured.TokensEstimated);
    }
}
