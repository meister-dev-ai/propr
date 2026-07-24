// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI.Providers;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.AI;

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
        var client = driver.CreateChatClient(connection, model, binding);
        var capabilities = driver.GetChatRuntimeCapabilities(connection, model, binding);
        return new ResolvedAiChatRuntime(connection, model, binding, this.WrapChatClient(client, model), capabilities)
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
        var generator = driver.CreateEmbeddingGenerator(connection, model, binding, dimensions);
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

    private IChatClient WrapChatClient(IChatClient client, AiConfiguredModelDto model)
    {
        return budgetScopeAccessor is null
            ? client
            : new BudgetEnforcingChatClient(client, budgetScopeAccessor, ToPricing(model));
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
