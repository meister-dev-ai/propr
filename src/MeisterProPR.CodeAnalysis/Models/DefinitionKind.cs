// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.CodeAnalysis;

/// <summary>
///     Coarse definition kind reported by the structural analyzer. Captures the
///     review-relevant category of an enclosing node, not the grammar's exact
///     node type, so the prefetch trace stays language-agnostic.
/// </summary>
public enum DefinitionKind
{
    Function,
    Method,
    Class,
    Interface,
    Module,
    Enum,
    Other,
}
