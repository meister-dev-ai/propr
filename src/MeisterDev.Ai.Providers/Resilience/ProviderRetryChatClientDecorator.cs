// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.Ai.Providers.Resilience;

/// <summary>
///     Fills the pipeline's <see cref="ProviderRuntimeStage.Retry" /> stage. It is the outermost stage, so every
///     attempt passes through the stages below it: a metering stage counts each attempt once, and a normalizing
///     stage shapes retried requests too.
/// </summary>
/// <param name="driver">The driver whose classification decides whether a failure is worth repeating.</param>
/// <param name="policy">How many attempts to make and how long to wait between them.</param>
/// <param name="profileLabel">
///     Operator-visible name of the connection profile, so a failure message names the profile to go and fix.
/// </param>
/// <param name="timeProvider">Clock used for backoff; <see langword="null" /> uses the system clock.</param>
/// <param name="logger">Optional logger for retry attempts.</param>
public sealed class ProviderRetryChatClientDecorator(
    IAiProviderDriver driver,
    ProviderRetryPolicy policy,
    string? profileLabel = null,
    TimeProvider? timeProvider = null,
    ILogger? logger = null) : IProviderChatClientDecorator
{
    /// <inheritdoc />
    public ProviderRuntimeStage Stage => ProviderRuntimeStage.Retry;

    /// <inheritdoc />
    public IChatClient Decorate(IChatClient inner, ProviderEndpoint endpoint, ProviderModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);

        return new ProviderRetryChatClient(
            inner,
            policy,
            driver.ClassifyRuntimeFailure,
            new ProviderCallTarget(endpoint.ProviderKind, model.RemoteModelId, profileLabel),
            timeProvider,
            logger);
    }
}
