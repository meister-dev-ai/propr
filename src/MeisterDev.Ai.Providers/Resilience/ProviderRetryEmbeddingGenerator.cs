// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.Ai.Providers.Resilience;

/// <summary>
///     The embedding counterpart to <see cref="ProviderRetryChatClient" />, on the same classification and the
///     same policy. Embedding calls are throttled by the same provider quotas as chat calls, and an indexing pass
///     that abandons a batch on one 429 is no more acceptable than a review that does.
/// </summary>
public sealed partial class ProviderRetryEmbeddingGenerator : DelegatingEmbeddingGenerator<string, Embedding<float>>
{
    private readonly ProviderRetryPolicy _policy;
    private readonly Func<Exception, ProviderFailureVerdict> _classify;
    private readonly ProviderCallTarget _target;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;

    /// <summary>Initializes a new instance of the <see cref="ProviderRetryEmbeddingGenerator" /> class.</summary>
    /// <param name="innerGenerator">The generator whose calls are retried.</param>
    /// <param name="policy">How many attempts to make and how long to wait between them.</param>
    /// <param name="classify">The driver's classification of a failure; usually <c>driver.ClassifyRuntimeFailure</c>.</param>
    /// <param name="target">The profile, provider and model being called, for logs and failure messages.</param>
    /// <param name="timeProvider">Clock used for backoff; <see langword="null" /> uses the system clock.</param>
    /// <param name="logger">Optional logger for retry attempts.</param>
    public ProviderRetryEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> innerGenerator,
        ProviderRetryPolicy policy,
        Func<Exception, ProviderFailureVerdict> classify,
        ProviderCallTarget target,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
        : base(innerGenerator)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(classify);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.MaxAttempts, 1);

        this._policy = policy;
        this._classify = classify;
        this._target = target;
        this._timeProvider = timeProvider ?? TimeProvider.System;
        this._logger = logger;
    }

    /// <inheritdoc />
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var batch = values as IList<string> ?? values.ToList();

        for (var attempt = 1;; attempt++)
        {
            try
            {
                return await base.GenerateAsync(batch, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (ProviderRetryMechanics.IsClassifiable(exception, cancellationToken))
            {
                var verdict = this._classify(exception);
                if (!verdict.IsTransient || attempt >= this._policy.MaxAttempts)
                {
                    throw new ProviderCallFailedException(
                        this._target,
                        verdict,
                        attempt,
                        DriverFailureMapper.ActionHintFor(verdict.HttpStatus),
                        exception);
                }

                var delay = ProviderRetryMechanics.Delay(this._policy, verdict, attempt);
                if (this._logger is not null)
                {
                    LogRetrying(this._logger, this._target.Describe(), attempt, this._policy.MaxAttempts, delay.TotalSeconds, verdict.Reason);
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, this._timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "AI embedding call to {Target} failed transiently ({Reason}); attempt {Attempt} of {MaxAttempts}, retrying in {DelaySeconds:0.##}s.")]
    private static partial void LogRetrying(
        ILogger logger,
        string target,
        int attempt,
        int maxAttempts,
        double delaySeconds,
        string reason);
}
