// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure;
using MeisterProPR.Application.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Wraps an <see cref="IChatClient" /> and transparently retries requests that fail
///     with a rate-limit response (HTTP 429 / <see cref="RequestFailedException" /> with Status 429)
///     using exponential backoff. All other exceptions are propagated immediately.
/// </summary>
public sealed partial class ResilientChatClientDecorator(
    IChatClient inner,
    IOptions<AiReviewOptions> aiOptions,
    ILogger<ResilientChatClientDecorator> logger) : IChatClient
{
    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = aiOptions.Value;
        var attempt = 0;
        // Materialise once so the enumerable can be replayed on retries.
        var messageList = messages is IList<ChatMessage> l ? l : messages.ToList();

        while (true)
        {
            try
            {
                return await inner.GetResponseAsync(messageList, options, cancellationToken);
            }
            catch (RequestFailedException rfe) when (rfe.Status == 429 && attempt < opts.MaxRateLimitRetries)
            {
                attempt++;
                var delaySeconds = Math.Min(Math.Pow(2, attempt), opts.MaxBackoffSeconds);
                LogRateLimitRetry(logger, attempt, opts.MaxRateLimitRetries, (int)delaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => inner.GetStreamingResponseAsync(messages, options, cancellationToken);

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? key = null)
        => inner.GetService(serviceType, key);

    /// <inheritdoc />
    public TService? GetService<TService>(object? key = null) where TService : class
        => inner.GetService<TService>(key);

    /// <inheritdoc />
    public void Dispose() => inner.Dispose();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "AI request rate-limited (429). Retry {Attempt}/{MaxRetries} after {DelaySeconds}s backoff.")]
    private static partial void LogRateLimitRetry(ILogger logger, int attempt, int maxRetries, int delaySeconds);
}
