// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

// The Responses API of the OpenAI library is marked for evaluation only.
#pragma warning disable OPENAI001

namespace MeisterDev.Ai.Providers.OpenAiAddIn;

/// <summary>
///     Usage counts of the Responses API that the OpenAI library has no property for, carried to the driver in
///     <see cref="UsageDetails.AdditionalCounts" /> under the vendor's field name.
/// </summary>
/// <remarks>
///     From GPT-5.6 on, OpenAI bills the input that a call writes to the prompt cache at 1.25 times the input rate
///     for the default 30-minute lifetime, and reports it as <c>usage.input_tokens_details.cache_write_tokens</c>.
///     The OpenAI library this add-in loads does not read that field, so the adapter of Microsoft.Extensions.AI
///     builds a usage without it, and the cache writes would be billed at the input rate. The library keeps a field
///     it has no property for on the model and writes it back out when the model is serialized, so the count is
///     read from the serialized details of the input.
/// </remarks>
internal static class OpenAiUsageCounters
{
    /// <summary>
    ///     The name of the count of input tokens written to the prompt cache, which is the name of the field in
    ///     the vendor's usage.
    /// </summary>
    public const string CacheWrite = "cache_write_tokens";

    /// <summary>
    ///     Adds the cache-write count of a Responses API usage to the usage the adapter built from the same response.
    /// </summary>
    /// <param name="usage">The usage the adapter built, or null when the response carried none.</param>
    /// <param name="reported">The usage of the Responses API, as the OpenAI library read it.</param>
    public static void AddCacheWrite(UsageDetails? usage, ResponseTokenUsage? reported)
    {
        if (usage is null || reported?.InputTokenDetails is not { } details || ReadCacheWrite(details) is not { } written)
        {
            return;
        }

        usage.AdditionalCounts ??= [];
        usage.AdditionalCounts[CacheWrite] = written;
    }

    /// <summary>
    ///     Adds the cache-write count of a streamed response to the usage update that the adapter built from the final
    ///     event of the stream.
    /// </summary>
    /// <param name="update">An update of the stream.</param>
    public static void AddCacheWrite(ChatResponseUpdate update)
    {
        var final = update.RawRepresentation switch
        {
            StreamingResponseCompletedUpdate completed => completed.Response,
            StreamingResponseIncompleteUpdate incomplete => incomplete.Response,
            StreamingResponseFailedUpdate failed => failed.Response,
            _ => null,
        };

        if (final?.Usage is not { } reported)
        {
            return;
        }

        foreach (var content in update.Contents.OfType<UsageContent>())
        {
            AddCacheWrite(content.Details, reported);
        }
    }

    /// <summary>The cache-write count in the input details, or null when the response does not report one.</summary>
    /// <param name="details">The input details of the usage.</param>
    private static long? ReadCacheWrite(ResponseInputTokenUsageDetails details)
    {
        using var json = JsonDocument.Parse(ModelReaderWriter.Write(details));

        return json.RootElement.TryGetProperty(CacheWrite, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt64(out var count)
               && count >= 0
            ? count
            : null;
    }
}
