// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.CodeAnalysis;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

internal static class FallbackReasonTraceExtensions
{
    private static readonly IReadOnlyDictionary<FallbackReason, string> TraceStrings =
        new Dictionary<FallbackReason, string>
        {
            [FallbackReason.AnalyzerDisabled] = "analyzer_disabled",
            [FallbackReason.NativeUnavailable] = "native_unavailable",
            [FallbackReason.UnsupportedLanguage] = "unsupported_language",
            [FallbackReason.ParseFault] = "parse_fault",
            [FallbackReason.ParseTimeout] = "parse_timeout",
            [FallbackReason.FileTooLarge] = "file_too_large",
            [FallbackReason.NoEnclosingDefinition] = "no_enclosing_definition",
        };

    /// <summary>Lowercase snake_case trace string for <c>fallbackReason</c> per contracts/trace-event.md.</summary>
    public static string ToTraceString(this FallbackReason reason)
    {
        return TraceStrings.TryGetValue(reason, out var s) ? s : reason.ToString().ToLowerInvariant();
    }

    /// <summary>Lowercase trace string for <c>enclosingKind</c> per contracts/trace-event.md.</summary>
    public static string ToTraceString(this DefinitionKind kind)
    {
        return kind.ToString().ToLowerInvariant();
    }
}
