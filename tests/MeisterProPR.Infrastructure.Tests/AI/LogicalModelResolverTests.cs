// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class LogicalModelResolverTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000117");

    private readonly ILogicalModelCatalogRepository _catalog = Substitute.For<ILogicalModelCatalogRepository>();
    private readonly IAiConnectionRepository _connections = Substitute.For<IAiConnectionRepository>();
    private readonly IAiRuntimeFactory _runtimeFactory = Substitute.For<IAiRuntimeFactory>();

    public LogicalModelResolverTests()
    {
        // Default: no overrides, no tenant entries. Individual tests override as needed.
        this._catalog.GetClientOverridesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<LogicalModelDto>());
        this._catalog.GetTenantEntriesForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<LogicalModelDto>());
    }

    private LogicalModelResolver Sut()
    {
        return new LogicalModelResolver(this._catalog, this._connections, this._runtimeFactory);
    }

    // AC #1: no client override → the tenant entry's connection + model + settings are used.
    [Fact]
    public async Task ResolveChat_NoOverride_UsesTenantEntryAndSettings()
    {
        var modelId = Guid.NewGuid();
        var model = AiConnectionTestFactory.CreateChatModel("deep-model", modelId);
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [model]);
        var mapping = ChatMapping("deep", connection.Id, modelId, ReviewReasoningEffort.High, AiProtocolMode.Responses);
        this.SetTenantEntries(mapping);
        this._connections.GetByIdAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        this._runtimeFactory.CreateChatRuntime(connection, model, Arg.Any<AiPurposeBindingDto>(), Arg.Any<string?>()).Returns(runtime);

        var result = await this.Sut().ResolveChatRuntimeAsync(ClientId, "deep");

        Assert.Same(runtime, result.Runtime);
        Assert.Equal(LogicalModelLayer.TenantCatalog, result.Layer);
        Assert.Equal(ReviewReasoningEffort.High, result.ReasoningEffort);
        // The synthesized binding must carry the mapping's protocol mode, and the runtime is tagged with the role name.
        this._runtimeFactory.Received(1).CreateChatRuntime(
            connection, model, Arg.Is<AiPurposeBindingDto>(b => b.ProtocolMode == AiProtocolMode.Responses), "deep");
    }

    // AC #2: a client override takes precedence over the tenant entry of the same name.
    [Fact]
    public async Task ResolveChat_OverridePresent_TakesPrecedenceOverTenant()
    {
        var tenantModelId = Guid.NewGuid();
        var tenantModel = AiConnectionTestFactory.CreateChatModel("tenant-model", tenantModelId);
        var tenantConnection = AiConnectionTestFactory.CreateConnection(ClientId, [tenantModel]);
        this.SetTenantEntries(ChatMapping("deep", tenantConnection.Id, tenantModelId, ReviewReasoningEffort.Low, AiProtocolMode.Auto));

        var overrideModelId = Guid.NewGuid();
        var overrideModel = AiConnectionTestFactory.CreateChatModel("override-model", overrideModelId);
        var overrideConnection = AiConnectionTestFactory.CreateConnection(ClientId, [overrideModel]);
        this.SetOverrides(ChatMapping("deep", overrideConnection.Id, overrideModelId, ReviewReasoningEffort.Medium, AiProtocolMode.ChatCompletions));

        this._connections.GetByIdAsync(overrideConnection.Id, Arg.Any<CancellationToken>()).Returns(overrideConnection);
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        this._runtimeFactory.CreateChatRuntime(overrideConnection, overrideModel, Arg.Any<AiPurposeBindingDto>(), Arg.Any<string?>()).Returns(runtime);

        var result = await this.Sut().ResolveChatRuntimeAsync(ClientId, "deep");

        Assert.Same(runtime, result.Runtime);
        Assert.Equal(LogicalModelLayer.ClientOverride, result.Layer);
        Assert.Equal(ReviewReasoningEffort.Medium, result.ReasoningEffort);
        // The tenant connection must never be loaded when an override wins.
        await this._connections.DidNotReceive().GetByIdAsync(tenantConnection.Id, Arg.Any<CancellationToken>());
    }

    // AC #4: asking for a chat runtime on an embedding-typed role fails clearly.
    [Fact]
    public async Task ResolveChat_OnEmbeddingRole_Throws()
    {
        this.SetTenantEntries(
            new LogicalModelDto(
                Guid.NewGuid(), "embed", AiOperationKind.Embedding, Guid.NewGuid(), Guid.NewGuid(),
                ReviewReasoningEffort.None, AiProtocolMode.Embeddings));

        var ex = await Assert.ThrowsAsync<LogicalModelCapabilityMismatchException>(() => this.Sut().ResolveChatRuntimeAsync(ClientId, "embed"));
        Assert.Equal(AiOperationKind.Chat, ex.Expected);
        Assert.Equal(AiOperationKind.Embedding, ex.Actual);
    }

    // AC #4: asking for an embedding runtime on a chat-typed role fails clearly.
    [Fact]
    public async Task ResolveEmbedding_OnChatRole_Throws()
    {
        this.SetTenantEntries(ChatMapping("deep", Guid.NewGuid(), Guid.NewGuid(), ReviewReasoningEffort.None, AiProtocolMode.Auto));

        var ex = await Assert.ThrowsAsync<LogicalModelCapabilityMismatchException>(() => this.Sut().ResolveEmbeddingRuntimeAsync(ClientId, "deep"));
        Assert.Equal(AiOperationKind.Embedding, ex.Expected);
        Assert.Equal(AiOperationKind.Chat, ex.Actual);
    }

    // AC #4 (positive): an embedding-typed role resolves an embedding runtime.
    [Fact]
    public async Task ResolveEmbedding_EmbeddingRole_UsesEmbeddingRuntime()
    {
        var modelId = Guid.NewGuid();
        var model = AiConnectionTestFactory.CreateEmbeddingModel("embed-model", 1536, modelId);
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [model]);
        this.SetOverrides(
            new LogicalModelDto(
                Guid.NewGuid(), "embed", AiOperationKind.Embedding, connection.Id, modelId,
                ReviewReasoningEffort.None, AiProtocolMode.Embeddings));
        this._connections.GetByIdAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);
        var runtime = Substitute.For<IResolvedAiEmbeddingRuntime>();
        this._runtimeFactory
            .CreateEmbeddingRuntime(connection, model, Arg.Any<AiPurposeBindingDto>(), "cl100k_base", 1536, Arg.Any<string?>())
            .Returns(runtime);

        var result = await this.Sut().ResolveEmbeddingRuntimeAsync(ClientId, "embed", 1536);

        Assert.Same(runtime, result.Runtime);
        Assert.Equal(LogicalModelLayer.ClientOverride, result.Layer);
    }

    // AC #5: an unknown role (no override, no tenant entry) fails with a clear, named error.
    [Fact]
    public async Task ResolveChat_UnknownRole_Throws()
    {
        var ex = await Assert.ThrowsAsync<LogicalModelNotFoundException>(() => this.Sut().ResolveChatRuntimeAsync(ClientId, "missing"));
        Assert.Equal("missing", ex.RoleName);
    }

    // A mapping that points at a connection that no longer exists fails clearly (integrity is enforced).
    [Fact]
    public async Task ResolveChat_MissingConnection_Throws()
    {
        var connectionId = Guid.NewGuid();
        this.SetTenantEntries(ChatMapping("deep", connectionId, Guid.NewGuid(), ReviewReasoningEffort.None, AiProtocolMode.Auto));
        this._connections.GetByIdAsync(connectionId, Arg.Any<CancellationToken>()).Returns((AiConnectionDto?)null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => this.Sut().ResolveChatRuntimeAsync(ClientId, "deep"));
        Assert.Contains(connectionId.ToString(), ex.Message, StringComparison.Ordinal);
    }

    // AC #3: the resolved role name + layer are recorded to the protocol/trace when a recorder is supplied.
    [Fact]
    public async Task ResolveChat_RecordsResolutionEvent_WithRoleAndLayer()
    {
        var modelId = Guid.NewGuid();
        var model = AiConnectionTestFactory.CreateChatModel("deep-model", modelId);
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [model]);
        this.SetTenantEntries(ChatMapping("deep", connection.Id, modelId, ReviewReasoningEffort.High, AiProtocolMode.Responses));
        this._connections.GetByIdAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);
        this._runtimeFactory.CreateChatRuntime(connection, model, Arg.Any<AiPurposeBindingDto>(), Arg.Any<string?>())
            .Returns(Substitute.For<IResolvedAiChatRuntime>());
        var recorder = Substitute.For<IProtocolRecorder>();
        var protocolId = Guid.NewGuid();

        await this.Sut().ResolveChatRuntimeAsync(ClientId, "deep", recorder, protocolId);

        await recorder.Received(1).RecordLogicalModelResolutionEventAsync(
            protocolId,
            Arg.Is<string>(n => n == "logical_model_resolved"),
            Arg.Is<string?>(d => d != null && d.Contains("\"deep\"", StringComparison.Ordinal) && d.Contains("TenantCatalog", StringComparison.Ordinal)),
            Arg.Is<string?>(o => o == null),
            Arg.Is<string?>(e => e == null),
            Arg.Any<CancellationToken>());
    }

    // AC #3 (guard): a recorder with no protocol id is a no-op — nothing is recorded.
    [Fact]
    public async Task ResolveChat_RecorderButNoProtocolId_DoesNotRecord()
    {
        var modelId = Guid.NewGuid();
        var model = AiConnectionTestFactory.CreateChatModel("deep-model", modelId);
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [model]);
        this.SetTenantEntries(ChatMapping("deep", connection.Id, modelId, ReviewReasoningEffort.None, AiProtocolMode.Auto));
        this._connections.GetByIdAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);
        this._runtimeFactory.CreateChatRuntime(connection, model, Arg.Any<AiPurposeBindingDto>(), Arg.Any<string?>())
            .Returns(Substitute.For<IResolvedAiChatRuntime>());
        var recorder = Substitute.For<IProtocolRecorder>();

        await this.Sut().ResolveChatRuntimeAsync(ClientId, "deep", recorder, protocolId: null);

        await recorder.DidNotReceive().RecordLogicalModelResolutionEventAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // A mapping whose connection exists but no longer holds the configured model fails clearly.
    [Fact]
    public async Task ResolveChat_MissingModelOnConnection_Throws()
    {
        var missingModelId = Guid.NewGuid();
        // The connection has some other model, not the one the mapping points at.
        var otherModel = AiConnectionTestFactory.CreateChatModel("other-model");
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [otherModel]);
        this.SetTenantEntries(ChatMapping("deep", connection.Id, missingModelId, ReviewReasoningEffort.None, AiProtocolMode.Auto));
        this._connections.GetByIdAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => this.Sut().ResolveChatRuntimeAsync(ClientId, "deep"));
        Assert.Contains(missingModelId.ToString(), ex.Message, StringComparison.Ordinal);
    }

    private static LogicalModelDto ChatMapping(string name, Guid connectionId, Guid modelId, ReviewReasoningEffort effort, AiProtocolMode protocol)
    {
        return new LogicalModelDto(Guid.NewGuid(), name, AiOperationKind.Chat, connectionId, modelId, effort, protocol);
    }

    private void SetOverrides(params LogicalModelDto[] entries)
    {
        this._catalog.GetClientOverridesAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<LogicalModelDto>)entries);
    }

    private void SetTenantEntries(params LogicalModelDto[] entries)
    {
        this._catalog.GetTenantEntriesForClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<LogicalModelDto>)entries);
    }
}
