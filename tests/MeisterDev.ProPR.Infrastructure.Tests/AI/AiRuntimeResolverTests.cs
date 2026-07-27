// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     The resolver decides WHICH connection, model, and protocol a purpose maps to; building the runtime is the
///     factory's single responsibility. These tests therefore assert the selection and the hand-off, not the
///     driver interactions that now happen one layer down.
/// </summary>
public sealed class AiRuntimeResolverTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000042");

    private readonly IAiConnectionRepository _repository = Substitute.For<IAiConnectionRepository>();
    private readonly IAiRuntimeFactory _runtimeFactory = Substitute.For<IAiRuntimeFactory>();

    [Fact]
    public async Task ResolveChatRuntimeAsync_UsesPurposeBindingWithoutReadingGenericActiveProfile()
    {
        var (connection, model, binding) = Chat("gpt-4.1");
        this._repository.GetActiveBindingForPurposeAsync(ClientId, AiPurpose.ReviewDefault, Arg.Any<CancellationToken>())
            .Returns(new AiResolvedPurposeBindingDto(connection, model, binding));
        var expected = Substitute.For<IResolvedAiChatRuntime>();
        this._runtimeFactory.CreateChatRuntime(connection, model, binding, Arg.Any<string?>()).Returns(expected);

        var runtime = await this.Sut().ResolveChatRuntimeAsync(ClientId, AiPurpose.ReviewDefault, CancellationToken.None);

        Assert.Same(expected, runtime);
        this._runtimeFactory.Received(1).CreateChatRuntime(connection, model, binding, Arg.Any<string?>());
        await this._repository.DidNotReceive().GetActiveForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveChatRuntimeForModelAsync_BuildsRuntimeFromModelBinding()
    {
        var (connection, model, binding) = Chat("gpt-5.3-codex");
        this._repository.GetModelBindingAsync(ClientId, model.Id, Arg.Any<CancellationToken>())
            .Returns(new AiResolvedPurposeBindingDto(connection, model, binding));
        var expected = Substitute.For<IResolvedAiChatRuntime>();
        this._runtimeFactory.CreateChatRuntime(connection, model, binding, Arg.Any<string?>()).Returns(expected);

        var runtime = await this.Sut().ResolveChatRuntimeForModelAsync(ClientId, model.Id, CancellationToken.None);

        Assert.Same(expected, runtime);
    }

    [Fact]
    public async Task ResolveChatRuntimeAsync_NoBinding_ThrowsWithoutFallingBackToActiveProfile()
    {
        this._repository.GetActiveBindingForPurposeAsync(ClientId, AiPurpose.ReviewDefault, Arg.Any<CancellationToken>())
            .Returns((AiResolvedPurposeBindingDto?)null);

        // The distinct type is the contract callers act on: an optional purpose degrades to non-AI behaviour on this
        // exception alone, so a fault must not arrive wearing the same type and be mistaken for an unmapped purpose.
        var exception = await Assert.ThrowsAsync<AiPurposeBindingNotConfiguredException>(() => this.Sut().ResolveChatRuntimeAsync(
            ClientId, AiPurpose.ReviewDefault, CancellationToken.None));

        Assert.Equal(AiPurpose.ReviewDefault, exception.Purpose);
        Assert.Contains("No active AI binding is configured", exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
        await this._repository.DidNotReceive().GetActiveForClientAsync(ClientId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveChatRuntimeAsync_ModelWithoutChatSupport_DoesNotBuildARuntime()
    {
        var model = AiConnectionTestFactory.CreateEmbeddingModel("text-embedding-3-small", 1536);
        var binding = AiConnectionTestFactory.CreateBinding(AiPurpose.ReviewDefault, model);
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [model], [binding]);
        this._repository.GetActiveBindingForPurposeAsync(ClientId, AiPurpose.ReviewDefault, Arg.Any<CancellationToken>())
            .Returns(new AiResolvedPurposeBindingDto(connection, model, binding));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => this.Sut().ResolveChatRuntimeAsync(
            ClientId, AiPurpose.ReviewDefault, CancellationToken.None));

        Assert.Contains("does not support chat", exception.Message, StringComparison.Ordinal);
        this._runtimeFactory.DidNotReceiveWithAnyArgs().CreateChatRuntime(null!, null!, null!);
    }

    // A purpose mapped to a logical model resolves through the catalog, bypassing the purpose-binding path.
    [Fact]
    public async Task ResolveChatRuntimeAsync_MappedPurpose_ResolvesViaLogicalModel()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        var logicalResolver = Substitute.For<ILogicalModelResolver>();
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        catalog.GetPurposeRoleAsync(ClientId, AiPurpose.ReviewTriage, Arg.Any<CancellationToken>()).Returns("triage-role");
        logicalResolver
            .ResolveChatRuntimeAsync(ClientId, "triage-role", Arg.Any<IProtocolRecorder?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedLogicalModelChatRuntime(runtime, "triage-role", LogicalModelLayer.ClientOverride, ReviewReasoningEffort.Medium));

        var result = await this.Sut(logicalResolver, catalog).ResolveChatRuntimeAsync(ClientId, AiPurpose.ReviewTriage, CancellationToken.None);

        Assert.Same(runtime, result);
        await this._repository.DidNotReceive()
            .GetActiveBindingForPurposeAsync(Arg.Any<Guid>(), Arg.Any<AiPurpose>(), Arg.Any<CancellationToken>());
    }

    // An embedding purpose mapped to a logical model resolves through the catalog.
    [Fact]
    public async Task ResolveEmbeddingRuntimeAsync_MappedPurpose_ResolvesViaLogicalModel()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        var logicalResolver = Substitute.For<ILogicalModelResolver>();
        var runtime = Substitute.For<IResolvedAiEmbeddingRuntime>();
        catalog.GetPurposeRoleAsync(ClientId, AiPurpose.EmbeddingDefault, Arg.Any<CancellationToken>()).Returns("embed-role");
        logicalResolver
            .ResolveEmbeddingRuntimeAsync(
                ClientId, "embed-role", Arg.Any<int?>(), Arg.Any<IProtocolRecorder?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedLogicalModelEmbeddingRuntime(runtime, "embed-role", LogicalModelLayer.TenantCatalog));

        var result = await this.Sut(logicalResolver, catalog)
            .ResolveEmbeddingRuntimeAsync(ClientId, AiPurpose.EmbeddingDefault, 1536, CancellationToken.None);

        Assert.Same(runtime, result);
        await this._repository.DidNotReceive()
            .GetActiveBindingForPurposeAsync(Arg.Any<Guid>(), Arg.Any<AiPurpose>(), Arg.Any<CancellationToken>());
    }

    // With the logical-model layer available but the purpose unmapped, resolution uses the purpose-binding path
    // unchanged.
    [Fact]
    public async Task ResolveChatRuntimeAsync_UnmappedPurposeWithLayerPresent_UsesBindingPath()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        var logicalResolver = Substitute.For<ILogicalModelResolver>();
        catalog.GetPurposeRoleAsync(ClientId, AiPurpose.ReviewDefault, Arg.Any<CancellationToken>()).Returns((string?)null);
        var (connection, model, binding) = Chat("gpt-4.1");
        this._repository.GetActiveBindingForPurposeAsync(ClientId, AiPurpose.ReviewDefault, Arg.Any<CancellationToken>())
            .Returns(new AiResolvedPurposeBindingDto(connection, model, binding));
        var expected = Substitute.For<IResolvedAiChatRuntime>();
        this._runtimeFactory.CreateChatRuntime(connection, model, binding, Arg.Any<string?>()).Returns(expected);

        var runtime = await this.Sut(logicalResolver, catalog)
            .ResolveChatRuntimeAsync(ClientId, AiPurpose.ReviewDefault, CancellationToken.None);

        Assert.Same(expected, runtime);
        await logicalResolver.DidNotReceive().ResolveChatRuntimeAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProtocolRecorder?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    private static (AiConnectionDto Connection, AiConfiguredModelDto Model, AiPurposeBindingDto Binding) Chat(string remoteModelId)
    {
        var model = AiConnectionTestFactory.CreateChatModel(remoteModelId);
        var binding = AiConnectionTestFactory.CreateBinding(AiPurpose.ReviewDefault, model);
        return (AiConnectionTestFactory.CreateConnection(ClientId, [model], [binding]), model, binding);
    }

    // Defence in depth for the allow-list: the legacy purpose-binding path consults no connection scope guard, so
    // a profile bound before the policy changed would otherwise still run on a provider the tenant has forbidden.
    [Fact]
    public async Task ResolveChatRuntimeAsync_RefusesAProviderTheTenantNoLongerPermits()
    {
        var (connection, model, binding) = Chat("gpt-4.1");
        this._repository.GetActiveBindingForPurposeAsync(ClientId, AiPurpose.ReviewDefault, Arg.Any<CancellationToken>())
            .Returns(new AiResolvedPurposeBindingDto(connection, model, binding));
        var policies = Substitute.For<ITenantProviderPolicyProvider>();
        policies.GetForClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(new TenantProviderPolicy([AiProviderKind.OpenAiCompatible]));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => this.Sut(providerPolicies: policies)
            .ResolveChatRuntimeAsync(ClientId, AiPurpose.ReviewDefault, CancellationToken.None));

        Assert.Contains("permitted provider list", exception.Message, StringComparison.Ordinal);
        // Refused before the runtime is built, so no credential is used.
        this._runtimeFactory.DidNotReceive().CreateChatRuntime(
            Arg.Any<AiConnectionDto>(),
            Arg.Any<AiConfiguredModelDto>(),
            Arg.Any<AiPurposeBindingDto>(),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task ResolveChatRuntimeAsync_PermittedProviderStillResolves()
    {
        var (connection, model, binding) = Chat("gpt-4.1");
        this._repository.GetActiveBindingForPurposeAsync(ClientId, AiPurpose.ReviewDefault, Arg.Any<CancellationToken>())
            .Returns(new AiResolvedPurposeBindingDto(connection, model, binding));
        var expected = Substitute.For<IResolvedAiChatRuntime>();
        this._runtimeFactory.CreateChatRuntime(connection, model, binding, Arg.Any<string?>()).Returns(expected);
        var policies = Substitute.For<ITenantProviderPolicyProvider>();
        policies.GetForClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(new TenantProviderPolicy([connection.ProviderKind]));

        var runtime = await this.Sut(providerPolicies: policies)
            .ResolveChatRuntimeAsync(ClientId, AiPurpose.ReviewDefault, CancellationToken.None);

        Assert.Same(expected, runtime);
    }

    private AiRuntimeResolver Sut(
        ILogicalModelResolver? logicalModelResolver = null,
        ILogicalModelCatalogRepository? logicalModelCatalog = null,
        ITenantProviderPolicyProvider? providerPolicies = null)
    {
        return new AiRuntimeResolver(
            this._repository,
            this._runtimeFactory,
            logicalModelResolver,
            logicalModelCatalog,
            providerPolicies);
    }
}
