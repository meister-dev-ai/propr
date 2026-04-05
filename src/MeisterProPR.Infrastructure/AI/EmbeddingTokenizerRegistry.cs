// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using SharpToken;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Central registry for supported embedding tokenizer families and token counting.
/// </summary>
public static class EmbeddingTokenizerRegistry
{
    private static readonly string[] SupportedTokenizerNames =
    [
        "cl100k_base",
        "o200k_base",
        "o200k_harmony",
        "r50k_base",
        "p50k_base",
        "p50k_edit",
        "claude",
    ];

    private static readonly ConcurrentDictionary<string, GptEncoding> Encodings =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Returns all supported tokenizer names.
    /// </summary>
    public static IReadOnlyList<string> GetSupportedTokenizerNames() => SupportedTokenizerNames;

    /// <summary>
    ///     Returns whether the supplied tokenizer family is supported.
    /// </summary>
    public static bool IsSupported(string? tokenizerName)
    {
        return SupportedTokenizerNames.Any(name =>
            string.Equals(name, tokenizerName?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Counts tokens for one input using the configured tokenizer family.
    /// </summary>
    public static int CountTokens(string tokenizerName, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizerName);

        var normalizedTokenizer = tokenizerName.Trim();
        if (!IsSupported(normalizedTokenizer))
        {
            throw new NotSupportedException($"Unsupported tokenizer '{normalizedTokenizer}'.");
        }

        var encoding = Encodings.GetOrAdd(
            normalizedTokenizer,
            static name => GptEncoding.GetEncoding(name));

        return encoding.CountTokens(value ?? string.Empty);
    }
}
