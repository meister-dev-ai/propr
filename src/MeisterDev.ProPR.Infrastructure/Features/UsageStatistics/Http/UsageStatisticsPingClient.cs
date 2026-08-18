// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Support;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.UsageStatistics.Http;

/// <summary>
///     Posts one snapshot to the vendor receiver and reads the response.
///     <para>
///         A failed send is not retried, queued or persisted. A snapshot that does not arrive is discarded and
///         the next daily cycle builds a fresh one, so an unreachable receiver accumulates no pending work.
///     </para>
/// </summary>
public sealed partial class UsageStatisticsPingClient(
    HttpClient httpClient,
    TimeProvider timeProvider,
    ILogger<UsageStatisticsPingClient> logger) : IUsageStatisticsPingClient
{
    /// <summary>
    ///     How much of the response is read and allocated.
    ///     <para>
    ///         The response is bounded even though the receiver is the vendor's, because an unbounded response
    ///         would allocate without limit on the installation's own host. Two things enforce it: the request
    ///         completes on headers rather than on content, and the reader stops at this many bytes. The
    ///         HttpClient's own MaxResponseContentBufferSize is set to the same value as a backstop.
    ///     </para>
    /// </summary>
    internal const int MaxResponseBytes = 64 * 1024;

    /// <inheritdoc />
    public async Task<UsageStatisticsSendOutcome> SendAsync(
        UsageStatisticsSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var attemptedAt = timeProvider.GetUtcNow();

        try
        {
            using var content = new StringContent(
                UsageStatisticsContract.Serialize(snapshot),
                Encoding.UTF8,
                new MediaTypeHeaderValue("application/json"));

            using var request = new HttpRequestMessage(HttpMethod.Post, UsageStatisticsContract.PingEndpoint)
            {
                Content = content,
            };

            // ResponseHeadersRead is what makes MaxResponseBytes a bound. The default for PostAsync,
            // HttpCompletionOption.ResponseContentRead, buffers the entire body into managed memory before
            // returning, which would leave the cap in ReadBoundedAsync limiting only what is parsed.
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                LogPingRejected(logger, (int)response.StatusCode);
                return new UsageStatisticsSendOutcome(
                    attemptedAt,
                    false,
                    $"The receiver answered {(int)response.StatusCode}.",
                    null);
            }

            var payload = await ReadBoundedAsync(response, cancellationToken);
            LogPingDelivered(logger);

            return new UsageStatisticsSendOutcome(attemptedAt, true, "Delivered.", Parse(payload));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // The transport and parse failures a send can be expected to hit. Anything outside this set is a defect
        // rather than an unreachable receiver, so it propagates to the worker, which logs it as an error
        // instead of recording it as a failed delivery.
        catch (Exception exception) when (exception is HttpRequestException
                                              or TaskCanceledException
                                              or TimeoutException
                                              or IOException
                                              or SocketException
                                              or JsonException)
        {
            LogPingFailed(logger, exception);
            return new UsageStatisticsSendOutcome(attemptedAt, false, Describe(exception), null);
        }
    }

    /// <summary>
    ///     Maps an exception to a short operator-facing line for the settings page.
    ///     <para>
    ///         The message is generic because a transport error can carry a proxy's response text, which does
    ///         not belong on the settings page.
    ///     </para>
    /// </summary>
    private static string Describe(Exception exception)
    {
        return exception switch
        {
            TaskCanceledException => "The receiver did not answer in time.",
            HttpRequestException => "The receiver could not be reached.",
            _ => "The send did not complete.",
        };
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[MaxResponseBytes];
        var read = 0;

        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (chunk == 0)
            {
                break;
            }

            read += chunk;
        }

        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>Reads the response, treating anything unreadable as no response.</summary>
    private static UsageStatisticsPingResponse? Parse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<UsageStatisticsPingResponse>(
                payload,
                UsageStatisticsContract.SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
