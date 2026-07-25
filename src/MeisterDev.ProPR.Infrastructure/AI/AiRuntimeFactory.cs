// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Runtime;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.Ai.Providers.Drivers;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Default <see cref="IAiRuntimeFactory" />. Builds runtimes via the provider driver registry and, when a budget
///     scope accessor is available, wraps the chat client / embedding generator so every model call is metered and
///     gated against the active review job's USD hard cap — mirroring <see cref="AiRuntimeResolver" />'s construction.
/// </summary>
public sealed class AiRuntimeFactory(
    IAiProviderDriverRegistry providerDriverRegistry,
    IBudgetScopeAccessor? budgetScopeAccessor = null) : IAiRuntimeFactory
{
    public IResolvedAiChatRuntime CreateChatRuntime(
        AiConnectionDto connection,
        AiConfiguredModelDto model,
        AiPurposeBindingDto binding,
        string? logicalModelName = null)
    {
        var driver = providerDriverRegistry.GetRequired(connection.ProviderKind);
        var client = driver.CreateChatClient(connection.ToProviderEndpoint(), model.ToProviderModel(), binding.ProtocolMode);
        var capabilities = driver.GetChatRuntimeCapabilities(
            connection.ToProviderEndpoint(),
            model.ToProviderModel(),
            binding.ProtocolMode).ToReviewCapabilities();
        return new ResolvedAiChatRuntime(connection, model, binding, this.WrapChatClient(client, connection, model), capabilities)
        {
            LogicalModelName = logicalModelName,
        };
    }

    public IResolvedAiEmbeddingRuntime CreateEmbeddingRuntime(
        AiConnectionDto connection,
        AiConfiguredModelDto model,
        AiPurposeBindingDto binding,
        string tokenizerName,
        int dimensions,
        string? logicalModelName = null)
    {
        var driver = providerDriverRegistry.GetRequired(connection.ProviderKind);
        var generator = driver.CreateEmbeddingGenerator(connection.ToProviderEndpoint(), model.ToProviderModel(), binding.ProtocolMode, dimensions);
        return new ResolvedAiEmbeddingRuntime(
            connection,
            model,
            binding,
            this.WrapEmbeddingGenerator(generator, model),
            tokenizerName,
            dimensions)
        {
            LogicalModelName = logicalModelName,
        };
    }

    private static ModelPricing ToPricing(AiConfiguredModelDto model)
    {
        return new ModelPricing(model.InputCostPer1MUsd, model.OutputCostPer1MUsd, model.CachedInputCostPer1MUsd);
    }

    // The provider pipeline fixes the order of the per-call stages; the budget stage is contributed from here
    // because the library has no notion of cost. A host with no budget scope simply contributes no stage.
    private IChatClient WrapChatClient(IChatClient client, AiConnectionDto connection, AiConfiguredModelDto model)
    {
        if (budgetScopeAccessor is null)
        {
            return client;
        }

        var pipeline = new ProviderRuntimePipeline([new BudgetEnforcingChatClientDecorator(budgetScopeAccessor, _ => ToPricing(model))]);

        return pipeline.Compose(client, connection.ToProviderEndpoint(), model.ToProviderModel());
    }

    private IEmbeddingGenerator<string, Embedding<float>> WrapEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        AiConfiguredModelDto model)
    {
        return budgetScopeAccessor is null
            ? generator
            : new BudgetEnforcingEmbeddingGenerator(generator, budgetScopeAccessor, ToPricing(model));
    }
}
