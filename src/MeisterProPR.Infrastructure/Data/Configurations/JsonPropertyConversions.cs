// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal static class JsonPropertyConversions
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static readonly ValueConverter<Dictionary<string, string>, string> StringDictionaryConverter = new(
        value => JsonSerializer.Serialize(value ?? new Dictionary<string, string>(), SerializerOptions),
        value => string.IsNullOrWhiteSpace(value)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(value, SerializerOptions) ?? new Dictionary<string, string>());

    public static readonly ValueComparer<Dictionary<string, string>> StringDictionaryComparer = new(
        (left, right) => JsonSerializer.Serialize(left ?? new Dictionary<string, string>(), SerializerOptions)
                         == JsonSerializer.Serialize(right ?? new Dictionary<string, string>(), SerializerOptions),
        value => JsonSerializer.Serialize(value ?? new Dictionary<string, string>(), SerializerOptions).GetHashCode(),
        value => value == null
            ? new Dictionary<string, string>()
            : value.ToDictionary(pair => pair.Key, pair => pair.Value));

    public static readonly ValueConverter<Dictionary<string, string>?, string?> NullableStringDictionaryConverter = new(
        value => value == null ? null : JsonSerializer.Serialize(value, SerializerOptions),
        value => string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(value, SerializerOptions));

    public static readonly ValueComparer<Dictionary<string, string>?> NullableStringDictionaryComparer = new(
        (left, right) => JsonSerializer.Serialize(left ?? new Dictionary<string, string>(), SerializerOptions)
                         == JsonSerializer.Serialize(right ?? new Dictionary<string, string>(), SerializerOptions),
        value => JsonSerializer.Serialize(value ?? new Dictionary<string, string>(), SerializerOptions).GetHashCode(),
        value => value == null ? null : value.ToDictionary(pair => pair.Key, pair => pair.Value));

    public static readonly ValueConverter<string[], string> StringArrayConverter = new(
        value => JsonSerializer.Serialize(value ?? Array.Empty<string>(), SerializerOptions),
        value => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : JsonSerializer.Deserialize<string[]>(value, SerializerOptions) ?? Array.Empty<string>());

    public static readonly ValueComparer<string[]> StringArrayComparer = new(
        (left, right) => JsonSerializer.Serialize(left ?? Array.Empty<string>(), SerializerOptions)
                         == JsonSerializer.Serialize(right ?? Array.Empty<string>(), SerializerOptions),
        value => JsonSerializer.Serialize(value ?? Array.Empty<string>(), SerializerOptions).GetHashCode(),
        value => value == null ? Array.Empty<string>() : value.ToArray());
}
