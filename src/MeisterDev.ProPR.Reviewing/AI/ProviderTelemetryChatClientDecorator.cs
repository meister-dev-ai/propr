// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Resilience;
using MeisterDev.Ai.Providers.Runtime;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Contributes per-call spans, logs and measurements as the pipeline's observability stage. The provider
///     library has no telemetry opinion of its own, so this comes from the product side and occupies the stage the
///     library reserves for it.
/// </summary>
/// <param name="metrics">Instruments the measurements are recorded on.</param>
/// <param name="pricingFor">Pricing for the model being called, so cost can be measured alongside tokens.</param>
/// <param name="profileLabel">Operator-visible name of the connection profile.</param>
/// <param name="clientId">Owning client, tagged on spans only; it is deliberately absent from metric tags.</param>
/// <param name="logicalModelName">The logical-model role the call was resolved under, when there was one.</param>
/// <param name="logger">Optional logger for the per-call log line.</param>
/// <param name="classifyFailure">
///     The driver's classification of a failure, so a throttle the retry stage absorbs is recorded as throttling
///     rather than as a provider error. The raw exception this stage sees cannot be read that way on its own.
/// </param>
public sealed class ProviderTelemetryChatClientDecorator(
    AiProviderMetrics metrics,
    Func<ProviderModelDescriptor, ModelPricing> pricingFor,
    string? profileLabel = null,
    Guid? clientId = null,
    string? logicalModelName = null,
    ILogger? logger = null,
    Func<Exception, ProviderFailureVerdict>? classifyFailure = null) : IProviderChatClientDecorator
{
    /// <inheritdoc />
    public ProviderRuntimeStage Stage => ProviderRuntimeStage.Observability;

    /// <inheritdoc />
    public IChatClient Decorate(IChatClient inner, ProviderEndpoint endpoint, ProviderModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);

        return new ProviderTelemetryChatClient(
            inner,
            new ProviderCallTarget(endpoint.ProviderKind, model.RemoteModelId, profileLabel),
            pricingFor(model),
            metrics,
            logger,
            clientId,
            logicalModelName,
            classifyFailure);
    }
}
