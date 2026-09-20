// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Resilience;
using MeisterDev.Ai.Providers.Runtime;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.Ai.Providers.Drivers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Default <see cref="IAiRuntimeFactory" />. Builds runtimes via the provider driver registry and wraps the
///     chat client in the provider pipeline's fixed stage order: transient failures are retried for every provider,
///     every call is recorded with the provider and model it went to, and — when a budget scope accessor is
///     available — every call is metered and gated against the active review job's USD hard cap.
/// </summary>
public sealed class AiRuntimeFactory(
    IAiProviderDriverRegistry providerDriverRegistry,
    IBudgetScopeAccessor? budgetScopeAccessor = null,
    IOptions<AiReviewOptions>? aiOptions = null,
    ILogger<AiRuntimeFactory>? logger = null,
    TimeProvider? timeProvider = null,
    AiProviderMetrics? metrics = null,
    ProviderThrottleGate? throttleGate = null,
    IProviderConnectionContextFactory? hostContexts = null) : IAiRuntimeFactory
{
    public IResolvedAiChatRuntime CreateChatRuntime(
        AiConnectionDto connection,
        AiConfiguredModelDto model,
        AiPurposeBindingDto binding,
        string? logicalModelName = null)
    {
        this.RefuseUnavailableConnection(connection);
        var driver = providerDriverRegistry.GetRequired(connection.ProviderKind);

        // Built once and put on every endpoint this runtime hands the driver, so the client the driver
        // constructs can keep it: this factory's scope is gone long before the review's last pass, and the
        // credential the client started with expires before then.
        var endpoint = connection.ToProviderEndpoint(hostContexts?.ForConnection(connection));
        var client = driver.CreateChatClient(endpoint, model.ToProviderModel(), binding.ProtocolMode);
        var capabilities = driver.GetChatRuntimeCapabilities(
            endpoint,
            model.ToProviderModel(),
            binding.ProtocolMode).ToReviewCapabilities();
        var wrapped = this.WrapChatClient(client, driver, connection, model, logicalModelName);
        return new ResolvedAiChatRuntime(connection, model, binding, wrapped, capabilities)
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
        this.RefuseUnavailableConnection(connection);
        var driver = providerDriverRegistry.GetRequired(connection.ProviderKind);
        var generator = driver.CreateEmbeddingGenerator(
            connection.ToProviderEndpoint(hostContexts?.ForConnection(connection)),
            model.ToProviderModel(),
            binding.ProtocolMode,
            dimensions);
        return new ResolvedAiEmbeddingRuntime(
            connection,
            model,
            binding,
            this.WrapEmbeddingGenerator(generator, driver, connection, model, dimensions),
            tokenizerName,
            dimensions)
        {
            LogicalModelName = logicalModelName,
        };
    }

    /// <summary>
    ///     Refuses a profile this host cannot serve, before the driver is resolved and before the credential on
    ///     the profile is handed to one.
    /// </summary>
    /// <param name="connection">The profile a review resolved to.</param>
    /// <exception cref="AiConnectionUnavailableException">The profile cannot be served as it is stored.</exception>
    /// <remarks>
    ///     Building the runtime is where every resolution path meets, so the refusal is stated once here. A
    ///     profile carries the identity it is stored against whether or not a family claims it, so routing on
    ///     that value without asking the registry first would reach for a driver that is not loaded.
    /// </remarks>
    private void RefuseUnavailableConnection(AiConnectionDto connection)
    {
        if (connection.Availability.State == AiConnectionAvailabilityState.Unavailable)
        {
            throw new AiConnectionUnavailableException(connection, connection.Availability);
        }

        // The registry is what says which families this build can call, and a profile naming one that is not
        // loaded is reported with the identity it holds so an operator can see which add-in is missing.
        if (!providerDriverRegistry.IsRegistered(connection.ProviderKind))
        {
            throw new AiConnectionUnavailableException(
                connection,
                new AiConnectionAvailabilityDto(
                    AiConnectionAvailabilityState.Unavailable,
                    AiConnectionUnavailableReason.ProviderFamilyAbsent,
                    connection.ProviderKind,
                    []));
        }
    }

    private static ModelPricing ToPricing(AiConfiguredModelDto model)
    {
        return new ModelPricing(
            model.InputCostPer1MUsd,
            model.OutputCostPer1MUsd,
            model.CachedInputCostPer1MUsd,
            model.CacheWriteCostPer1MUsd);
    }

    // Retries count attempts, while the option counts retries on top of the first call, so the first attempt is
    // added back here. The option was written for 429s specifically; it now governs every transient class,
    // because "how many times may a call be repeated" is one decision regardless of which failure provoked it.
    private ProviderRetryPolicy RetryPolicy()
    {
        var options = aiOptions?.Value;
        if (options is null)
        {
            return ProviderRetryPolicy.Default;
        }

        return new ProviderRetryPolicy
        {
            MaxAttempts = options.MaxRateLimitRetries + 1,
            BaseDelay = TimeSpan.FromSeconds(2),
            MaxDelay = TimeSpan.FromSeconds(options.MaxBackoffSeconds),
        };
    }

    // The provider pipeline fixes the order of the per-call stages. Retry is contributed for every provider
    // because a review that dies on one throttled call is not shippable behaviour; the budget stage is
    // contributed from here because the library has no notion of cost, and a host with no budget scope
    // contributes no stage at all.
    private IChatClient WrapChatClient(
        IChatClient client,
        IAiProviderDriver driver,
        AiConnectionDto connection,
        AiConfiguredModelDto model,
        string? logicalModelName)
    {
        var policy = this.RetryPolicy();
        var decorators = new List<IProviderChatClientDecorator>
        {
            new ProviderRetryChatClientDecorator(driver, policy, connection.DisplayName, timeProvider, logger),
            new ReasoningModelSamplingDecorator(driver),

            // Innermost, so the budget and telemetry stages outside it price and record the counters the family
            // mapped. Neither of those stages holds a driver, and neither reads a vendor's field names.
            new NormalizedUsageChatClientDecorator(driver),
        };

        // The gate is handed in rather than built here because it has to be shared: this factory is scoped, and
        // a gate per scope would tell each caller only what it had already found out for itself. Keyed by the
        // connection, since that is what the provider's quota belongs to.
        if (throttleGate is not null)
        {
            decorators.Add(
                new ProviderPacingChatClientDecorator(
                    driver,
                    throttleGate,
                    connection.Id.ToString("D"),
                    policy,
                    logger));
        }

        if (metrics is not null)
        {
            decorators.Add(
                new ProviderTelemetryChatClientDecorator(
                    metrics,
                    _ => ToPricing(model),
                    connection.DisplayName,
                    connection.ClientId,
                    logicalModelName,
                    logger,
                    driver.ClassifyRuntimeFailure));
        }

        if (budgetScopeAccessor is not null)
        {
            decorators.Add(new BudgetEnforcingChatClientDecorator(budgetScopeAccessor, _ => ToPricing(model)));
        }

        return new ProviderRuntimePipeline(decorators)
            .Compose(client, connection.ToProviderEndpoint(), model.ToProviderModel());
    }

    // The embedding path has no decorator pipeline to order, but it faces the same provider quotas, so it gets
    // the same retry behaviour — wrapped innermost-first so metering counts one attempt at a time.
    private IEmbeddingGenerator<string, Embedding<float>> WrapEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        IAiProviderDriver driver,
        AiConnectionDto connection,
        AiConfiguredModelDto model,
        int dimensions)
    {
        var metered = budgetScopeAccessor is null
            ? generator
            : new BudgetEnforcingEmbeddingGenerator(generator, budgetScopeAccessor, ToPricing(model));

        var retrying = new ProviderRetryEmbeddingGenerator(
            metered,
            this.RetryPolicy(),
            driver.ClassifyRuntimeFailure,
            new ProviderCallTarget(connection.ProviderKind, model.RemoteModelId, connection.DisplayName),
            timeProvider,
            logger);

        // Outermost, because a width the deployment did not answer with is a configuration fault and no attempt
        // changes it. A caller that states no width has nothing to check against, so the stage is left out.
        return dimensions > 0
            ? new DeclaredWidthEmbeddingGenerator(retrying, model.RemoteModelId, dimensions)
            : retrying;
    }
}
