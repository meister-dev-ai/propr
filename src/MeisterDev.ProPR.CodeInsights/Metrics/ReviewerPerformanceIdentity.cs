// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using System.Text;
using System.Text.Json;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

internal static class ReviewerPerformanceIdentity
{
    private const int MaximumProviderScopeCharacters = 1024;
    private const int MaximumRepositoryIdCharacters = 512;
    private const int MaximumModelIdCharacters = 256;
    private const int MaximumLogicalModelCharacters = 128;

    private const int GuidCharacters = 36;

    // An escaped UTF-16 code unit occupies six ASCII bytes as a JSON Unicode escape.
    private const int MaximumJsonBytesPerUtf16Character = 6;

    // Three strings use two brackets, six quotes and two commas; two strings use two brackets, four quotes and one comma.
    private const int RepositoryJsonFramingBytes = 10;
    private const int ModelJsonFramingBytes = 7;
    private const int Base64InputBytes = 3;
    private const int Base64OutputCharacters = 4;

    private const int RepositoryJsonBytes = MaximumJsonBytesPerUtf16Character * (MaximumProviderScopeCharacters + MaximumRepositoryIdCharacters)
                                            + GuidCharacters + RepositoryJsonFramingBytes;

    private const int ModelJsonBytes = MaximumJsonBytesPerUtf16Character * (MaximumModelIdCharacters + MaximumLogicalModelCharacters) + ModelJsonFramingBytes;

    // Base64 rounds each final partial three-byte input block up to four characters.
    internal const int MaximumRepositoryIdentityLength = Base64OutputCharacters * ((RepositoryJsonBytes + Base64InputBytes - 1) / Base64InputBytes);
    internal const int MaximumModelIdentityLength = Base64OutputCharacters * ((ModelJsonBytes + Base64InputBytes - 1) / Base64InputBytes);

    internal static RepositoryFallback? DecodeRepositoryFallback(string id)
    {
        try
        {
            var parts = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(Convert.FromBase64String(id)));
            return parts is { Length: 3 } ? new(parts[0], parts[2]) : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    public static string RepositoryKey(ReviewerPerformanceDailyCount row) => Encode([row.ClientId.ToString(), row.ProviderScope, row.RepositoryId]);
    public static string ModelKey(ReviewerPerformanceDailyCount row) => Encode([row.ModelId, row.LogicalModelName]);
    private static string Encode(string[] parts) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts)));

    internal readonly record struct RepositoryFallback(string? ClientId, string? RepositoryId);
}
