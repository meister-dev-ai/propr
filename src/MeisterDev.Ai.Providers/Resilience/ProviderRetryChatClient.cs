// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
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
    /// <summary>
    ///     Why the code after either attempt loop cannot run: the loop is bounded by the policy's attempt count, and
    ///     the final attempt either produces a result or is refused a retry and throws.
    /// </summary>
    private const string LoopEndedWithoutOutcome =
        "The attempt loop ended without a result or a failure, which the attempt bound makes impossible.";

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

        for (var attempt = 1; attempt <= this._policy.MaxAttempts; attempt++)
        {
            try
            {
                return await base.GetResponseAsync(conversation, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (ProviderRetryMechanics.IsClassifiable(exception, cancellationToken))
            {
                await this.ThrowOrWaitAsync(exception, attempt, answerAlreadyStarted: false, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new UnreachableException(LoopEndedWithoutOutcome);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var conversation = RequireConversation(messages);

        for (var attempt = 1; attempt <= this._policy.MaxAttempts; attempt++)
        {
            var stream = base.GetStreamingResponseAsync(conversation, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            var delivered = false;
            bool retry;

            try
            {
                var step = await this.ReadNextAsync(stream, delivered, attempt, cancellationToken).ConfigureAwait(false);
                while (step.Update is not null)
                {
                    delivered = true;
                    yield return step.Update;
                    step = await this.ReadNextAsync(stream, delivered, attempt, cancellationToken).ConfigureAwait(false);
                }

                retry = step.Retry;
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            if (!retry)
            {
                yield break;
            }
        }

        throw new UnreachableException(LoopEndedWithoutOutcome);
    }

    /// <summary>
    ///     Validates the streaming caller's message sequence and materialises it into a list the retry loop can
    ///     walk through on each attempt. Pulling this out keeps <see cref="GetStreamingResponseAsync" /> a pure
    ///     iterator over attempts and lets the failure-handling path stay inside the loop.
    /// </summary>
    private static IList<ChatMessage> RequireConversation(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages as IList<ChatMessage> ?? messages.ToList();
    }

    /// <summary>
    ///     Reads the next update of a streaming attempt, absorbing a transient failure into a retry signal so the
    ///     caller's loop stays a plain walk over the stream.
    /// </summary>
    /// <param name="stream">The attempt being read.</param>
    /// <param name="delivered">Whether an update from this attempt has already reached the caller.</param>
    /// <param name="attempt">The attempt number, counted from one.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The update read, or an empty step saying whether another attempt should be made.</returns>
    private async Task<StreamStep> ReadNextAsync(
        IAsyncEnumerator<ChatResponseUpdate> stream,
        bool delivered,
        int attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            return await stream.MoveNextAsync().ConfigureAwait(false)
                ? new StreamStep(stream.Current, false)
                : new StreamStep(null, false);
        }
        catch (Exception exception) when (ProviderRetryMechanics.IsClassifiable(exception, cancellationToken))
        {
            await this.ThrowOrWaitAsync(exception, attempt, delivered, cancellationToken).ConfigureAwait(false);
            return new StreamStep(null, true);
        }
    }

    /// <summary>
    ///     Reports the failure when no further attempt is allowed, and otherwise waits out the backoff so the
    ///     caller's loop can make the next one.
    /// </summary>
    /// <param name="exception">The failure the attempt threw.</param>
    /// <param name="attempt">The attempt that failed, counted from one.</param>
    /// <param name="answerAlreadyStarted">Whether part of a streamed answer has already reached the caller.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    private async Task ThrowOrWaitAsync(
        Exception exception,
        int attempt,
        bool answerAlreadyStarted,
        CancellationToken cancellationToken)
    {
        var verdict = this._classify(exception);
        if (!ProviderRetryMechanics.ShouldRetry(this._policy, verdict, attempt, answerAlreadyStarted))
        {
            throw this.Fail(exception, verdict, attempt);
        }

        await this.WaitBeforeRetryAsync(verdict, attempt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One step of a streaming attempt: an update to hand on, or the reason there is none.</summary>
    /// <param name="Update">The update read, or <see langword="null" /> when this attempt produced no more.</param>
    /// <param name="Retry">Whether the attempt ended in a failure that another attempt may get past.</param>
    private readonly record struct StreamStep(ChatResponseUpdate? Update, bool Retry);

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
