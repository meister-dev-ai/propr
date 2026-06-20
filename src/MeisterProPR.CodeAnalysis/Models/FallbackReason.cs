// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.CodeAnalysis;

/// <summary>
///     Why the structural analyzer could not produce a boundary-resolved context.
///     Surfaced on the extended <c>context_prefetch_applied</c> trace event
///     and on observability counters.
/// </summary>
public enum FallbackReason
{
    /// <summary>Kill-switch <c>EnableStructuralBoundaryResolution</c> disabled the feature.</summary>
    AnalyzerDisabled,

    /// <summary>The native parser failed to load at startup.</summary>
    NativeUnavailable,

    /// <summary>The file extension does not map to a <see cref="SupportedLanguage" />.</summary>
    UnsupportedLanguage,

    /// <summary>A parse exception was caught and contained.</summary>
    ParseFault,

    /// <summary>The parse exceeded <c>StructuralParseTimeoutMs</c>.</summary>
    ParseTimeout,

    /// <summary>The source exceeded <c>MaxStructuralParseBytes</c>.</summary>
    FileTooLarge,

    /// <summary>The change sat outside any definition (e.g. imports/preamble).</summary>
    NoEnclosingDefinition,
}
