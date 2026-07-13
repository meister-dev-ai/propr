// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using TS = TreeSitter;

namespace MeisterProPR.CodeAnalysis.TreeSitter.Parsing;

/// <summary>
///     Outcome of a pooled parse attempt. <see cref="Tree" /> is non-null on success;
///     <see cref="FallbackReason" /> is set when the analyzer must fall back to the heuristic.
/// </summary>
internal sealed record PooledParseResult(TS.Tree? Tree, FallbackReason? FallbackReason);

/// <summary>
///     Bounded, thread-safe gate around native Tree-sitter parsing (R2/R8).
/// </summary>
/// <remarks>
///     <para>
///         <b>Concurrency model:</b> a <see cref="SemaphoreSlim" /> bounds concurrent parses to
///         <c>MaxFileReviewConcurrency</c> so the worker never spawns unbounded native allocations.
///         Within a parse, the <c>TreeSitter.Parser</c> / <c>TreeSitter.Tree</c> are
///         used by exactly one thread and disposed deterministically.
///     </para>
/// </remarks>
internal sealed class ParserPool : IDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly int _maxParseBytes;
    private readonly int _parseTimeoutMs;
    private int _isDisposed;

    public ParserPool(int maxConcurrency, int maxParseBytes, int parseTimeoutMs)
    {
        if (maxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "maxConcurrency must be at least 1.");
        }

        this._maxParseBytes = maxParseBytes;
        this._parseTimeoutMs = parseTimeoutMs;
        this._gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref this._isDisposed, 1, 0) != 0)
        {
            return;
        }

        this._gate.Dispose();
    }

    /// <summary>
    ///     Parses <paramref name="source" /> under the size guard and wall-clock timeout.
    ///     Never throws for parse/timeout problems - returns a fallback reason instead.
    /// </summary>
    public async Task<PooledParseResult> ParseAsync(
        SupportedLanguage language,
        string source,
        CancellationToken ct)
    {
        if (Volatile.Read(ref this._isDisposed) == 1)
        {
            return new PooledParseResult(null, FallbackReason.NativeUnavailable);
        }

        if (string.IsNullOrEmpty(source))
        {
            return new PooledParseResult(null, FallbackReason.ParseFault);
        }

        // Pre-parse size guard. Use the UTF-8 byte count because that is what
        // the native parser ultimately receives; the budget is specified in bytes.
        int byteCount;
        try
        {
            byteCount = Encoding.UTF8.GetByteCount(source);
        }
        catch
        {
            return new PooledParseResult(null, FallbackReason.ParseFault);
        }

        if (byteCount > this._maxParseBytes)
        {
            return new PooledParseResult(null, FallbackReason.FileTooLarge);
        }

        var nativeLanguage = LanguageRegistry.TryGetLanguage(language);
        if (nativeLanguage is null)
        {
            return new PooledParseResult(null, FallbackReason.NativeUnavailable);
        }

        try
        {
            await this._gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new PooledParseResult(null, FallbackReason.ParseFault);
        }

        // From here, exactly one owner releases the gate slot acquired above:
        //   - the construction-failure path below releases it inline, or
        //   - ParseWithTimeoutAsync, which takes ownership of both the parser and the
        //     gate slot for its success AND timeout paths. On timeout it hands that
        //     ownership to a continuation that disposes the parser and releases the gate
        //     only after the in-flight native parse has actually finished.
        // ParseAsync MUST NOT dispose the parser or release the gate after handing off:
        // doing so on the timeout path would free the parser while ts_parser_parse is
        // still running on the parse thread (native double-free / subtree refcount
        // corruption) and over-release the semaphore.
        TS.Parser parser;
        try
        {
            parser = new TS.Parser(nativeLanguage);
        }
        catch
        {
            this._gate.Release();
            return new PooledParseResult(null, FallbackReason.NativeUnavailable);
        }

        return await this.ParseWithTimeoutAsync(parser, source).ConfigureAwait(false);
    }

    /// <summary>
    ///     Primes the native parse path once, best-effort, on a background thread: loads each
    ///     supported grammar and runs a trivial parse so the managed JIT and per-language native
    ///     initialization are paid up front rather than inside the deadline of the first real parse
    ///     (the production budget is only <c>StructuralParseTimeoutMs</c>). Never throws; if native
    ///     libraries are unavailable each attempt simply falls back to a no-op.
    /// </summary>
    public void WarmUp()
    {
        _ = Task.Run(async () =>
        {
            foreach (var language in Enum.GetValues<SupportedLanguage>())
            {
                try
                {
                    var result = await this.ParseAsync(language, "\n", CancellationToken.None).ConfigureAwait(false);
                    result.Tree?.Dispose();
                }
                catch
                {
                    // Warm-up is best-effort; a failure just means the first real parse pays the cost.
                }
            }
        });
    }

    private async Task<PooledParseResult> ParseWithTimeoutAsync(TS.Parser parser, string source)
    {
        // Ownership of `parser` and the gate slot transfers into this method. Either we
        // dispose+release inline (success) or a continuation disposes+releases (timeout).
        TS.Tree? tree = null;

        // Run the native parse on a DEDICATED thread (LongRunning), NOT the shared thread pool.
        // The wall-clock deadline below would otherwise count thread-pool *scheduling* latency:
        // under load (e.g. many files reviewed concurrently, or a parallel test run) the pool can be
        // saturated, so a pool-scheduled parse delegate waits in the queue while Task.Delay fires on the
        // timer regardless — producing spurious ParseTimeout fallbacks even though the parse itself
        // is fast. A dedicated thread starts immediately, so the timeout measures parse work rather
        // than queueing. Concurrency is already bounded by the gate, so at most maxConcurrency of
        // these threads exist at once.
        var parseTask = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    return parser.Parse(source);
                }
                catch
                {
                    // Contain any native/managed parse fault (FR-010). Caller falls back to heuristic.
                    return (TS.Tree?)null;
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

        using var timeoutCts = new CancellationTokenSource(this._parseTimeoutMs);
        var timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);

        var winner = await Task.WhenAny(parseTask, timeoutTask).ConfigureAwait(false);

        if (winner == parseTask)
        {
            await timeoutCts.CancelAsync().ConfigureAwait(false);
            try
            {
                tree = await parseTask.ConfigureAwait(false);
            }
            catch
            {
                // Should not happen — the StartNew wrapper swallows. Defensive.
            }

            try
            {
                parser.Dispose();
            }
            catch
            {
                /* best-effort */
            }

            this._gate.Release();

            return tree is null
                ? new PooledParseResult(null, FallbackReason.ParseFault)
                : new PooledParseResult(tree, null);
        }

        // Timeout: abandon the in-flight native parse. The parser is NOT disposed here
        // because the native call may still be reading it; the continuation disposes it
        // (and releases the gate) once the parse actually completes. The size guard
        // bounds the practical worst case so this continuation runs promptly.
        var abandonedParser = parser;
        _ = parseTask.ContinueWith(
            t =>
            {
                try
                {
                    t.Result?.Dispose();
                }
                catch
                {
                    /* best-effort */
                }

                try
                {
                    abandonedParser.Dispose();
                }
                catch
                {
                    /* best-effort */
                }

                this._gate.Release();
            },
            TaskScheduler.Default);

        return new PooledParseResult(null, FallbackReason.ParseTimeout);
    }
}
