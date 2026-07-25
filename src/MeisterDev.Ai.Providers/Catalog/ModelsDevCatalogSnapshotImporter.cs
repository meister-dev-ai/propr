// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text.Json;

namespace MeisterDev.Ai.Providers.Catalog;

/// <summary>
///     Imports a models.dev snapshot: a JSON object keyed by provider id, each provider carrying a
///     <c>models</c> object keyed by model id. Costs in that source are already denominated per million USD,
///     which is the unit used throughout, so no conversion is applied and no rounding is introduced.
/// </summary>
public sealed class ModelsDevCatalogSnapshotImporter : ICatalogSnapshotImporter
{
    /// <inheritdoc />
    public string SourceFormat => "models.dev";

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProviderCatalogEntry>> ImportAsync(Stream snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        using var document = await JsonDocument.ParseAsync(snapshot, cancellationToken: ct).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var entries = new List<ProviderCatalogEntry>();
        foreach (var provider in document.RootElement.EnumerateObject())
        {
            if (provider.Value.ValueKind != JsonValueKind.Object
                || !provider.Value.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var providerId = String(provider.Value, "id") ?? provider.Name;
            var providerName = String(provider.Value, "name") ?? providerId;

            foreach (var model in models.EnumerateObject())
            {
                var entry = TryReadModel(providerId, providerName, model);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
        }

        return entries;
    }

    private static ProviderCatalogEntry? TryReadModel(string providerId, string providerName, JsonProperty model)
    {
        if (model.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var remoteModelId = String(model.Value, "id") ?? model.Name;
        if (string.IsNullOrWhiteSpace(remoteModelId))
        {
            return null;
        }

        var cost = Object(model.Value, "cost");
        var limit = Object(model.Value, "limit");

        var cachedInput = Decimal(cost, "cache_read");
        var cacheWrite = Decimal(cost, "cache_write");

        return new ProviderCatalogEntry(
            providerId,
            providerName,
            remoteModelId,
            String(model.Value, "name") ?? remoteModelId,
            String(model.Value, "family"),
            Bool(model.Value, "tool_call"),
            Bool(model.Value, "structured_output"),
            Bool(model.Value, "reasoning"),
            // The source has no "supports caching" flag; a stated cache price is the only dependable signal
            // that caching is billable, and a zero price is still a statement that the path exists.
            cachedInput.HasValue || cacheWrite.HasValue,
            // A model that interleaves reasoning names the field it needs echoed back on assistant turns.
            String(Object(model.Value, "interleaved"), "field"),
            Int(limit, "context"),
            Int(limit, "output"),
            Decimal(cost, "input"),
            Decimal(cost, "output"),
            cachedInput,
            cacheWrite,
            Bool(model.Value, "open_weights"),
            Date(model.Value, "release_date"));
    }

    private static JsonElement? Object(JsonElement? parent, string name)
    {
        return parent is { } element
               && element.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.Object
            ? value
            : null;
    }

    private static string? String(JsonElement? parent, string name)
    {
        return parent is { } element
               && element.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool Bool(JsonElement? parent, string name)
    {
        return parent is { } element
               && element.TryGetProperty(name, out var value)
               && value.ValueKind is JsonValueKind.True or JsonValueKind.False
               && value.GetBoolean();
    }

    private static int? Int(JsonElement? parent, string name)
    {
        return parent is { } element
               && element.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
    }

    private static decimal? Decimal(JsonElement? parent, string name)
    {
        return parent is { } element
               && element.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetDecimal(out var parsed)
            ? parsed
            : null;
    }

    private static DateOnly? Date(JsonElement? parent, string name)
    {
        var raw = String(parent, name);
        return DateOnly.TryParse(raw, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}
