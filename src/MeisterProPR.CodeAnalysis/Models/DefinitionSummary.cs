// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.CodeAnalysis;

/// <summary>
///     A file's definition surfaced by the "list definitions" capability (US2). Line spans
///     are 1-based and inclusive.
/// </summary>
public sealed record DefinitionSummary(
    DefinitionKind Kind,
    string? Name,
    int StartLine,
    int EndLine);
