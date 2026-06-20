// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.CodeAnalysis;

/// <summary>
///     Whether a reference/definition result was resolved by name (the syntactic floor every
///     backend meets) or by semantic binding.
/// </summary>
public enum ResolutionMode
{
    /// <summary>Name-based (syntactic) match.</summary>
    NameBased,

    /// <summary>Semantically resolved (binding).</summary>
    Semantic,
}
