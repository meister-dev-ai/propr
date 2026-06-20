// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.CodeAnalysis.TreeSitter.Startup;

/// <summary>
///     Read-only availability surface the analyzer depends on, so the analyzer is
///     testable without constructing a real native probe (C8).
/// </summary>
internal interface IStructuralAnalyzerProbe
{
    /// <summary>
    ///     True when the native parser is loaded for the running platform. When false,
    ///     the analyzer returns empty for all calls and callers fall back to the heuristic.
    /// </summary>
    bool IsAvailable { get; }
}
