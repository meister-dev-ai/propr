// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.CodeAnalysis;

/// <summary>
///     A definition discovered by the structural analyzer. Line spans are 1-based and inclusive.
/// </summary>
/// <param name="Kind">Coarse <see cref="DefinitionKind" /> for the enclosing node.</param>
/// <param name="Name">The definition name when discoverable, or <c>null</c> for anonymous nodes.</param>
/// <param name="StartLine">1-based inclusive start line of the definition.</param>
/// <param name="EndLine">1-based inclusive end line of the definition.</param>
/// <param name="TotalLines">Total line count of the parsed file (for trace context).</param>
public sealed record EnclosingDefinition(
    DefinitionKind Kind,
    string? Name,
    int StartLine,
    int EndLine,
    int TotalLines);
