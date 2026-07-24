// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Repositories;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class LogicalModelCapabilityValidatorTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000f0118");

    private readonly IAiConnectionRepository _connections = Substitute.For<IAiConnectionRepository>();

    private LogicalModelCapabilityValidator Sut()
    {
        return new LogicalModelCapabilityValidator(this._connections);
    }

    [Fact]
    public async Task ValidChatModel_Passes()
    {
        var modelId = Guid.NewGuid();
        var model = AiConnectionTestFactory.CreateChatModel("gpt-x", modelId);
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [model]);
        this._connections.GetByIdAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);

        // Must not throw.
        await this.Sut().ValidateAsync(Entry("deep", AiOperationKind.Chat, connection.Id, modelId));
    }

    [Fact]
    public async Task ValidEmbeddingModel_Passes()
    {
        var modelId = Guid.NewGuid();
        var model = AiConnectionTestFactory.CreateEmbeddingModel("embed-x", 1536, modelId);
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [model]);
        this._connections.GetByIdAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);

        await this.Sut().ValidateAsync(Entry("embed", AiOperationKind.Embedding, connection.Id, modelId));
    }

    [Fact]
    public async Task ChatRole_OnEmbeddingOnlyModel_Throws()
    {
        var modelId = Guid.NewGuid();
        var model = AiConnectionTestFactory.CreateEmbeddingModel("embed-x", 1536, modelId);
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [model]);
        this._connections.GetByIdAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);

        var ex = await Assert.ThrowsAsync<LogicalModelReferenceInvalidException>(() =>
            this.Sut().ValidateAsync(Entry("deep", AiOperationKind.Chat, connection.Id, modelId)));
        Assert.Equal("deep", ex.RoleName);
        Assert.Contains("chat", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmbeddingRole_OnChatOnlyModel_Throws()
    {
        var modelId = Guid.NewGuid();
        var model = AiConnectionTestFactory.CreateChatModel("gpt-x", modelId);
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [model]);
        this._connections.GetByIdAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);

        var ex = await Assert.ThrowsAsync<LogicalModelReferenceInvalidException>(() =>
            this.Sut().ValidateAsync(Entry("embed", AiOperationKind.Embedding, connection.Id, modelId)));
        Assert.Contains("embedding", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingConnection_Throws()
    {
        var connectionId = Guid.NewGuid();
        this._connections.GetByIdAsync(connectionId, Arg.Any<CancellationToken>()).Returns((AiConnectionDto?)null);

        var ex = await Assert.ThrowsAsync<LogicalModelReferenceInvalidException>(() =>
            this.Sut().ValidateAsync(Entry("deep", AiOperationKind.Chat, connectionId, Guid.NewGuid())));
        Assert.Contains(connectionId.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingModelOnConnection_Throws()
    {
        var missingModelId = Guid.NewGuid();
        var otherModel = AiConnectionTestFactory.CreateChatModel("other");
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [otherModel]);
        this._connections.GetByIdAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);

        var ex = await Assert.ThrowsAsync<LogicalModelReferenceInvalidException>(() =>
            this.Sut().ValidateAsync(Entry("deep", AiOperationKind.Chat, connection.Id, missingModelId)));
        Assert.Contains(missingModelId.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbeddingModel_MissingMetadata_Throws()
    {
        var modelId = Guid.NewGuid();
        // Declares embedding capability but has no tokenizer / dimensions metadata.
        var model = new AiConfiguredModelDto(
            modelId,
            "embed-nometa",
            "embed-nometa",
            [AiOperationKind.Embedding],
            [AiProtocolMode.Embeddings]);
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [model]);
        this._connections.GetByIdAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);

        var ex = await Assert.ThrowsAsync<LogicalModelReferenceInvalidException>(() =>
            this.Sut().ValidateAsync(Entry("embed", AiOperationKind.Embedding, connection.Id, modelId)));
        Assert.Contains("metadata", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // A model that supports BOTH chat and embedding validates for either role — the switch tests only the declared
    // capability, and independent SupportsChat/SupportsEmbedding checks both hold.
    [Fact]
    public async Task DualCapabilityModel_PassesForBothChatAndEmbedding()
    {
        var modelId = Guid.NewGuid();
        var model = new AiConfiguredModelDto(
            modelId,
            "omni",
            "omni",
            [AiOperationKind.Chat, AiOperationKind.Embedding],
            [AiProtocolMode.Auto, AiProtocolMode.Embeddings],
            TokenizerName: "cl100k_base",
            EmbeddingDimensions: 1536);
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [model]);
        this._connections.GetByIdAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);

        await this.Sut().ValidateAsync(Entry("deep", AiOperationKind.Chat, connection.Id, modelId));
        await this.Sut().ValidateAsync(Entry("embed", AiOperationKind.Embedding, connection.Id, modelId));
    }

    private static LogicalModelDto Entry(string name, AiOperationKind capability, Guid connectionId, Guid modelId)
    {
        return new LogicalModelDto(
            Guid.NewGuid(),
            name,
            capability,
            connectionId,
            modelId,
            ReviewReasoningEffort.None,
            AiProtocolMode.Auto);
    }
}
