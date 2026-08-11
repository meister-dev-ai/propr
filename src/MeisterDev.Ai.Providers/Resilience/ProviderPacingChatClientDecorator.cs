// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.Ai.Providers.Resilience;

/// <summary>
///     Fills the pipeline's <see cref="ProviderRuntimeStage.Pacing" /> stage: inside retry, so every attempt is a
///     real request against the quota, and outside observability, so a wait on the gate is not recorded as
///     provider latency.
/// </summary>
/// <param name="driver">The driver whose classification says whether a failure was a throttle.</param>
/// <param name="gate">The process-wide gate the connection's calls wait on. Shared, so it must outlive one call.</param>
/// <param name="connectionKey">
///     Identifies the connection whose quota is shared by every call routed through it, usually its id.
/// </param>
/// <param name="policy">The retry policy, read for the ceiling a stated delay is held to.</param>
/// <param name="logger">Optional logger for the moment a connection is found throttled.</param>
public sealed class ProviderPacingChatClientDecorator(
    IAiProviderDriver driver,
    ProviderThrottleGate gate,
    string connectionKey,
    ProviderRetryPolicy policy,
    ILogger? logger = null) : IProviderChatClientDecorator
{
    /// <inheritdoc />
    public ProviderRuntimeStage Stage => ProviderRuntimeStage.Pacing;

    /// <inheritdoc />
    public IChatClient Decorate(IChatClient inner, ProviderEndpoint endpoint, ProviderModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(policy);

        return new ProviderPacingChatClient(
            inner,
            gate,
            connectionKey,
            driver.ClassifyRuntimeFailure,
            policy.MaxDelay,
            logger);
    }
}
