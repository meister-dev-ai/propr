// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace MeisterDev.Ai.Providers.Transport;

/// <summary>
///     Reads the model identifiers out of an OpenAI-shaped model listing.
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
        modelIds = null;

        if (string.IsNullOrWhiteSpace(payload))
        {
            modelIds = [];
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
                modelIds = [];
                return true;
            }

            var found = new List<string>();
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // A non-string identifier is skipped and not read. GetString raises on any other kind, and one
                // odd entry is not a reason to refuse a listing the rest of which is usable.
                if (entry.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && id.GetString() is { } value
                    && !string.IsNullOrWhiteSpace(value))
                {
                    found.Add(value);
                }
            }

            modelIds =
            [
                .. found
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
            ];
            return true;
        }
    }
}
