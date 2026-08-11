// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.Ai.Providers.Resilience;

/// <summary>
///     Waits on the connection's throttle gate before each attempt, and closes that gate for as long as the
///     provider asked when an attempt comes back throttled.
/// </summary>
/// <remarks>
///     <para>
///         A review runs several calls at once against one connection. Without a shared signal, each of them has
///         to be refused in its own right before it backs off, so an exhausted quota is hit once per caller and
///         the provider spends the whole window saying no. One refusal here is enough to hold the rest.
///     </para>
///     <para>
///         Nothing is retried or rewritten here. The failure is passed on exactly as it arrived, and the retry
///         stage above decides what to do with it, so the attempt budget has one owner.
///     </para>
///     <para>
///         The gate closes only for a delay the provider actually stated. An exponential guess is a guess about
///         the one call that made it, and holding an entire fan-out on it would cost more time than the throttle
///         itself.
///     </para>
/// </remarks>
public sealed partial class ProviderPacingChatClient : DelegatingChatClient
{
    private readonly ProviderThrottleGate _gate;
    private readonly string _connectionKey;
    private readonly Func<Exception, ProviderFailureVerdict> _classify;
    private readonly TimeSpan _maxWindow;
    private readonly ILogger? _logger;

    /// <summary>Initializes a new instance of the <see cref="ProviderPacingChatClient" /> class.</summary>
    /// <param name="innerClient">The client whose calls are paced.</param>
    /// <param name="gate">The process-wide gate this connection's calls wait on.</param>
    /// <param name="connectionKey">Identifies the connection whose quota is shared, usually its id.</param>
    /// <param name="classify">The driver's classification of a failure; usually <c>driver.ClassifyRuntimeFailure</c>.</param>
    /// <param name="maxWindow">Ceiling for a stated delay, so an absurd one cannot park the fan-out.</param>
    /// <param name="logger">Optional logger for the moment a connection is found throttled.</param>
    public ProviderPacingChatClient(
        IChatClient innerClient,
        ProviderThrottleGate gate,
        string connectionKey,
        Func<Exception, ProviderFailureVerdict> classify,
        TimeSpan maxWindow,
        ILogger? logger = null)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentException.ThrowIfNullOrEmpty(connectionKey);
        ArgumentNullException.ThrowIfNull(classify);

        this._gate = gate;
        this._connectionKey = connectionKey;
        this._classify = classify;
        this._maxWindow = maxWindow;
        this._logger = logger;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await this._gate.WaitAsync(this._connectionKey, cancellationToken).ConfigureAwait(false);

        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            this.TryNoteThrottle(exception, cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await this._gate.WaitAsync(this._connectionKey, cancellationToken).ConfigureAwait(false);

        IAsyncEnumerator<ChatResponseUpdate> stream;
        try
        {
            stream = base.GetStreamingResponseAsync(messages, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception exception)
        {
            // A client that refuses before handing back an enumerator never reaches MoveNextAsync, so the one
            // refusal the gate exists to act on would go unseen. There is nothing to dispose on this path.
            this.TryNoteThrottle(exception, cancellationToken);
            throw;
        }

        // The attempt's own failure is held rather than thrown from inside the loop, so that disposing the
        // enumerator cannot get in front of it. Disposal has to happen either way, and a provider that fails the
        // read and then fails the cleanup would otherwise hand the retry stage the cleanup error to judge.
        ExceptionDispatchInfo? failure = null;

        try
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await stream.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    update = stream.Current;
                }
                catch (Exception exception)
                {
                    this.TryNoteThrottle(exception, cancellationToken);
                    failure = ExceptionDispatchInfo.Capture(exception);
                    break;
                }

                yield return update;
            }
        }
        finally
        {
            await DisposeAsync(stream, failure is not null).ConfigureAwait(false);
        }

        failure?.Throw();
    }

    /// <summary>
    ///     Disposes the attempt's enumerator, keeping a disposal failure to itself only when the attempt has a
    ///     failure of its own to report. A refusal replaced by a cleanup error is a refusal the retry stage cannot
    ///     recognise, and an attempt that read cleanly still has to hear about a cleanup that did not.
    /// </summary>
    /// <param name="stream">The enumerator to dispose.</param>
    /// <param name="attemptFailed">Whether the read already failed and that failure is the one being reported.</param>
    private static async ValueTask DisposeAsync(IAsyncEnumerator<ChatResponseUpdate> stream, bool attemptFailed)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception) when (attemptFailed)
        {
            // Deliberately swallowed; the caller rethrows the read failure this disposal came after.
        }
    }

    /// <summary>
    ///     Notes a throttle without letting the attempt to note it disturb the failure on its way up. The
    ///     classifier is the driver's own code, and a gate that stays open costs one wasted request while a lost
    ///     exception costs the retry stage the only thing it has to judge.
    /// </summary>
    /// <param name="exception">The failure the attempt threw.</param>
    /// <param name="cancellationToken">The caller's token, used to tell a real cancellation from a timeout.</param>
    private void TryNoteThrottle(Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            this.NoteThrottle(exception, cancellationToken);
        }
        catch (Exception)
        {
            // Deliberately swallowed; the caller rethrows the failure this was called about.
        }
    }

    /// <summary>
    ///     Closes the connection's gate when the failure was the provider refusing for want of quota and it said
    ///     how long to wait. Cancellation and product refusals riding it are left alone, as everywhere else.
    /// </summary>
    /// <param name="exception">The failure the attempt threw.</param>
    /// <param name="cancellationToken">The caller's token, used to tell a real cancellation from a timeout.</param>
    private void NoteThrottle(Exception exception, CancellationToken cancellationToken)
    {
        if (!ProviderRetryMechanics.IsClassifiable(exception, cancellationToken))
        {
            return;
        }

        var verdict = this._classify(exception);
        if (!verdict.IsThrottled || verdict.RetryAfter is not { } stated)
        {
            return;
        }

        var window = stated < this._maxWindow ? stated : this._maxWindow;
        this._gate.CloseFor(this._connectionKey, window);

        if (this._logger is not null)
        {
            LogConnectionThrottled(this._logger, this._connectionKey, window.TotalSeconds, verdict.Reason);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "AI connection {Connection} is throttled ({Reason}); further calls to it wait {WindowSeconds:0.##}s.")]
    private static partial void LogConnectionThrottled(
        ILogger logger,
        string connection,
        double windowSeconds,
        string reason);
}
