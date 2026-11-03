// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Transport;

/// <summary>A model as an OpenAI-shaped listing names it.</summary>
/// <param name="Id">The identifier.</param>
/// <param name="Mode">
///     What the model is for, where the listing says so, such as <c>chat</c> or <c>embedding</c>. A LiteLLM gateway
///     states it; most endpoints do not.
/// </param>
/// <param name="MaxInputTokens">The input limit the listing states, or null without one.</param>
public sealed record ProviderListedModel(string Id, string? Mode = null, int? MaxInputTokens = null);

/// <summary>
///     Reads the models out of an OpenAI-shaped model listing.
/// </summary>
/// <remarks>
///     The body is whatever the endpoint sent. A gateway that answers 200 with an error page, a proxy that
///     truncates, or a provider that reports a numeric identifier all reach this. Every one of those raised out
///     of discovery before, and the operator saw an internal error naming nothing to act on. Reported as a
///     refusal instead, which is how the caller already reports a body it could not use.
/// </remarks>
public static class ProviderModelListJson
{
    /// <summary>
    ///     Reads the identifiers, or reports that the body is not a model listing this host can read.
    /// </summary>
    /// <param name="payload">The response body.</param>
    /// <param name="modelIds">The identifiers found, sorted and de-duplicated.</param>
    /// <returns><see langword="true" /> when the body was read, <see langword="false" /> when it is malformed.</returns>
    public static bool TryReadModelIds(string? payload, [NotNullWhen(true)] out IReadOnlyList<string>? modelIds)
    {
        modelIds = TryReadModels(payload, out var models) ? [.. models.Select(model => model.Id)] : null;
        return modelIds is not null;
    }

    /// <summary>
    ///     Reads the models with what the listing says about them, or reports that the body is not a model listing
    ///     this host can read.
    /// </summary>
    /// <param name="payload">The response body.</param>
    /// <param name="models">The models found, sorted and de-duplicated by identifier; the first entry of a duplicate is kept.</param>
    /// <returns><see langword="true" /> when the body was read, <see langword="false" /> when it is malformed.</returns>
    public static bool TryReadModels(string? payload, [NotNullWhen(true)] out IReadOnlyList<ProviderListedModel>? models)
    {
        models = null;
        if (string.IsNullOrWhiteSpace(payload))
        {
            models = [];
            return true;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            // The root has to be an object before it can be asked for a property. TryGetProperty throws on a
            // root that is a literal, an array or a string, and a body shaped that way is one this reader cannot
            // read, which is the answer it is documented to give.
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                models = [];
                return true;
            }

            var found = new List<ProviderListedModel>();
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // A non-string identifier is skipped and not read. GetString raises on any other kind, and one
                // odd entry is not a reason to refuse a listing the rest of which is usable. The same holds for
                // a mode or an input limit of another kind: the model is still listed, without that fact.
                if (entry.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && id.GetString() is { } value
                    && !string.IsNullOrWhiteSpace(value))
                {
                    found.Add(new ProviderListedModel(value, Text(entry, "mode"), PositiveInteger(entry, "max_input_tokens")));
                }
            }

            models =
            [
                .. found
                    .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase),
            ];
            return true;
        }
    }

    /// <summary>
    ///     What a listed model can serve: the mode the listing states, and otherwise what its identifier says. A
    ///     stated mode other than chat or embedding, such as image generation, speech or reranking, serves no
    ///     purpose of a review, so the model is not offered.
    /// </summary>
    /// <param name="model">The listed model.</param>
    /// <returns>The operation, or null for a model that serves neither chat nor embeddings.</returns>
    public static AiOperationKind? OperationOf(ProviderListedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Mode?.ToLowerInvariant() switch
        {
            null => model.Id.Contains("embedding", StringComparison.OrdinalIgnoreCase)
                ? AiOperationKind.Embedding
                : AiOperationKind.Chat,
            "embedding" => AiOperationKind.Embedding,
            "chat" or "completion" or "responses" => AiOperationKind.Chat,
            _ => null,
        };
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                                                  && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static int? PositiveInteger(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                                                  && value.TryGetInt32(out var number) && number > 0
            ? number
            : null;
}
