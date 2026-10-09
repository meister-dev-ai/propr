// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Bounds metadata requests and preserves unavailable or incomplete provider collections.</summary>
internal sealed class ReviewOverviewReadSession
{
    internal const int MaxRequests = 12;
    internal const int MaxResponseBytes = 2 * 1024 * 1024;
    private int _requests;

    internal readonly record struct Pagination(bool? HasMore = null, int? TotalCount = null);

    internal sealed record Collection(IReadOnlyList<JsonElement> Items, bool Complete);

    public bool TryConsume(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return this._requests++ < MaxRequests;
    }

    public async Task<Collection> ReadPagesAsync(
        Func<HttpResponseMessage, Pagination> readPagination,
        Func<int, CancellationToken, Task<HttpResponseMessage>> requestPage, CancellationToken ct,
        Action<HttpResponseMessage>? classifyResponse = null)
    {
        var items = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= ProviderRestPager.MaxPages; page++)
        {
            if (!this.TryConsume(ct))
            {
                return new(items, false);
            }

            using var response = await requestPage(page, ct).ConfigureAwait(false);
            var payload = await ReadJsonAsync(response, ct, classifyResponse).ConfigureAwait(false);
            if (payload is not { ValueKind: JsonValueKind.Array } root || root.GetArrayLength() > ProviderRestPager.PageSize)
            {
                return new(items, false);
            }

            var pagination = readPagination(response);
            var hasMore = pagination.HasMore;
            var total = pagination.TotalCount;
            if (root.GetArrayLength() == 0)
            {
                return new(items, hasMore != true && (total is null || items.Count >= total));
            }

            var added = 0;
            foreach (var item in root.EnumerateArray())
            {
                if (Identifier(item) is not { } id)
                {
                    return new(items, false);
                }

                if (!seen.Add(id))
                {
                    return new(items, false);
                }

                items.Add(item.Clone());
                added++;
            }

            if (added == 0)
            {
                return new(items, false);
            }

            if (total is { } count)
            {
                if (items.Count >= count)
                {
                    return new(items, true);
                }
            }
            else if (hasMore == false || hasMore is null && root.GetArrayLength() < ProviderRestPager.PageSize)
            {
                return new(items, true);
            }
        }

        return new(items, false);
    }

    public static async Task<JsonElement?> ReadJsonAsync(
        HttpResponseMessage response, CancellationToken ct, Action<HttpResponseMessage>? classifyResponse = null)
    {
        classifyResponse?.Invoke(response);
        ProviderReadFailures.ThrowIfDeniedOrThrottled(response);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The provider could not return pull request metadata.");
        }

        try
        {
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (bounded.Length + read > MaxResponseBytes)
                {
                    return null;
                }

                await bounded.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            bounded.Position = 0;
            using var document = await JsonDocument.ParseAsync(bounded, cancellationToken: ct).ConfigureAwait(false);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonElement? Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) ? result : null;

    public static string? Identifier(JsonElement value)
    {
        var id = Property(value, "id");
        return id?.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(id.Value.GetString()) ? null : id.Value.GetString(),
            JsonValueKind.Number => id.Value.TryGetInt64(out var number) && number > 0
                ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null,
            _ => null,
        };
    }

    public static bool? Boolean(JsonElement value, string name) =>
        Property(value, name)?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };

    public static Uri ScopedUri(Uri providerUri, string scopePath, string builderBaseUrl)
    {
        var prefix = new Uri(scopePath).AbsolutePath.TrimEnd('/');
        var builderPrefix = new Uri(builderBaseUrl).AbsolutePath.TrimEnd('/');
        if (string.Equals(prefix, builderPrefix, StringComparison.Ordinal))
        {
            return providerUri;
        }

        var path = providerUri.AbsolutePath;
        if (builderPrefix.Length > 0)
        {
            if (!path.StartsWith(builderPrefix + "/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The provider could not return pull request metadata.");
            }

            path = path[builderPrefix.Length..];
        }

        return new UriBuilder(providerUri)
        {
            Path = prefix + path,
        }.Uri;
    }
}
