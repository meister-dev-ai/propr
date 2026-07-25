// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Runtime.CompilerServices;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.Ai.Providers.Resilience;

/// <summary>
///     Repeats a provider call while the driver says the failure was transient, then reports the failure in terms
///     an operator can act on. One implementation serves every provider, because it acts on the classification a
///     driver returns rather than on any SDK's exception types.
/// </summary>
/// <remarks>
///     <para>
///         Cancellation is never retried and never rewritten. That includes product-level refusals that ride the
///         cancellation channel — a budget hard cap, for instance — which must reach their handler as the type
///         they were thrown as. The single exception is the HTTP client's own timeout, which arrives as a
///         cancellation carrying a <see cref="TimeoutException" /> and is a genuine transport failure.
///     </para>
///     <para>
///         Streaming retries only until the first update is handed to the caller. After that the caller has
///         already seen part of an answer, and starting a second attempt would either duplicate or contradict it.
///     </para>
/// </remarks>
public sealed partial class ProviderRetryChatClient : DelegatingChatClient
{
    private readonly ProviderRetryPolicy _policy;
    private readonly Func<Exception, ProviderFailureVerdict> _classify;
    private readonly ProviderCallTarget _target;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;

    /// <summary>Initializes a new instance of the <see cref="ProviderRetryChatClient" /> class.</summary>
    /// <param name="innerClient">The client whose calls are retried.</param>
    /// <param name="policy">How many attempts to make and how long to wait between them.</param>
    /// <param name="classify">The driver's classification of a failure; usually <c>driver.ClassifyRuntimeFailure</c>.</param>
    /// <param name="target">The profile, provider and model being called, for logs and failure messages.</param>
    /// <param name="timeProvider">Clock used for backoff; <see langword="null" /> uses the system clock.</param>
    /// <param name="logger">Optional logger for retry attempts.</param>
    public ProviderRetryChatClient(
        IChatClient innerClient,
        ProviderRetryPolicy policy,
        Func<Exception, ProviderFailureVerdict> classify,
        ProviderCallTarget target,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
        : base(innerClient)
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
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // Materialise once: a retried attempt must send the same conversation, and an enumerable built lazily
        // (a Select over a mutating list, say) is not guaranteed to produce it twice.
        var conversation = messages as IList<ChatMessage> ?? messages.ToList();

        for (var attempt = 1;; attempt++)
        {
            try
            {
                return await base.GetResponseAsync(conversation, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (ProviderRetryMechanics.IsClassifiable(exception, cancellationToken))
            {
                var verdict = this._classify(exception);
                if (!verdict.IsTransient || attempt >= this._policy.MaxAttempts)
                {
                    throw this.Fail(exception, verdict, attempt);
                }

                await this.WaitBeforeRetryAsync(verdict, attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var conversation = messages as IList<ChatMessage> ?? messages.ToList();

        for (var attempt = 1;; attempt++)
        {
            var retrying = false;
            var enumerator = base.GetStreamingResponseAsync(conversation, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            try
            {
                var delivered = false;
                while (true)
                {
                    ChatResponseUpdate update;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        update = enumerator.Current;
                    }
                    catch (Exception exception) when (ProviderRetryMechanics.IsClassifiable(exception, cancellationToken))
                    {
                        var verdict = this._classify(exception);

                        // Once part of an answer has been handed over, a second attempt would either duplicate or
                        // contradict it, so the failure is reported rather than retried.
                        if (delivered || !verdict.IsTransient || attempt >= this._policy.MaxAttempts)
                        {
                            throw this.Fail(exception, verdict, attempt);
                        }

                        await this.WaitBeforeRetryAsync(verdict, attempt, cancellationToken).ConfigureAwait(false);
                        retrying = true;
                        break;
                    }

                    delivered = true;
                    yield return update;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (!retrying)
            {
                yield break;
            }
        }
    }

    private ProviderCallFailedException Fail(Exception exception, ProviderFailureVerdict verdict, int attempts)
    {
        return new ProviderCallFailedException(
            this._target,
            verdict,
            attempts,
            DriverFailureMapper.ActionHintFor(verdict.HttpStatus),
            exception);
    }

    private async Task WaitBeforeRetryAsync(ProviderFailureVerdict verdict, int attempt, CancellationToken cancellationToken)
    {
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

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "AI provider call to {Target} failed transiently ({Reason}); attempt {Attempt} of {MaxAttempts}, retrying in {DelaySeconds:0.##}s.")]
    private static partial void LogRetrying(
        ILogger logger,
        string target,
        int attempt,
        int maxAttempts,
        double delaySeconds,
        string reason);
}
